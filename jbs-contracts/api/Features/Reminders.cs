using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Every email's wording (D-70, D-73): plain text an admin can change, with a fixed list of {placeholders} per email. A paragraph
/// whose placeholder has nothing to fill (for example no note) is left out. Values are plain text, HTML-encoded in the email.
/// </summary>
public static partial class EmailWording
{
    public sealed record Kind(string Key, string Name, string Group, string When, string[] Placeholders, bool HasDays = false, string Button = "Open the contract");

    private static readonly string[] ContractFields = ["First name", "Contract title", "Reference", "Supplier", "Owner"];

    public static readonly Kind[] Kinds =
    [
        new("Notice", "Notice deadline reminder", "Reminders", "Before the notice deadline, to the owner and backup, until a renewal decision is recorded.", [.. ContractFields, "Due date", "Days"], HasDays: true),
        new("Escalation", "Escalation to the Group Head", "Reminders", "Before the notice deadline with no decision, to the Group Head (the owner and backup are copied).", [.. ContractFields, "Due date", "Days"], HasDays: true),
        new("End", "End date reminder", "Reminders", "Before the end date, to the owner and backup, until a renewal decision is recorded.", [.. ContractFields, "Due date", "Days"], HasDays: true),
        new("Review", "Review date reminder", "Reminders", "Before the review date, to the owner and backup, until the review is done.", [.. ContractFields, "Due date", "Days"], HasDays: true),
        new("AccessGranted", "You've been given access", "People", "When an admin adds someone (on screen or from Excel).", ["First name", "Added by", "Roles", "Windows logon"], Button: "Open JBS Contracts"),
        new("LegalNew", "Sent to Legal (new)", "Legal review", "To everyone in Legal, when a contract is sent to Legal and nobody has picked it up.", [.. ContractFields, "Sent by", "Note"]),
        new("LegalReturned", "Back with Legal", "Legal review", "To the Legal person who has it, when the owner sends it back to Legal.", [.. ContractFields, "Sent by", "Note"]),
        new("LegalAssigned", "Legal review given to you", "Legal review", "To a Legal person when a colleague gives them the review.", [.. ContractFields, "Assigned by"]),
        new("BackFromLegal", "Back with the owner from Legal", "Legal review", "To the owner and backup when Legal sends it back.", [.. ContractFields, "Legal person", "Note"]),
        new("LegalCleared", "Cleared by Legal", "Legal review", "To the owner and backup when Legal clears it.", [.. ContractFields, "Legal person", "Outcome"]),
        new("ApprovalWaiting", "Waiting for approval", "Approval", "To the Group Head when a contract is sent for approval.", [.. ContractFields, "Sent by", "Effective date"]),
        new("Approved", "Approved and Effective", "Approval", "To the owner and backup when it's approved.", [.. ContractFields, "Approver"]),
        new("ApprovalUndone", "Approval undone", "Approval", "To the owner and backup when the approver undoes an approval made by mistake.", [.. ContractFields, "Approver"]),
        new("ApprovalSentBack", "Sent back from approval", "Approval", "To the owner and backup when an approver sends it back.", [.. ContractFields, "Approver", "Reason"]),
        new("HandedOver", "Contract handed over to you", "Variations and hand-overs", "To the new owner or backup when a contract owner hands a contract over.", [.. ContractFields, "Handed over by", "Role"]),
        new("VariationCleared", "Variation cleared by Legal", "Variations and hand-overs", "To the owner and backup when Legal clears a Variation.", [.. ContractFields, "Variation", "Legal person", "Outcome"]),
        new("VariationWaiting", "Variation waiting for approval", "Variations and hand-overs", "To the Group Head when a Variation is sent for approval.", [.. ContractFields, "Variation", "Sent by", "Reason", "Changes"]),
        new("VariationApproved", "Variation approved", "Variations and hand-overs", "To the owner and backup when a Variation is approved and its changes apply.", [.. ContractFields, "Variation", "Approver", "Changes"]),
        new("VariationSentBack", "Variation sent back", "Variations and hand-overs", "To the owner and backup when an approver sends a Variation back.", [.. ContractFields, "Variation", "Approver", "Reason"]),
        new("SupplierExpiry", "Supplier document expiring", "Suppliers", "Before a supplier document expires, to each person who ticked Remind me before it expires.",
            ["First name", "Supplier", "Document", "Document type", "Due date", "Days"], HasDays: true, Button: "Open the supplier"),
        new("SupplierReview", "Business review due", "Suppliers", "Before a supplier's next business review is due, to its relationship owner.",
            ["First name", "Supplier", "Due date", "Days"], HasDays: true, Button: "Open the supplier"),
        new("ReviewAction", "Business review action given to you", "Suppliers", "To the person given an action at a business review (not when you give one to yourself).",
            ["First name", "Supplier", "Review date", "Due date", "Action"], Button: "Open the supplier"),
        new("FeedbackNew", "New feedback", "Feedback", "To every admin when someone sends feedback (not to whoever sent it).",
            ["First name", "Sent by", "Feedback type", "Screen", "Comment"], Button: "Open feedback"),
    ];

