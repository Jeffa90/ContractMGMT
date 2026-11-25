using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public sealed record SaveReminderDays(string? Kind, int[]? Days);
public sealed record SaveReminderWording(string? Kind, string? Subject, string? Body);

/// <summary>
/// Admin, Reminders: the days each reminder is sent and its email wording (D-13, D-70). Closed choices only: whole days, and plain text
/// with a fixed list of {placeholders}. Every saved change is kept in the history and audited.
/// </summary>
public static class ReminderAdminEndpoints
{
    public static void MapReminderAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/reminders");

        group.MapGet("/", async (CurrentUser current, Db db) =>
        {
            var (me, refusal) = await RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var days = (await c.QueryAsync<(string Kind, string Days)>("SELECT Kind, Days FROM contracts.ReminderSetting")).ToDictionary(d => d.Kind, d => d.Days);
            var wording = (await c.QueryAsync<(string Kind, string Subject, string Body)>("SELECT Kind, Subject, Body FROM contracts.EmailTemplate")).ToDictionary(w => w.Kind);
            var history = await c.QueryAsync("""
                SELECT TOP (50) h.Id, h.Kind, h.What, h.OldValue, h.NewValue, p.Name AS ByName, h.AtUtc
                FROM contracts.ReminderChange h JOIN contracts.Person p ON p.Id = h.ById ORDER BY h.Id DESC
                """);
            var lastRun = await c.QuerySingleOrDefaultAsync<DateTime?>("SELECT LastRunUtc FROM contracts.JobRun WHERE Name = N'Reminders'");
            return Results.Ok(new
            {
                kinds = ReminderWording.Kinds.Select(k => new
                {
                    kind = k, name = ReminderWording.KindNames[k],
                    days = days[k].Split(',').Select(int.Parse).OrderByDescending(d => d),
                    subject = wording[k].Subject, body = wording[k].Body,
                }),
                placeholders = ReminderWording.Placeholders,
                history = history.Select(h => new
                {
                    id = (int)h.Id, kind = (string)h.Kind, kindName = ReminderWording.KindNames[(string)h.Kind], what = (string)h.What,
                    before = (string)h.OldValue, after = (string)h.NewValue, byName = (string)h.ByName, atUtc = DateTime.SpecifyKind((DateTime)h.AtUtc, DateTimeKind.Utc),
                }),
                lastRunUtc = lastRun is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : (DateTime?)null,
            });
        });

        group.MapPut("/days", async (SaveReminderDays body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            if (body.Kind is not { } kind || !ReminderWording.Kinds.Contains(kind)) return ApiResults.Invalid("Choose a reminder.", new() { ["kind"] = "Choose a reminder." });
            var days = (body.Days ?? []).Distinct().OrderByDescending(d => d).ToArray();
            if (days.Length == 0) return ApiResults.Invalid("Add at least one day.", new() { ["days"] = "Add at least one day, for example 30." });
            if (days.Length > 8) return ApiResults.Invalid("Use up to 8 days.", new() { ["days"] = "Use up to 8 reminders." });
            if (days.Any(d => d is < 1 or > 365)) return ApiResults.Invalid("Use days between 1 and 365.", new() { ["days"] = "Each one must be between 1 and 365 days before." });
            var after = string.Join(",", days);
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await c.QuerySingleAsync<string>("SELECT Days FROM contracts.ReminderSetting WITH (UPDLOCK) WHERE Kind = @kind", new { kind }, tx);
            if (before == after) return Results.Ok(new { changed = false });
            await c.ExecuteAsync("UPDATE contracts.ReminderSetting SET Days = @after, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Kind = @kind", new { after, me = me!.Id, kind }, tx);
            await c.ExecuteAsync("INSERT INTO contracts.ReminderChange (Kind, What, OldValue, NewValue, ById) VALUES (@kind, N'Days', @before, @after, @me)", new { kind, before, after, me = me.Id }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "reminders.days", "ReminderSetting", null,
                $"{me.Name} changed the {ReminderWording.KindNames[kind].ToLowerInvariant()} reminders from {List(before)} to {List(after)} days before.", new { kind, before, after });
            await tx.CommitAsync();
            return Results.Ok(new { changed = true });
        });

        group.MapPut("/wording", async (SaveReminderWording body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            if (body.Kind is not { } kind || !ReminderWording.Kinds.Contains(kind)) return ApiResults.Invalid("Choose a reminder.", new() { ["kind"] = "Choose a reminder." });
            var subject = (body.Subject ?? "").Trim();
            var text = (body.Body ?? "").Replace("\r\n", "\n").Trim();
            var problems = new Dictionary<string, string>();
            if (subject.Length == 0) problems["subject"] = "Add a subject line.";
            else if (subject.Length > 200) problems["subject"] = "Keep the subject under 200 characters.";
            else if (subject.Contains('\n')) problems["subject"] = "Keep the subject on one line.";
            else if (ReminderWording.Check(subject) is { } sp) problems["subject"] = sp;
            if (text.Length == 0) problems["body"] = "Add the email's wording.";
            else if (text.Length > 4000) problems["body"] = "Keep it under 4,000 characters.";
            else if (ReminderWording.Check(text) is { } bp) problems["body"] = bp;
            if (problems.Count > 0) return ApiResults.Invalid(problems.Values.First(), problems);
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await c.QuerySingleAsync<(string Subject, string Body)>("SELECT Subject, Body FROM contracts.EmailTemplate WITH (UPDLOCK) WHERE Kind = @kind", new { kind }, tx);
            if (before.Subject == subject && before.Body == text) return Results.Ok(new { changed = false });
            await c.ExecuteAsync("UPDATE contracts.EmailTemplate SET Subject = @subject, Body = @text, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Kind = @kind",
                new { subject, text, me = me!.Id, kind }, tx);
            await c.ExecuteAsync("INSERT INTO contracts.ReminderChange (Kind, What, OldValue, NewValue, ById) VALUES (@kind, N'Wording', @before, @after, @me)",
                new { kind, before = $"{before.Subject}\n\n{before.Body}", after = $"{subject}\n\n{text}", me = me.Id }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "reminders.wording", "EmailTemplate", null,
                $"{me.Name} changed the wording of the {ReminderWording.KindNames[kind].ToLowerInvariant()} reminder email.",
                new { kind, before = new { before.Subject, before.Body }, after = new { subject, body = text } });
            await tx.CommitAsync();
            return Results.Ok(new { changed = true });
        });
    }

    private static string List(string days) => string.Join(", ", days.Split(','));

    private static async Task<(Person? Me, IResult? Refusal)> RequireAdminAsync(CurrentUser current, Db db)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return (null, refusal);
        return me!.IsAdmin ? (me, null) : (null, ApiResults.NotAllowed("Only admins can change reminders."));
    }
}
