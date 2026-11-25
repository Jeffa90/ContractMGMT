using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>Reminder kinds, their default days and their wording with a fixed list of placeholders (D-13, D-32, D-70).</summary>
public static partial class ReminderWording
{
    public static readonly string[] Kinds = ["Notice", "Escalation", "End", "Review"];

    public static readonly Dictionary<string, string> KindNames = new()
    {
        ["Notice"] = "Notice deadline", ["Escalation"] = "Escalation to the Group Head", ["End"] = "End date", ["Review"] = "Review date",
    };

    /// <summary>The only placeholders wording may use. Values are always plain text (HTML-encoded in the email).</summary>
    public static readonly string[] Placeholders = ["First name", "Contract title", "Reference", "Supplier", "Owner", "Due date", "Days"];

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Token();

    /// <summary>Null when the wording only uses known placeholders and its braces match; otherwise a plain-words problem.</summary>
    public static string? Check(string text)
    {
        foreach (Match m in Token().Matches(text))
            if (!Placeholders.Contains(m.Groups[1].Value))
                return $"{{{m.Groups[1].Value}}} isn't a placeholder you can use. Use one of: {string.Join(", ", Placeholders.Select(p => $"{{{p}}}"))}.";
        var stripped = Token().Replace(text, "");
        if (stripped.Contains('{') || stripped.Contains('}')) return "A { or } doesn't match up. Placeholders look like {Reference}.";
        return null;
    }

    public static string Fill(string text, IReadOnlyDictionary<string, string> values) =>
        Token().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}