    public static readonly Dictionary<string, Kind> ByKey = Kinds.ToDictionary(k => k.Key);
    public static readonly string[] ReminderKinds = Kinds.Where(k => k.HasDays).Select(k => k.Key).ToArray();

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Token();

    /// <summary>Null when the wording only uses this email's placeholders and its braces match; otherwise a plain-words problem.</summary>
    public static string? Check(string kind, string text)
    {
        var allowed = ByKey[kind].Placeholders;
        foreach (Match m in Token().Matches(text))
            if (!allowed.Contains(m.Groups[1].Value))
                return $"{{{m.Groups[1].Value}}} isn't a placeholder this email can use. Use one of: {string.Join(", ", allowed.Select(p => $"{{{p}}}"))}.";
        var stripped = Token().Replace(text, "");
        if (stripped.Contains('{') || stripped.Contains('}')) return "A { or } doesn't match up. Placeholders look like {Reference}.";
        return null;
    }

    /// <summary>The email from this kind's saved wording, filled in. Paragraphs whose placeholders are empty are left out.</summary>
    public static async Task<(string Subject, string Text, string Html)> RenderAsync(SqlConnection c, SqlTransaction tx, string kind, IReadOnlyDictionary<string, string?> values, string? link)
    {
        var t = await c.QuerySingleAsync<(string Subject, string Body)>("SELECT Subject, Body FROM contracts.EmailTemplate WHERE Kind = @kind", new { kind }, tx);
        return Compose(kind, t.Subject, t.Body, values, link);
    }

    public static (string Subject, string Text, string Html) Compose(string kind, string subject, string body, IReadOnlyDictionary<string, string?> values, string? link)
    {
        bool Has(Match m) => values.TryGetValue(m.Groups[1].Value, out var v) && !string.IsNullOrWhiteSpace(v);
        string Fill(string text) => Token().Replace(text, m => Has(m) ? values[m.Groups[1].Value]! : "");
        var paragraphs = body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(para => Token().Matches(para).All(Has))
            .Select(Fill).ToArray();
        return EmailTemplates.FromWording(Fill(subject).Trim(), paragraphs, link, ByKey[kind].Button);
    }

    /// <summary>The placeholders every contract email shares.</summary>
    public static Dictionary<string, string?> For(string recipientName, ContractRow c) => new()
    {
        ["First name"] = LegalEndpoints.First(recipientName), ["Contract title"] = c.Title, ["Reference"] = c.Reference,
        ["Supplier"] = c.SupplierName ?? "the supplier", ["Owner"] = c.OwnerName,
    };
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
                        logger.LogInformation("Daily reminders for {Today}: {Sent} reminders, {Ended} contracts ended, {Renewed} renewed automatically.", today, result.Reminders, result.Ended, result.Renewed);
                        // A copy of the audit trail's latest link, kept outside the database (D-93): rewriting history and its hashes
                        // afterwards no longer matches these log lines.
                        var tip = await c.QuerySingleOrDefaultAsync<(long Id, string Hash)>("SELECT TOP (1) Id, Hash FROM contracts.AuditEvent ORDER BY Id DESC");
                        logger.LogInformation("Audit trail on {Today}: latest event {AuditId}, hash {AuditHash}.", today, tip.Id, tip.Hash);
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

    public sealed record Result(int Reminders, int Ended, int Renewed = 0);

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

