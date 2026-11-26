using System.Globalization;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;
using static JbsContracts.Api.Features.LegalEndpoints;

namespace JbsContracts.Api.Features;

public sealed record RenewalAction(string? RowVersion, string? Decision = null, string? Note = null, DateOnly? Date = null);

/// <summary>
/// Renewal decisions, renewing as a new linked contract, ending (on the end date, or early), review done, archiving, and the
/// Renewals page (product-spec section 8; D-12, D-14, D-67 to D-72). Every rule is checked on the server and every change audited.
/// </summary>
public static class RenewalEndpoints
{
    public static readonly Dictionary<string, string> DecisionNames = new()
    {
        ["Renew"] = "Renew", ["LetItEnd"] = "Let it end", ["Renegotiate"] = "Renegotiate",
    };

    private static string Day(DateTime d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    private static string Day(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    private static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    public static List<string> Actions(Person me, ContractRow c)
    {
        var actions = new List<string>();
        if (c.Stage == "Effective" && me.IsContractOwner)
        {
            actions.Add("recordDecision");
            if (c.PlannedEndDate is null) actions.Add("endEarly");
            actions.Add("reviewDone");
        }
        if (c.Stage == "Ended" && me.IsAdmin) actions.Add("archive");
        return actions;
    }

    public static object View(ContractRow r) => new
    {
        Decision = r.RenewalDecision, DecisionName = r.RenewalDecision is { } d ? DecisionNames[d] : null, DecidedByName = r.RenewalDecisionByName,
        DecidedUtc = ContractEndpoints.Utc(r.RenewalDecisionUtc), Note = r.RenewalDecisionNote, PlannedEndDate = D(r.PlannedEndDate),
    };

    public static async Task<IEnumerable<object>> RemindersAsync(SqlConnection c, int id) =>
        (await c.QueryAsync("SELECT Kind, DueDate, DaysBefore, SentUtc, Recipients FROM contracts.Reminder WHERE ContractId = @id ORDER BY SentUtc DESC, Id DESC", new { id }))
        .Select(r => (object)new
        {
            Kind = (string)r.Kind, DueDate = DateOnly.FromDateTime((DateTime)r.DueDate), DaysBefore = (int)r.DaysBefore,
            SentUtc = DateTime.SpecifyKind((DateTime)r.SentUtc, DateTimeKind.Utc), Recipients = (string)r.Recipients,
        });

    public static void MapRenewalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contracts/{id:int}");

        // Owner: Renew, Let it end or Renegotiate. Stops the notice, escalation and end-date reminders. Renew also starts the renewal draft.
        group.MapPost("/renewal-decision", (int id, RenewalAction body, CurrentUser current, Db db, AuditLog audit, TimeProvider clock) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (EffectiveOwner(x) is { } refusal) return refusal;
                if (body.Decision is not { } decision || !DecisionNames.ContainsKey(decision))
                    return ApiResults.Invalid("Choose a decision.", new() { ["decision"] = "Choose Renew, Let it end or Renegotiate." });
                var note = Clean(body.Note);
                if (note is { Length: > 1000 }) return TooLong("note", 1000);
                var today = DateOnly.FromDateTime(HomeEndpoints.Today(clock));
                DateOnly? plannedEnd = null;
                if (decision == "LetItEnd" && (c.IsEvergreen || c.EndDate is null))
                {
                    // Evergreen (or no end date): the owner says when it will end once notice is given (D-68).
                    if (body.Date is not { } end) return ApiResults.Invalid("Enter the date it will end.", new() { ["date"] = "Enter the date it will end, once notice is given." });
                    if (end < today) return ApiResults.Invalid("That date has passed.", new() { ["date"] = "Choose today or a later date. To end it now, use End early." });
                    plannedEnd = end;
                }
                var p = new DynamicParameters(new { decision, note, me = x.Me.Id, id });
                p.Add("plannedEnd", plannedEnd?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Contract SET RenewalDecision = @decision, RenewalDecisionNote = @note, RenewalDecisionUtc = SYSUTCDATETIME(), RenewalDecisionById = @me,
                        PlannedEndDate = @plannedEnd, EndedReason = NULL, EndedById = NULL, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, p, x.Tx);
                var what = decision switch
                {
                    "Renew" => "renew it",
                    "Renegotiate" => "renegotiate it",
                    _ => plannedEnd is { } e ? $"let it end on {Day(e)}" : c.EndDate is { } ed ? $"let it end on its end date ({Day(ed)})" : "let it end",
                };
                var changed = c.RenewalDecision is { } before && before != decision ? $" (was {DecisionNames[before]})" : "";
                await x.Audit("contract.renewal-decision", $"{x.Me.Name} decided to {what} for {c.Reference}{changed}" + Quote(note) + Stop(note), new { decision, note, plannedEnd });

                // Renew: a new draft, linked to this one (unless a renewal is already under way).
                if (decision == "Renew")
                {
                    var existing = await x.C.ExecuteScalarAsync<int?>("SELECT TOP (1) Id FROM contracts.Contract WHERE RenewsContractId = @id AND Stage <> N'Withdrawn'", new { id }, x.Tx);
                    x.Result = new { renewalId = existing ?? await StartRenewalAsync(x) };
                }
                return null;
            }, audit));