/// <summary>
/// The daily reminder job (D-13, A-13): once a day after 7:00am Brisbane time, and catching up after a missed day, it ends contracts whose
/// owner chose Let it end (or End early with a later date) once their date is reached, and sends due reminders:
/// notice deadline, escalation to the Group Head, end date and review date. For each kind only the most recent due step is sent (a
/// late start never sends a pile of old reminders), and each step is sent once (unique key), even after a restart.
/// </summary>
public sealed class ReminderJob(Db db, AuditLog audit, AppSettings settings, TimeProvider clock, ILogger<ReminderJob> logger) : BackgroundService
{
    private const string JobName = "Reminders";
    private static readonly TimeZoneInfo Brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Email.SendInBackground) return; // tests run the job themselves
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Brisbane);
                var today = DateOnly.FromDateTime(local.DateTime);
                if (local.Hour >= 7)
                {
                    await using var c = await db.OpenAsync(stoppingToken);
                    var last = await c.ExecuteScalarAsync<DateTime?>("SELECT LastRunDate FROM contracts.JobRun WHERE Name = @JobName", new { JobName });
                    if (last is null || DateOnly.FromDateTime(last.Value) < today)
                    {
                        var result = await RunAsync(today, stoppingToken);
                        logger.LogInformation("Daily reminders for {Today}: {Sent} reminders, {Ended} contracts ended.", today, result.Reminders, result.Ended);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "The daily reminder job failed; it will try again shortly.");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public sealed record Result(int Reminders, int Ended);

    private sealed class Row
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public string? SupplierName { get; set; }
        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = "";
        public int? BackupOwnerId { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsEvergreen { get; set; }
        public int? NoticePeriodDays { get; set; }
        public DateTime? ReviewDate { get; set; }
        public string? RenewalDecision { get; set; }
        public DateTime? PlannedEndDate { get; set; }
        public string? EndedReason { get; set; }
    }

    private sealed class Wording
    {
        public string Kind { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Body { get; set; } = "";
    }

    /// <summary>Runs the job for a day (tests call this directly). All in one transaction.</summary>
    public async Task<Result> RunAsync(DateOnly today, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = await Db.BeginWriteAsync(c);
        var todayDate = today.ToDateTime(TimeOnly.MinValue);
        var job = new AuditLog.Actor(null, "JBS Contracts (daily reminders)");

        // 1. Endings: Let it end (on the end date or the date chosen), and End early with a later date.
        var ending = (await c.QueryAsync<Row>("""
            SELECT Id, Reference, Title, OwnerId, EndDate, PlannedEndDate, EndedReason FROM contracts.Contract
            WHERE Stage = N'Effective' AND RenewalDecision = N'LetItEnd'
              AND ((PlannedEndDate IS NOT NULL AND PlannedEndDate <= @today) OR (PlannedEndDate IS NULL AND IsEvergreen = 0 AND EndDate < @today))
            """, new { today = todayDate }, tx)).ToList();
        foreach (var e in ending)
        {
            var endedOn = e.PlannedEndDate ?? e.EndDate!.Value;
            var reason = e.EndedReason ?? "Let it end: it reached its end date.";
            var p = new DynamicParameters(new { reason, id = e.Id });
            p.Add("endedOn", endedOn, System.Data.DbType.Date);
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = N'Ended', StageSinceUtc = SYSUTCDATETIME(), EndedOn = @endedOn, EndedReason = @reason, PlannedEndDate = NULL, UpdatedUtc = SYSUTCDATETIME()
                WHERE Id = @id
                """, p, tx);
            await audit.WriteAsync(c, tx, job, "contract.ended", "Contract", e.Id,
                $"{e.Reference} ended on {endedOn.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}, as decided (Let it end).", new { endedOn = DateOnly.FromDateTime(endedOn) }, contractId: e.Id);
        }

        // 2. Reminders.
        var offsets = (await c.QueryAsync<(string Kind, string Days)>("SELECT Kind, Days FROM contracts.ReminderSetting", transaction: tx))
            .ToDictionary(o => o.Kind, o => o.Days.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).OrderBy(d => d).ToArray());
        var wording = (await c.QueryAsync<Wording>("SELECT Kind, Subject, Body FROM contracts.EmailTemplate", transaction: tx)).ToDictionary(w => w.Kind);
        var people = (await c.QueryAsync<Person>("SELECT * FROM contracts.Person WHERE IsActive = 1", transaction: tx)).ToDictionary(p => p.Id);
        var groupHead = people.Values.FirstOrDefault(p => p.IsGroupHead);
        var contracts = await c.QueryAsync<Row>("""
            SELECT k.Id, k.Reference, k.Title, s.Name AS SupplierName, k.OwnerId, o.Name AS OwnerName, k.BackupOwnerId, k.EndDate, k.IsEvergreen, k.NoticePeriodDays,
                   k.ReviewDate, k.RenewalDecision, k.PlannedEndDate
            FROM contracts.Contract k JOIN contracts.Person o ON o.Id = k.OwnerId LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
            WHERE k.Stage = N'Effective'
            """, transaction: tx);
        var sent = 0;
        foreach (var k in contracts)
        {
            var undecided = k.RenewalDecision is null;
            var anchor = k.IsEvergreen ? k.ReviewDate : k.EndDate;
            if (undecided && k.NoticePeriodDays is { } notice && anchor is { } a)
            {
                var deadline = DateOnly.FromDateTime(a.AddDays(-notice));
                sent += await SendAsync(c, tx, k, "Notice", deadline, today, offsets["Notice"], wording, Owners(k, people));
                var escalateTo = Owners(k, people);
                if (groupHead is not null && groupHead.Id != k.OwnerId && groupHead.Id != k.BackupOwnerId) escalateTo.Insert(0, groupHead);
                sent += await SendAsync(c, tx, k, "Escalation", deadline, today, offsets["Escalation"], wording, escalateTo);
            }
            if (undecided && !k.IsEvergreen && k.EndDate is { } end)
                sent += await SendAsync(c, tx, k, "End", DateOnly.FromDateTime(end), today, offsets["End"], wording, Owners(k, people));
            if (k.ReviewDate is { } review)
                sent += await SendAsync(c, tx, k, "Review", DateOnly.FromDateTime(review), today, offsets["Review"], wording, Owners(k, people));
        }

        await c.ExecuteAsync("""
            MERGE contracts.JobRun AS t USING (SELECT @JobName AS Name) AS s ON t.Name = s.Name
            WHEN MATCHED THEN UPDATE SET LastRunDate = @today, LastRunUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Name, LastRunDate, LastRunUtc) VALUES (@JobName, @today, SYSUTCDATETIME());
            """, new { JobName, today = todayDate }, tx);
        await tx.CommitAsync(ct);
        return new Result(sent, ending.Count);
    }

    private static List<Person> Owners(Row k, Dictionary<int, Person> people) =>
        new[] { k.OwnerId, k.BackupOwnerId ?? 0 }.Distinct().Where(people.ContainsKey).Select(id => people[id]).ToList();

    /// <summary>
    /// Sends the most recent due step for this date, if it hasn't been sent: steps are days before (for example 90, 60, 30, 14, 7).
    /// Nothing is sent once the date has passed.
    /// </summary>
    private async Task<int> SendAsync(SqlConnection c, SqlTransaction tx, Row k, string kind, DateOnly due, DateOnly today, int[] steps,
        Dictionary<string, Wording> wording, List<Person> recipients)
    {
        var left = due.DayNumber - today.DayNumber;
        if (left < 0 || recipients.Count == 0) return 0;
        var step = steps.Where(d => left <= d).Cast<int?>().FirstOrDefault(); // smallest step not yet passed
        if (step is null) return 0;
        var dueDate = due.ToDateTime(TimeOnly.MinValue);
        var already = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Reminder WHERE ContractId = @id AND Kind = @kind AND DueDate = @dueDate AND DaysBefore = @step",
            new { id = k.Id, kind, dueDate, step }, tx);
        if (already > 0) return 0;
        await c.ExecuteAsync("INSERT INTO contracts.Reminder (ContractId, Kind, DueDate, DaysBefore, Recipients) VALUES (@id, @kind, @dueDate, @step, @to)",
            new { id = k.Id, kind, dueDate, step, to = string.Join(", ", recipients.Select(r => r.Name)) }, tx);
        var w = wording[kind];
        foreach (var person in recipients)
        {
            var values = new Dictionary<string, string>
            {
                ["First name"] = LegalEndpoints.First(person.Name), ["Contract title"] = k.Title, ["Reference"] = k.Reference, ["Supplier"] = k.SupplierName ?? "the supplier",
                ["Owner"] = k.OwnerName, ["Due date"] = due.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture), ["Days"] = left.ToString(CultureInfo.InvariantCulture),
            };
            var link = settings.SiteUrl is null ? null : $"{settings.SiteUrl}/contracts/{k.Id}";
            await EmailOutbox.QueueAsync(c, tx, $"reminder-{kind.ToLowerInvariant()}", person.Email, person.Name,
                EmailTemplates.FromWording(ReminderWording.Fill(w.Subject, values), ReminderWording.Fill(w.Body, values), link),
                personId: person.Id, contractId: k.Id, dedupeKey: $"reminder:{k.Id}:{kind}:{due:yyyy-MM-dd}:{step}:{person.Id}");
        }
        return 1;
    }
}