        // 1b. Renews automatically (D-102): once the end date has passed and no one decided to let it end, the end date moves on by the
        // renewal term (as many terms as it takes to pass today), and the renewal decision is cleared for the new term, so its reminders
        // start again from the new date.
        var rolling = (await c.QueryAsync<(int Id, string Reference, DateTime EndDate, int Term, string? Decision)>("""
            SELECT Id, Reference, EndDate, RenewalTermMonths, RenewalDecision FROM contracts.Contract
            WHERE Stage = N'Effective' AND AutoRenews = 1 AND IsEvergreen = 0 AND RenewalTermMonths IS NOT NULL AND EndDate < @today
              AND (RenewalDecision IS NULL OR RenewalDecision <> N'LetItEnd')
            """, new { today = todayDate }, tx)).ToList();
        foreach (var k in rolling)
        {
            var was = DateOnly.FromDateTime(k.EndDate);
            var terms = 0;
            var end = was;
            while (end < today) end = was.AddMonths(k.Term * ++terms); // from the original date, so 31 Jan stays the month's end
            var p = new DynamicParameters(new { id = k.Id });
            p.Add("end", end.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET EndDate = @end, ReviewDate = CASE WHEN ReviewMonthsBeforeEnd IS NULL THEN ReviewDate ELSE DATEADD(MONTH, -ReviewMonthsBeforeEnd, @end) END,
                    RenewalDecision = NULL, RenewalDecisionNote = NULL, RenewalDecisionUtc = NULL, RenewalDecisionById = NULL,
                    UpdatedUtc = SYSUTCDATETIME()
                WHERE Id = @id
                """, p, tx);
            var cleared = k.Decision is { } d ? $" The renewal decision ({RenewalEndpoints.DecisionNames[d]}) was cleared for the new term." : "";
            await audit.WriteAsync(c, tx, job, "contract.auto-renewed", "Contract", k.Id,
                $"{k.Reference} renewed automatically for {ContractEndpoints.Months(k.Term * terms)}: its end date moved from {Day(was)} to {Day(end)}.{cleared}",
                new { endDate = new { before = was, after = end }, months = k.Term * terms, clearedDecision = k.Decision }, contractId: k.Id);
        }

        // 2. Reminders. A reminder an admin switched off has no steps, so nothing is sent for it (D-99).
        var offsets = (await c.QueryAsync<(string Kind, string Days, bool IsOn)>("SELECT Kind, Days, IsOn FROM contracts.ReminderSetting", transaction: tx))
            .ToDictionary(o => o.Kind, o => o.IsOn ? o.Days.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).OrderBy(d => d).ToArray() : []);
        var delivery = new Delivery(await c.ExecuteScalarAsync<string?>("SELECT Value FROM contracts.AppSetting WHERE Name = N'ReminderDelivery'", transaction: tx) ?? "Daily");
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
                sent += await SendAsync(c, tx, k, "Notice", deadline, today, offsets["Notice"], wording, Owners(k, people), delivery);
                var escalateTo = Owners(k, people);
                if (groupHead is not null && groupHead.Id != k.OwnerId && groupHead.Id != k.BackupOwnerId) escalateTo.Insert(0, groupHead);
                sent += await SendAsync(c, tx, k, "Escalation", deadline, today, offsets["Escalation"], wording, escalateTo, delivery);
            }
            if (undecided && !k.IsEvergreen && k.EndDate is { } end)
                sent += await SendAsync(c, tx, k, "End", DateOnly.FromDateTime(end), today, offsets["End"], wording, Owners(k, people), delivery);
            if (k.ReviewDate is { } review)
                sent += await SendAsync(c, tx, k, "Review", DateOnly.FromDateTime(review), today, offsets["Review"], wording, Owners(k, people), delivery);
        }

        // 3. Suppliers: documents expiring (to whoever ticked Remind me) and business reviews due (to the relationship owner).
        sent += await SupplierRemindersAsync(c, tx, today, offsets, wording, people, delivery);

        // 4. Summary emails: everything waiting for people who get their reminders daily, or weekly when it's Monday or something is close.
        await SendSummariesAsync(c, tx, today, people, delivery);

        await c.ExecuteAsync("""
            MERGE contracts.JobRun AS t USING (SELECT @JobName AS Name) AS s ON t.Name = s.Name
            WHEN MATCHED THEN UPDATE SET LastRunDate = @today, LastRunUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (Name, LastRunDate, LastRunUtc) VALUES (@JobName, @today, SYSUTCDATETIME());
            """, new { JobName, today = todayDate }, tx);
        await tx.CommitAsync(ct);
        return new Result(sent, ending.Count, rolling.Count);
    }

    private async Task<int> SupplierRemindersAsync(SqlConnection c, SqlTransaction tx, DateOnly today, Dictionary<string, int[]> offsets, Dictionary<string, Wording> wording,
        Dictionary<int, Person> people, Delivery delivery)
    {
        var sent = 0;
        int? Step(DateOnly due, string kind)
        {
            var left = due.DayNumber - today.DayNumber;
            return left < 0 ? null : offsets[kind].Where(d => left <= d).Cast<int?>().FirstOrDefault();
        }

        var watched = await c.QueryAsync<(int DocumentId, int PersonId, DateTime ExpiresOn, string Title, string TypeName, int SupplierId, string Supplier)>("""
            SELECT d.Id, w.PersonId, d.ExpiresOn, d.Title, t.Value, s.Id, s.Name
            FROM contracts.SupplierDocumentWatch w JOIN contracts.SupplierDocument d ON d.Id = w.DocumentId JOIN contracts.ListItem t ON t.Id = d.TypeId
            JOIN contracts.Supplier s ON s.Id = d.SupplierId
            WHERE d.IsRemoved = 0 AND d.ExpiresOn IS NOT NULL
            """, transaction: tx);
        foreach (var w in watched)
        {
            var due = DateOnly.FromDateTime(w.ExpiresOn);
            if (Step(due, "SupplierExpiry") is not { } step || !people.TryGetValue(w.PersonId, out var person)) continue;
            var inserted = await c.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM contracts.SupplierDocumentReminder WHERE DocumentId = @doc AND PersonId = @person AND ExpiresOn = @due AND DaysBefore = @step)
                INSERT INTO contracts.SupplierDocumentReminder (DocumentId, PersonId, ExpiresOn, DaysBefore) VALUES (@doc, @person, @due, @step)
                """, new { doc = w.DocumentId, person = person.Id, due = w.ExpiresOn, step }, tx);
            if (inserted == 0) continue;
            var values = new Dictionary<string, string?>
            {
                ["First name"] = LegalEndpoints.First(person.Name), ["Supplier"] = w.Supplier, ["Document"] = w.Title, ["Document type"] = w.TypeName.ToLowerInvariant(),
                ["Due date"] = Day(due), ["Days"] = (due.DayNumber - today.DayNumber).ToString(CultureInfo.InvariantCulture),
            };
            var t = wording["SupplierExpiry"];
            var docLink = settings.SiteUrl is null ? null : $"{settings.SiteUrl}/suppliers/{w.SupplierId}?tab=documents";
            var left = due.DayNumber - today.DayNumber;
            if (!await HoldAsync(c, tx, person, delivery, "SupplierExpiry", $"{w.Supplier}: \"{w.Title}\" ({w.TypeName.ToLowerInvariant()}) expires {Day(due)}, {In(left)}.", docLink, due, left, null, w.SupplierId))
                await EmailOutbox.QueueAsync(c, tx, "supplier-expiry", person.Email, person.Name,
                    EmailWording.Compose("SupplierExpiry", t.Subject, t.Body, values, docLink),
                    personId: person.Id, supplierId: w.SupplierId, dedupeKey: $"supplier-expiry:{w.DocumentId}:{due:yyyy-MM-dd}:{step}:{person.Id}");
            sent++;
        }

