using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

/// <summary>
/// The Team page (D-85): one row per person showing what they have coming up and outstanding, so a lead can see who needs to focus where.
/// For admins, auditors and Group Heads. It only shows; it changes nothing and sends nothing. Counts respect Confidential: a viewer who
/// can't see a Confidential contract doesn't have it counted.
/// </summary>
public static class TeamEndpoints
{
    /// <summary>What one contract (or Variation) means for one person.</summary>
    public static class What
    {
        public const string Overdue = "Overdue";             // notice deadline or end date passed, no renewal decision
        public const string ComingUp = "ComingUp";           // within the window, no renewal decision
        public const string Draft = "Draft";                 // their Draft to finish and send to Legal
        public const string BackFromLegal = "BackFromLegal"; // Legal review, the owner's turn
        public const string Signing = "Signing";             // signed copy to attach and send for approval
        public const string Variation = "Variation";         // an open Variation on the owner's turn
        public const string WithLegal = "WithLegal";         // theirs, waiting on Legal
        public const string InApproval = "InApproval";       // theirs, waiting on the approver
        public const string LegalTurn = "LegalTurn";         // a Legal person's picked-up review (contract or Variation)
    }

    private sealed class OpenRow
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public string Title { get; set; } = "";
        public string Stage { get; set; } = "";
        public string? WhoseTurn { get; set; }
        public int OwnerId { get; set; }
        public int? LegalPersonId { get; set; }
        public DateTime SinceUtc { get; set; }
        public int? VariationId { get; set; }
        public int? VariationNumber { get; set; }
    }

    private sealed class Detail
    {
        public int Id { get; set; }
        public string? SupplierName { get; set; }
        public decimal? Value { get; set; }
        public string CurrencyCode { get; set; } = "AUD";
        public string? Category { get; set; }
        public DateTime? EndDate { get; set; }
        public bool IsEvergreen { get; set; }
    }

    private sealed record Item(int PersonId, int ContractId, string Reference, string Title, string What, DateOnly? Date, int? Days, int? VariationId, int? VariationNumber, string? Kind = null);

    public static void MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/team", async (int? days, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!(me!.IsAdmin || me.IsAuditor || me.IsGroupHead)) return ApiResults.NotAllowed("The Team page is for admins, auditors and Group Heads.");
            var window = days is 30 or 60 or 90 or 180 ? days.Value : 90;
            var today = DateOnly.FromDateTime(HomeEndpoints.Today(clock));
            var now = clock.GetUtcNow().UtcDateTime;
            await using var c = await db.OpenAsync();
            var p = ContractAccess.Params(me);

            var people = (await c.QueryAsync<(int Id, string Name, bool IsContractOwner, bool IsLegal)>(
                "SELECT Id, Name, IsContractOwner, IsLegal FROM contracts.Person WHERE IsActive = 1")).ToList();
            var items = new List<Item>();

            // Effective contracts: what's coming up, and how many each person owns or backs up.
            var effective = (await c.QueryAsync<RenewalEndpoints.RenewalRow>($"""
                SELECT k.Id, k.Reference, k.Title, k.OwnerId, k.BackupOwnerId, k.EndDate, k.IsEvergreen, k.ReviewDate, k.NoticePeriodDays, k.RenewalDecision, k.PlannedEndDate
                FROM contracts.Contract k WHERE k.Stage = N'Effective' AND {ContractAccess.VisibleSql("k")}
                """, p)).ToList();
            foreach (var r in effective.Where(r => r.RenewalDecision is null))
            {
                var (kind, date) = RenewalEndpoints.KeyDate(r);
                if (date is not { } d) continue;
                var daysAway = d.DayNumber - today.DayNumber;
                if (daysAway > window) continue;
                items.Add(new(r.OwnerId, r.Id, r.Reference, r.Title, daysAway < 0 ? What.Overdue : What.ComingUp, d, daysAway, null, null, kind));
            }

            // Contracts on the way, and open Variations (the contract's owner handles a Variation).
            var open = await c.QueryAsync<OpenRow>($"""
                SELECT k.Id, k.Reference, k.Title, k.Stage, k.WhoseTurn, k.OwnerId, k.LegalPersonId, COALESCE(k.TurnSinceUtc, k.StageSinceUtc) AS SinceUtc,
                       NULL AS VariationId, NULL AS VariationNumber
                FROM contracts.Contract k WHERE k.Stage IN (N'Draft', N'LegalReview', N'Signing', N'Approval') AND {ContractAccess.VisibleSql("k")}
                UNION ALL
                SELECT k.Id, k.Reference, k.Title, v.Stage, v.WhoseTurn, k.OwnerId, v.LegalPersonId, COALESCE(v.TurnSinceUtc, v.StageSinceUtc), v.Id, v.Number
                FROM contracts.Variation v JOIN contracts.Contract k ON k.Id = v.ContractId
                WHERE v.Stage IN (N'Draft', N'LegalReview', N'Approval') AND {ContractAccess.VisibleSql("k")}
                """, p);
            var legalNotPickedUp = 0;
            foreach (var o in open)
            {
                var since = DateOnly.FromDateTime(o.SinceUtc);
                var waited = (int)Math.Max(0, (now - o.SinceUtc).TotalDays);
                void Add(int personId, string what) => items.Add(new(personId, o.Id, o.Reference, o.Title, what, since, waited, o.VariationId, o.VariationNumber));
                var ownersTurn = o.Stage == "Draft" || (o.Stage == "LegalReview" && o.WhoseTurn == "Owner") || o.Stage == "Signing";
                if (ownersTurn)
                    Add(o.OwnerId, o.VariationId is not null ? What.Variation : o.Stage == "Draft" ? What.Draft : o.Stage == "Signing" ? What.Signing : What.BackFromLegal);
                else if (o.Stage == "Approval")
                    Add(o.OwnerId, What.InApproval);
                else if (o.Stage == "LegalReview" && o.WhoseTurn == "Legal")
                {
                    Add(o.OwnerId, What.WithLegal);
                    if (o.LegalPersonId is { } lp) Add(lp, What.LegalTurn);
                    else legalNotPickedUp++;
                }
            }

            // Each contract's supplier, value, category and end date, so a lead can see what it is at a glance.
            var ids = items.Select(i => i.ContractId).Distinct().ToList();
            var details = ids.Count == 0 ? [] : (await c.QueryAsync<Detail>("""
                SELECT k.Id, s.Name AS SupplierName, k.Value, k.CurrencyCode, CASE WHEN parent.Name IS NULL THEN cat.Name ELSE parent.Name + N' > ' + cat.Name END AS Category,
                       k.EndDate, k.IsEvergreen
                FROM contracts.Contract k LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
                LEFT JOIN contracts.Category cat ON cat.Id = k.CategoryId LEFT JOIN contracts.Category parent ON parent.Id = cat.ParentId
                WHERE k.Id IN (SELECT value FROM OPENJSON(@ids))
                """, new { ids = System.Text.Json.JsonSerializer.Serialize(ids) })).ToDictionary(d => d.Id);
            // Supplier work (D-90): open business review actions, and business reviews due for the suppliers each person looks after.
            var supplierWork = (await c.QueryAsync<(int PersonId, string Kind, int SupplierId, string SupplierName, string What, DateTime Due)>("""
                SELECT ISNULL(a.OwnerId, s.RelationshipOwnerId), N'Action', s.Id, s.Name, a.Text, a.DueOn
                FROM contracts.BusinessReviewAction a JOIN contracts.BusinessReview r ON r.Id = a.ReviewId JOIN contracts.Supplier s ON s.Id = r.SupplierId
                WHERE a.DoneUtc IS NULL AND r.IsRemoved = 0 AND a.DueOn <= @until AND ISNULL(a.OwnerId, s.RelationshipOwnerId) IS NOT NULL
                UNION ALL
                SELECT s.RelationshipOwnerId, N'ReviewDue', s.Id, s.Name, N'Business review', s.NextReviewDue FROM contracts.Supplier s
                WHERE s.IsActive = 1 AND s.RelationshipOwnerId IS NOT NULL AND s.NextReviewDue <= @until
                """, new { until = today.AddDays(window).ToDateTime(TimeOnly.MinValue) }))
                .ToLookup(x => x.PersonId);
            var byPerson = items.ToLookup(i => i.PersonId);
            int Count(int id, string what) => byPerson[id].Count(i => i.What == what);
            var rows = people
                .Select(person => new
                {
                    person.Id, person.Name, person.IsContractOwner, person.IsLegal,
                    Overdue = Count(person.Id, What.Overdue),
                    ComingUp = Count(person.Id, What.ComingUp),
                    TheirTurn = byPerson[person.Id].Count(i => i.What is What.Draft or What.BackFromLegal or What.Signing or What.Variation),
                    WaitingOnOthers = byPerson[person.Id].Count(i => i.What is What.WithLegal or What.InApproval),
                    LegalTurn = Count(person.Id, What.LegalTurn),
                    SupplierWork = supplierWork[person.Id].Count(),
                    Suppliers = supplierWork[person.Id].OrderBy(x => x.Due).Select(x =>
                    {
                        var due = DateOnly.FromDateTime(x.Due);
                        return new { x.Kind, x.SupplierId, x.SupplierName, x.What, Due = due, Days = due.DayNumber - today.DayNumber };
                    }),
                    Owns = effective.Count(r => r.OwnerId == person.Id),
                    BacksUp = effective.Count(r => r.BackupOwnerId == person.Id),
                    Items = byPerson[person.Id]
                        .OrderBy(i => i.What switch { What.Overdue => 0, What.ComingUp => 1, What.LegalTurn => 2, What.WithLegal or What.InApproval => 4, _ => 3 })
                        .ThenBy(i => i.What is What.Overdue or What.ComingUp ? i.Days : -i.Days)
                        .Select(i =>
                        {
                            var d = details.GetValueOrDefault(i.ContractId);
                            return new
                            {
                                i.ContractId, i.Reference, i.Title, i.What, i.Kind, i.Date, i.Days, i.VariationId, i.VariationNumber,
                                d?.SupplierName, d?.Value, CurrencyCode = d?.CurrencyCode ?? "AUD", d?.Category,
                                EndDate = d?.EndDate is { } e ? DateOnly.FromDateTime(e) : (DateOnly?)null, IsEvergreen = d?.IsEvergreen ?? false,
                            };
                        }),
                })
                // Only people with something to show: contract owners, Legal, and anyone who still owns or has work.
                .Where(r => r.IsContractOwner || r.IsLegal || r.Owns > 0 || r.BacksUp > 0 || r.Items.Any() || r.SupplierWork > 0)
                .OrderByDescending(r => r.Overdue).ThenByDescending(r => r.ComingUp + r.TheirTurn + r.LegalTurn + r.SupplierWork).ThenBy(r => r.Name)
                .ToList();

            return Results.Ok(new
            {
                days = window,
                today,
                totals = new
                {
                    overdue = rows.Sum(r => r.Overdue), comingUp = rows.Sum(r => r.ComingUp), theirTurn = rows.Sum(r => r.TheirTurn),
                    legalTurn = rows.Sum(r => r.LegalTurn), legalNotPickedUp,
                },
                people = rows,
            });
        });
    }
}
