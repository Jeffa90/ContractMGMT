using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public sealed record SendFeedback(string? Kind, string? Comment, string? Screen);
public sealed record FeedbackReplyInput(string? Text);
public sealed record FeedbackStatusInput(string? Status);
public sealed record FeedbackArchiveInput(bool Archived);

/// <summary>
/// Feedback (D-101, docs/features/feedback.md): anyone signed in sends feedback from any screen and sees, replies to and changes the status
/// of their own; admins see everyone's, reply, change the status and archive, and are emailed when new feedback comes in. Who sent,
/// replied or acted is always the signed-in person. Every change is audited. Nothing is deleted.
/// </summary>
public static class FeedbackEndpoints
{
    public static readonly Dictionary<string, string> KindNames = new()
    {
        ["Bug"] = "Bug", ["Improvement"] = "Improvement", ["DataWrong"] = "Data looks wrong", ["Question"] = "Question", ["Other"] = "Other",
    };
    public static readonly Dictionary<string, string> StatusNames = new()
    {
        ["Open"] = "Open", ["InProgress"] = "In progress", ["Done"] = "Done", ["WontDo"] = "Won't do",
    };
    private const int MaxText = 4000;

    private sealed class Row
    {
        public int Id { get; set; }
        public string Kind { get; set; } = "";
        public string Comment { get; set; } = "";
        public string? Screen { get; set; }
        public string Status { get; set; } = "";
        public bool IsArchived { get; set; }
        public int CreatedById { get; set; }
        public string CreatedByName { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public int Replies { get; set; }
    }

    private sealed class ReplyRow
    {
        public int Id { get; set; }
        public string ByName { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
    }

    private const string Select = """
        SELECT f.Id, f.Kind, f.Comment, f.Screen, f.Status, f.IsArchived, f.CreatedById, p.Name AS CreatedByName, f.CreatedUtc, f.UpdatedUtc,
               (SELECT COUNT(*) FROM contracts.FeedbackReply r WHERE r.FeedbackId = f.Id) AS Replies
        FROM contracts.Feedback f JOIN contracts.Person p ON p.Id = f.CreatedById
        """;

    private static object View(Row f, Person me) => new
    {
        f.Id, f.Kind, KindName = KindNames[f.Kind], f.Comment, f.Screen, f.Status, StatusName = StatusNames[f.Status], f.IsArchived,
        f.CreatedByName, Mine = f.CreatedById == me.Id,
        CreatedUtc = DateTime.SpecifyKind(f.CreatedUtc, DateTimeKind.Utc), UpdatedUtc = DateTime.SpecifyKind(f.UpdatedUtc, DateTimeKind.Utc), f.Replies,
    };

    private static string Clean(string? s) => (s ?? "").Trim();

    public static void MapFeedbackEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/feedback");