        var reviews = await c.QueryAsync<(int SupplierId, string Supplier, int OwnerId, DateTime NextReviewDue)>("""
            SELECT Id, Name, RelationshipOwnerId, NextReviewDue FROM contracts.Supplier
            WHERE IsActive = 1 AND RelationshipOwnerId IS NOT NULL AND NextReviewDue IS NOT NULL
            """, transaction: tx);
        foreach (var r in reviews)
        {
            var due = DateOnly.FromDateTime(r.NextReviewDue);
            if (Step(due, "SupplierReview") is not { } step || !people.TryGetValue(r.OwnerId, out var person)) continue;
            var inserted = await c.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM contracts.SupplierReviewReminder WHERE SupplierId = @supplier AND PersonId = @person AND DueOn = @due AND DaysBefore = @step)
                INSERT INTO contracts.SupplierReviewReminder (SupplierId, PersonId, DueOn, DaysBefore) VALUES (@supplier, @person, @due, @step)
                """, new { supplier = r.SupplierId, person = person.Id, due = r.NextReviewDue, step }, tx);
            if (inserted == 0) continue;
            var values = new Dictionary<string, string?>
            {
                ["First name"] = LegalEndpoints.First(person.Name), ["Supplier"] = r.Supplier, ["Due date"] = Day(due),
                ["Days"] = (due.DayNumber - today.DayNumber).ToString(CultureInfo.InvariantCulture),
            };
            var t = wording["SupplierReview"];
            var reviewLink = settings.SiteUrl is null ? null : $"{settings.SiteUrl}/suppliers/{r.SupplierId}?tab=reviews";
            var left = due.DayNumber - today.DayNumber;
            if (!await HoldAsync(c, tx, person, delivery, "SupplierReview", $"{r.Supplier}: business review due {Day(due)}, {In(left)}. You're the relationship owner.", reviewLink, due, left, null, r.SupplierId))
                await EmailOutbox.QueueAsync(c, tx, "supplier-review", person.Email, person.Name,
                    EmailWording.Compose("SupplierReview", t.Subject, t.Body, values, reviewLink),
                    personId: person.Id, supplierId: r.SupplierId, dedupeKey: $"supplier-review:{r.SupplierId}:{due:yyyy-MM-dd}:{step}:{person.Id}");
            sent++;
        }
        return sent;
    }

    private static List<Person> Owners(Row k, Dictionary<int, Person> people) =>
        new[] { k.OwnerId, k.BackupOwnerId ?? 0 }.Distinct().Where(people.ContainsKey).Select(id => people[id]).ToList();

    /// <summary>
    /// Sends the most recent due step for this date, if it hasn't been sent: steps are days before (for example 90, 60, 30, 14, 7).
    /// Nothing is sent once the date has passed.
    /// </summary>
    private async Task<int> SendAsync(SqlConnection c, SqlTransaction tx, Row k, string kind, DateOnly due, DateOnly today, int[] steps,
        Dictionary<string, Wording> wording, List<Person> recipients, Delivery delivery)
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
            var values = new Dictionary<string, string?>
            {
                ["First name"] = LegalEndpoints.First(person.Name), ["Contract title"] = k.Title, ["Reference"] = k.Reference, ["Supplier"] = k.SupplierName ?? "the supplier",
                ["Owner"] = k.OwnerName, ["Due date"] = due.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture), ["Days"] = left.ToString(CultureInfo.InvariantCulture),
            };
            var link = settings.SiteUrl is null ? null : $"{settings.SiteUrl}/contracts/{k.Id}";
            var name = $"{k.Reference} {k.Title}" + (k.SupplierName is { } sn ? $" ({sn})" : "");
            var line = kind switch
            {
                "Notice" => $"Notice deadline for {name}: {Day(due)}, {In(left)}. Decide whether to renew, let it end or renegotiate.",
                "Escalation" => $"No renewal decision yet for {name} (owner {k.OwnerName}). The notice deadline is {Day(due)}, {In(left)}.",
                "End" => $"{name} ends on {Day(due)}, {In(left)}.",
                _ => $"Review due for {name}: {Day(due)}, {In(left)}.",
            };
            if (await HoldAsync(c, tx, person, delivery, kind, line, link, due, left, k.Id, null)) continue;
            await EmailOutbox.QueueAsync(c, tx, $"reminder-{kind.ToLowerInvariant()}", person.Email, person.Name,
                EmailWording.Compose(kind, w.Subject, w.Body, values, link),
                personId: person.Id, contractId: k.Id, dedupeKey: $"reminder:{k.Id}:{kind}:{due:yyyy-MM-dd}:{step}:{person.Id}");
        }
        return 1;
    }

    // ------------------------------------------------------------ Summaries (D-99)

    /// <summary>The default an admin set; each person's own choice wins.</summary>
    private sealed record Delivery(string Default)
    {
        public string For(Person p) => p.ReminderDelivery ?? Default;
    }

    private static string Day(DateOnly d) => d.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
    private static string In(int days) => days switch { 0 => "today", 1 => "tomorrow", _ => $"in {days} days" };

    /// <summary>For someone who gets summaries: keeps the reminder for their next summary email instead of sending it now. False: send it now.</summary>
    private static async Task<bool> HoldAsync(SqlConnection c, SqlTransaction tx, Person person, Delivery delivery, string kind, string line, string? link, DateOnly due,
        int left, int? contractId, int? supplierId)
    {
        if (delivery.For(person) == "Each") return false;
        await c.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM contracts.Contract WHERE Id = @contractId AND IsDemo = 1)
               AND NOT EXISTS (SELECT 1 FROM contracts.Supplier WHERE Id = @supplierId AND IsDemo = 1)
            INSERT INTO contracts.ReminderSummaryItem (PersonId, Kind, Line, Link, DueDate, DaysLeft, ContractId, SupplierId)
            VALUES (@personId, @kind, @line, @link, @due, @left, @contractId, @supplierId)
            """, new { personId = person.Id, kind, line, link, due = due.ToDateTime(TimeOnly.MinValue), left, contractId, supplierId }, tx);
        return true;
    }

    /// <summary>
    /// One email per person with everything waiting: every day for Daily; for Weekly on Mondays, or sooner when something is a week or
    /// less away (so a weekly summary never makes someone miss a date). Someone who switched to "as they happen" gets what was waiting now.
    /// </summary>
    private async Task SendSummariesAsync(SqlConnection c, SqlTransaction tx, DateOnly today, Dictionary<int, Person> people, Delivery delivery)
    {
        var waiting = (await c.QueryAsync<(int Id, int PersonId, string Line, string? Link, DateTime DueDate)>(
            "SELECT Id, PersonId, Line, Link, DueDate FROM contracts.ReminderSummaryItem WITH (UPDLOCK) WHERE SentUtc IS NULL ORDER BY DueDate, Id", transaction: tx)).ToList();
        foreach (var group in waiting.GroupBy(w => w.PersonId))
        {
            var items = group.ToList();
            var ids = System.Text.Json.JsonSerializer.Serialize(items.Select(i => i.Id));
            if (!people.TryGetValue(group.Key, out var person))
            {
                // Switched off since: nothing to send.
                await c.ExecuteAsync("UPDATE contracts.ReminderSummaryItem SET SentUtc = SYSUTCDATETIME() WHERE Id IN (SELECT value FROM OPENJSON(@ids))", new { ids }, tx);
                continue;
            }
            var how = delivery.For(person);
            var close = items.Any(i => DateOnly.FromDateTime(i.DueDate).DayNumber - today.DayNumber <= 7);
            if (how == "Weekly" && today.DayOfWeek != DayOfWeek.Monday && !close) continue;
            var paragraphs = new List<string>
            {
                $"Hi {LegalEndpoints.First(person.Name)},",
                items.Count == 1 ? "Here's what's coming up:" : $"Here are the {items.Count} things coming up:",
            };
            paragraphs.AddRange(items.Select(i => "• " + i.Line + (i.Link is { } l ? $"\nOpen it: {l}" : "")));
            paragraphs.Add(how == "Weekly"
                ? "You get your reminders in one email a week, on Mondays (sooner when something is a week or less away). To change that, open JBS Contracts, click your name and choose Email reminders."
                : "You get your reminders in one email a day. To change that, open JBS Contracts, click your name and choose Email reminders.");
            var subject = items.Count == 1 ? "Your JBS Contracts reminder: 1 thing coming up" : $"Your JBS Contracts reminders: {items.Count} things coming up";
            await EmailOutbox.QueueAsync(c, tx, "reminder-summary", person.Email, person.Name,
                EmailTemplates.FromWording(subject, [.. paragraphs], settings.SiteUrl, "Open JBS Contracts"),
                personId: person.Id, dedupeKey: $"summary:{person.Id}:{today:yyyy-MM-dd}");
            await c.ExecuteAsync("UPDATE contracts.ReminderSummaryItem SET SentUtc = SYSUTCDATETIME() WHERE Id IN (SELECT value FROM OPENJSON(@ids))", new { ids }, tx);
        }
    }
}