        // Owner: end an Effective contract with a reason, today/earlier (it ends now) or on a later date (it ends then; reminders stop).
        group.MapPost("/end", (int id, RenewalAction body, CurrentUser current, Db db, AuditLog audit, TimeProvider clock) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (EffectiveOwner(x) is { } refusal) return refusal;
                var reason = Clean(body.Note);
                var problems = new Dictionary<string, string>();
                if (reason is null) problems["note"] = "Say why it's ending, for example \"Supplier went into administration\".";
                else if (reason.Length > 1000) problems["note"] = "Keep it under 1,000 characters.";
                if (body.Date is not { } date) problems["date"] = "Enter the date it ends.";
                else if (c.EffectiveDate is { } eff && date < DateOnly.FromDateTime(eff)) problems["date"] = $"It can't end before it took effect ({Day(eff)}).";
                else if (c.EndDate is { } end && !c.IsEvergreen && date > DateOnly.FromDateTime(end)) problems["date"] = $"That's after its end date ({Day(end)}). Choose an earlier date.";
                if (problems.Count > 0) return ApiResults.Invalid(problems.Values.First(), problems);
                var today = DateOnly.FromDateTime(HomeEndpoints.Today(clock));
                var p = new DynamicParameters(new { reason, me = x.Me.Id, id });
                p.Add("date", body.Date!.Value.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
                if (body.Date <= today)
                {
                    await x.C.ExecuteAsync("""
                        UPDATE contracts.Contract SET Stage = N'Ended', StageSinceUtc = SYSUTCDATETIME(), EndedOn = @date, EndedReason = @reason, EndedById = @me,
                            PlannedEndDate = NULL, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                        WHERE Id = @id
                        """, p, x.Tx);
                    await x.Audit("contract.ended", $"{x.Me.Name} ended {c.Reference} early, on {Day(body.Date.Value)}" + Quote(reason) + Stop(reason), new { reason, date = body.Date });
                }
                else
                {
                    await x.C.ExecuteAsync("""
                        UPDATE contracts.Contract SET PlannedEndDate = @date, EndedReason = @reason, EndedById = @me, RenewalDecision = N'LetItEnd',
                            RenewalDecisionNote = @reason, RenewalDecisionUtc = SYSUTCDATETIME(), RenewalDecisionById = @me, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                        WHERE Id = @id
                        """, p, x.Tx);
                    await x.Audit("contract.end-planned", $"{x.Me.Name} set {c.Reference} to end early, on {Day(body.Date.Value)}" + Quote(reason) + Stop(reason), new { reason, date = body.Date });
                }
                return null;
            }, audit));

        // Owner: the review is done; optionally the next review date. Allowed on a locked contract (D-69).
        group.MapPost("/review-done", (int id, RenewalAction body, CurrentUser current, Db db, AuditLog audit, TimeProvider clock) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                var c = x.Contract;
                if (EffectiveOwner(x) is { } refusal) return refusal;
                var today = DateOnly.FromDateTime(HomeEndpoints.Today(clock));
                if (body.Date is { } next && next <= today) return ApiResults.Invalid("Choose a later date.", new() { ["date"] = "The next review date must be after today." });
                var p = new DynamicParameters(new { me = x.Me.Id, id });
                p.Add("today", today.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
                p.Add("next", body.Date?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
                await x.C.ExecuteAsync("""
                    UPDATE contracts.Contract SET LastReviewedOn = @today, LastReviewedById = @me, ReviewDate = @next, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                    WHERE Id = @id
                    """, p, x.Tx);
                await x.Audit("contract.review-done", $"{x.Me.Name} marked the review of {c.Reference} done" + (body.Date is { } n ? $"; next review {Day(n)}." : "; no next review date."),
                    new { reviewDate = new { before = D(c.ReviewDate), after = body.Date } });
                return null;
            }, audit));

        // Admin: archive an Ended contract (read only from then on; nothing is deleted, D-14).
        group.MapPost("/archive", (int id, RenewalAction body, CurrentUser current, Db db, AuditLog audit) =>
            ActAsync(id, body.RowVersion, current, db, async x =>
            {
                if (!x.Me.IsAdmin) return ApiResults.NotAllowed("Only admins can archive contracts.");
                if (x.Contract.Stage != "Ended") return ApiResults.NotAllowed("Only Ended contracts can be archived.");
                await x.C.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Archived', StageSinceUtc = SYSUTCDATETIME(), UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id",
                    new { me = x.Me.Id, id }, x.Tx);
                await x.Audit("contract.archived", $"{x.Me.Name} archived {x.Contract.Reference}.", null);
                return null;
            }, audit));

        // The Renewals page: Effective contracts whose notice deadline (or end date) is within the window, or has passed while they still run.
        app.MapGet("/api/renewals", async (int? days, bool? mine, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var window = days is 30 or 60 or 90 or 180 ? days.Value : 90;
            var today = DateOnly.FromDateTime(HomeEndpoints.Today(clock));
            await using var c = await db.OpenAsync();
            var rows = await c.QueryAsync<RenewalRow>($"""
                SELECT k.Id, k.Reference, k.Title, s.Name AS SupplierName, o.Name AS OwnerName, k.OwnerId, k.BackupOwnerId, k.EndDate, k.IsEvergreen, k.ReviewDate,
                       k.NoticePeriodDays, k.RenewalDecision, k.PlannedEndDate, k.Value, k.CurrencyCode, k.IsConfidential
                FROM contracts.Contract k LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId JOIN contracts.Person o ON o.Id = k.OwnerId
                WHERE k.Stage = N'Effective' AND {ContractAccess.VisibleSql("k")} {(mine == true ? "AND (k.OwnerId = @meId OR k.BackupOwnerId = @meId)" : "")}
                """, ContractAccess.Params(me!));
            var items = rows.Select(r => (Row: r, Key: KeyDate(r))).Where(x => x.Key.Date is { } d && d <= today.AddDays(window)
                && (d >= today || x.Row.RenewalDecision is null || x.Row.PlannedEndDate is not null))
                .OrderBy(x => x.Key.Date)
                .Select(x => new
                {
                    x.Row.Id, x.Row.Reference, x.Row.Title, x.Row.SupplierName, x.Row.OwnerName, x.Row.IsConfidential, x.Row.Value, x.Row.CurrencyCode,
                    KeyKind = x.Key.Kind, KeyDate = x.Key.Date, Days = x.Key.Date!.Value.DayNumber - today.DayNumber,
                    EndDate = D(x.Row.EndDate), x.Row.IsEvergreen, Decision = x.Row.RenewalDecision,
                    DecisionName = x.Row.RenewalDecision is { } dn ? DecisionNames[dn] : null, PlannedEndDate = D(x.Row.PlannedEndDate),
                    Mine = x.Row.OwnerId == me!.Id || x.Row.BackupOwnerId == me.Id,
                });
            return Results.Ok(new { days = window, today, items });
        });
    }

    public sealed class RenewalRow
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public string? SupplierName { get; set; }
        public string OwnerName { get; set; } = "";
        public int OwnerId { get; set; }
        public int? BackupOwnerId { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsEvergreen { get; set; }
        public DateTime? ReviewDate { get; set; }
        public int? NoticePeriodDays { get; set; }
        public string? RenewalDecision { get; set; }
        public DateTime? PlannedEndDate { get; set; }
        public decimal? Value { get; set; }
        public string CurrencyCode { get; set; } = "AUD";
        public bool IsConfidential { get; set; }
    }

    /// <summary>The date that matters for renewing: a planned end date, else the notice deadline, else the end date.</summary>
    internal static (string Kind, DateOnly? Date) KeyDate(RenewalRow r)
    {
        if (r.PlannedEndDate is { } planned) return ("Ends", DateOnly.FromDateTime(planned));
        var anchor = r.IsEvergreen ? r.ReviewDate : r.EndDate;
        if (r.NoticePeriodDays is { } n && anchor is { } a) return ("Notice", DateOnly.FromDateTime(a.AddDays(-n)));
        return r.IsEvergreen || r.EndDate is null ? ("End", null) : ("End", DateOnly.FromDateTime(r.EndDate.Value));
    }

    private static IResult? EffectiveOwner(Ctx x) =>
        !x.Me.IsContractOwner ? ApiResults.NotAllowed("Only contract owners can do that.")
        : x.Contract.Stage != "Effective" ? ApiResults.NotAllowed($"It isn't Effective (it's {ContractEndpoints.StageNames[x.Contract.Stage]}).")
        : null;

    /// <summary>Copies the contract into a new Draft that renews it (D-12): same supplier, owner and details; it starts the day after the old one ends.</summary>
    private static async Task<int> StartRenewalAsync(Ctx x)
    {
        var c = x.Contract;
        var reference = await ContractNumbers.NextAsync(x.C, x.Tx);
        var title = c.Title.Length > 190 ? c.Title[..190] : c.Title;
        var p = new DynamicParameters(new { reference, title = $"{title} (renewal)", id = c.Id, me = x.Me.Id });
        p.Add("start", c.EndDate is { } end && !c.IsEvergreen ? end.AddDays(1) : (DateTime?)null, System.Data.DbType.Date);
        var newId = await x.C.ExecuteScalarAsync<int>("""
            INSERT INTO contracts.Contract (Reference, Title, Description, SupplierId, CategoryId, AgreementTypeId, BusinessUnitId, SiteId, EntityId, FunctionId, OwnerId, BackupOwnerId,
                IsConfidential, Value, CurrencyCode, SapOutlineAgreement, StartDate, IsEvergreen, AutoRenews, RenewalTermMonths, NoticePeriodDays, NeedsLegalReview, RenewsContractId,
                CreatedById, UpdatedById, IsDemo)
            OUTPUT INSERTED.Id
            SELECT @reference, @title, Description, SupplierId, CategoryId, AgreementTypeId, BusinessUnitId, SiteId, EntityId, FunctionId, OwnerId, BackupOwnerId,
                IsConfidential, Value, CurrencyCode, SapOutlineAgreement, @start, IsEvergreen, AutoRenews, RenewalTermMonths, NoticePeriodDays, 1, Id,
                @me, @me, IsDemo
            FROM contracts.Contract WHERE Id = @id
            """, p, x.Tx);
        // Added fields come across too.
        await x.C.ExecuteAsync("""
            INSERT INTO contracts.ContractFieldValue (ContractId, FieldId, TextValue, NumberValue, DateValue, BoolValue, PersonId, SupplierId)
            SELECT @newId, FieldId, TextValue, NumberValue, DateValue, BoolValue, PersonId, SupplierId FROM contracts.ContractFieldValue WHERE ContractId = @id
            """, new { newId, id = c.Id }, x.Tx);
        await x.AuditFor(newId, "contract.created", $"{x.Me.Name} started {reference}, the renewal of {c.Reference} {c.Title}.", new { renews = c.Id });
        return newId;
    }
}