        group.MapPost("/", async (SendFeedback body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var comment = Clean(body.Comment);
            if (comment.Length == 0) return ApiResults.Invalid("Tell us what happened.", new() { ["comment"] = "Tell us what happened, or what would help." });
            if (comment.Length > MaxText) return ApiResults.Invalid("Keep it shorter.", new() { ["comment"] = "Keep it under 4,000 characters." });
            var kind = body.Kind is { } k && KindNames.ContainsKey(k) ? k : "Other"; // anything else is saved as Other
            var screen = Clean(body.Screen);
            if (screen.Length > 500) screen = screen[..500];
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var id = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Feedback (Kind, Comment, Screen, CreatedById) OUTPUT INSERTED.Id VALUES (@kind, @comment, @screen, @me)
                """, new { kind, comment, screen = screen.Length == 0 ? null : screen, me = me!.Id }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "feedback.sent", "Feedback", id,
                $"{me.Name} sent feedback ({KindNames[kind]}){(screen.Length > 0 ? $" from {screen}" : "")}.", new { kind, screen });
            // Every admin is emailed (not whoever sent it).
            var admins = await c.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1 AND IsAdmin = 1 AND Id <> @me", new { me = me.Id }, tx);
            foreach (var a in admins)
                await EmailOutbox.QueueAsync(c, tx, "feedback-new", a.Email, a.Name,
                    await EmailWording.RenderAsync(c, tx, "FeedbackNew", new Dictionary<string, string?>
                    {
                        ["First name"] = LegalEndpoints.First(a.Name), ["Sent by"] = me.Name, ["Feedback type"] = KindNames[kind],
                        ["Screen"] = screen.Length == 0 ? "JBS Contracts" : screen, ["Comment"] = comment,
                    }, settings.SiteUrl is null ? null : $"{settings.SiteUrl}/feedback?open={id}"),
                    personId: a.Id, dedupeKey: $"feedback-new:{id}:{a.Id}");
            await tx.CommitAsync();
            return Results.Created($"/api/feedback/{id}", new { id });
        });

        // Mine, or (admins, all=true) everyone's; with counts for the tiles.
        group.MapGet("/", async (bool? all, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var everyone = all == true && me!.IsAdmin;
            await using var c = await db.OpenAsync();
            var rows = (await c.QueryAsync<Row>($"{Select} WHERE (@everyone = 1 OR f.CreatedById = @me) ORDER BY f.Id DESC", new { everyone, me = me!.Id })).ToList();
            return Results.Ok(new
            {
                canSeeAll = me.IsAdmin,
                showingAll = everyone,
                counts = new
                {
                    open = rows.Count(r => !r.IsArchived && r.Status == "Open"),
                    inProgress = rows.Count(r => !r.IsArchived && r.Status == "InProgress"),
                    closed = rows.Count(r => !r.IsArchived && r.Status is "Done" or "WontDo"),
                    archived = rows.Count(r => r.IsArchived),
                    total = rows.Count,
                },
                items = rows.Select(r => View(r, me)),
            });
        });

        group.MapGet("/{id:int}", async (int id, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var f = await c.QuerySingleOrDefaultAsync<Row>($"{Select} WHERE f.Id = @id", new { id });
            if (f is null || (f.CreatedById != me!.Id && !me.IsAdmin)) return ApiResults.NotFound("We couldn't find that feedback.");
            var replies = await c.QueryAsync<ReplyRow>("""
                SELECT r.Id, p.Name AS ByName, r.Text, r.CreatedUtc FROM contracts.FeedbackReply r JOIN contracts.Person p ON p.Id = r.ById
                WHERE r.FeedbackId = @id ORDER BY r.Id
                """, new { id });
            return Results.Ok(new
            {
                item = View(f, me),
                replies = replies.Select(r => new { r.Id, r.ByName, r.Text, CreatedUtc = DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc) }),
                canArchive = me.IsAdmin,
            });
        });

        group.MapPost("/{id:int}/replies", (int id, FeedbackReplyInput body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, current, db, audit, adminOnly: false, async (c, tx, me, f) =>
            {
                var text = Clean(body.Text);
                if (text.Length == 0) return ApiResults.Invalid("Write a reply.", new() { ["text"] = "Write a reply." });
                if (text.Length > MaxText) return ApiResults.Invalid("Keep it shorter.", new() { ["text"] = "Keep it under 4,000 characters." });
                await c.ExecuteAsync("INSERT INTO contracts.FeedbackReply (FeedbackId, ById, Text) VALUES (@id, @me, @text)", new { id, me = me.Id, text }, tx);
                return ("feedback.replied", $"{me.Name} replied to feedback {id}.");
            }));

        group.MapPut("/{id:int}/status", (int id, FeedbackStatusInput body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, current, db, audit, adminOnly: false, async (c, tx, me, f) =>
            {
                if (body.Status is not { } status || !StatusNames.ContainsKey(status))
                    return ApiResults.Invalid("Choose a status.", new() { ["status"] = "Choose Open, In progress, Done or Won't do." });
                await c.ExecuteAsync("UPDATE contracts.Feedback SET Status = @status WHERE Id = @id", new { id, status }, tx);
                return ("feedback.status", $"{me.Name} set feedback {id} to {StatusNames[status]} (was {StatusNames[f.Status]}).");
            }));

        group.MapPut("/{id:int}/archive", (int id, FeedbackArchiveInput body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, current, db, audit, adminOnly: true, async (c, tx, me, f) =>
            {
                await c.ExecuteAsync("UPDATE contracts.Feedback SET IsArchived = @archived WHERE Id = @id", new { id, archived = body.Archived }, tx);
                return ("feedback.archived", $"{me.Name} {(body.Archived ? "archived" : "unarchived")} feedback {id}.");
            }));
    }

    /// <summary>The person who sent it, or an admin (archive: admins only). The action returns a problem, or the audit action and summary.</summary>
    private static async Task<IResult> ActAsync(int id, CurrentUser current, Db db, AuditLog audit, bool adminOnly,
        Func<Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient.SqlTransaction, Person, Row, Task<object>> action)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return refusal;
        await using var c = await db.OpenAsync();
        await using var tx = await Db.BeginWriteAsync(c);
        var f = await c.QuerySingleOrDefaultAsync<Row>($"{Select} WHERE f.Id = @id", new { id }, tx);
        if (f is null || (f.CreatedById != me!.Id && !me.IsAdmin)) return ApiResults.NotFound("We couldn't find that feedback.");
        if (adminOnly && !me.IsAdmin) return ApiResults.NotAllowed("Only admins can archive feedback.");
        var result = await action(c, tx, me, f);
        if (result is IResult problem) return problem;
        var (auditAction, summary) = ((string, string))result;
        await c.ExecuteAsync("UPDATE contracts.Feedback SET UpdatedUtc = SYSUTCDATETIME() WHERE Id = @id", new { id }, tx);
        await audit.WriteAsync(c, tx, me.AsActor(), auditAction, "Feedback", id, summary);
        await tx.CommitAsync();
        return Results.Ok(new { id });
    }
}
