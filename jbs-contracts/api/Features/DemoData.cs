using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Fictional demo data so everyone testing on QA has something to work with (D-66, D-97). Admins only, and only where AllowDemoData is
/// on (QA and local; never Production). Loading it again ("Refresh demo data") archives the previous demo contracts and loads a fresh set
/// dated from today. Items are shared out across everyone active, by role: each owner gets drafts, things back from Legal, notice
/// deadlines and renewals; each Legal person gets reviews; each approver gets approvals; Procurement gets suppliers to look after.
/// Demo contracts and suppliers are marked IsDemo, so they never send email. Names of demo people end in "(demo)" and use example.com.
/// Everything is loaded in one transaction and audited. Nothing anyone added themselves is changed.
/// </summary>
public static class DemoData
{
    private const string Marker = "Southern Cross Freight (demo)";
    public const string ImportedFrom = "Medius export (demo).xlsx";
    private const int MaxPerRole = 15;

    public static async Task<bool> IsLoadedAsync(Db db)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Contract WHERE IsDemo = 1 AND Stage <> N'Archived'") > 0;
    }

    public static void MapDemoDataEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/demo-data", async (CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock, ILoggerFactory logs) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsAdmin) return ApiResults.NotAllowed("Only admins can load demo data.");
            if (!settings.AllowDemoData) return ApiResults.NotAllowed("Demo data can't be loaded here. It's only for QA.");
            if (settings.DocumentsDirectory is null) return ApiResults.Problem(StatusCodes.Status500InternalServerError, "no-data-folder", "The data folder isn't set up, so demo documents can't be saved.");

            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var seeder = new Seeder(c, tx, me, audit, settings.DocumentsDirectory, HomeEndpoints.Today(clock), clock.GetUtcNow().UtcDateTime, settings.DefaultDomain ?? "DEMO");
            int archived;
            try
            {
                archived = await seeder.ArchiveOldAsync();
                await seeder.RunAsync();
                await audit.WriteAsync(c, tx, me.AsActor(), archived > 0 ? "demo.refreshed" : "demo.loaded", "DemoData", null,
                    (archived > 0 ? $"{me.Name} refreshed the demo data: archived {archived} old demo contracts and loaded {seeder.Contracts} new ones"
                                  : $"{me.Name} loaded the demo data: {seeder.Contracts} fictional contracts")
                    + $", shared out across {seeder.People} people, with suppliers, Variations and imported contracts. Demo items never send email.");
                await tx.CommitAsync();
            }
            catch
            {
                seeder.DeleteFiles(logs.CreateLogger("DemoData"));
                throw;
            }
            return Results.Ok(new { contracts = seeder.Contracts, archived, people = seeder.People });
        });
    }

    private sealed record Spec(string Title, int? Supplier, int? Category, int? AgreementType, int? BusinessUnit, int? Site, int Owner, int? Backup,
        decimal? Value, DateTime? Start, DateTime? End, int? Notice, bool Legal, bool Evergreen = false, DateTime? Review = null, bool Confidential = false,
        int? AutoRenewMonths = null);

    private sealed class Seeder(SqlConnection c, SqlTransaction tx, Person me, AuditLog audit, string documents, DateTime today, DateTime now, string domain)
    {
        private readonly List<string> written = [];
        public int Contracts { get; private set; }
        public int People { get; private set; }

        private DateTime D(int days) => today.AddDays(days);
        private DateTime Ago(double days) => now.AddDays(-days);

        // Filled by ListsAsync.
        private int linehaul, coldChain, packaging, facilities, cleaning, waste, it, peopleServices, services, supply, beef, corporate, north, south, office;
        private int meatsEntity, foodsEntity, procurementFn, operationsFn, logisticsFn;
        private int scf, bluegum, harbour, ironbark, coastal, redgum, keystone, wattle;
        private int priya, sam, leah, grant;
        private int? groupHead;
        private (string Title, int Supplier, int Category, decimal Value)[] pool = [];

        // ------------------------------------------------------------ Refresh: the previous set is archived (nothing is deleted)

        public async Task<int> ArchiveOldAsync()
        {
            var old = (await c.QueryAsync<(int Id, string Reference)>(
                "SELECT Id, Reference FROM contracts.Contract WITH (UPDLOCK) WHERE IsDemo = 1 AND Stage <> N'Archived'", transaction: tx)).ToList();
            await c.ExecuteAsync("""
                UPDATE contracts.Variation SET Stage = N'Withdrawn', StageSinceUtc = SYSUTCDATETIME(), WhoseTurn = NULL, ClosedReason = N'The demo data was refreshed.', UpdatedUtc = SYSUTCDATETIME()
                WHERE Stage IN (N'Draft', N'LegalReview', N'Approval') AND ContractId IN (SELECT Id FROM contracts.Contract WHERE IsDemo = 1);
                UPDATE contracts.Unlock SET ClosedUtc = SYSUTCDATETIME(), ClosedById = @me
                WHERE ClosedUtc IS NULL AND ContractId IN (SELECT Id FROM contracts.Contract WHERE IsDemo = 1);
                UPDATE contracts.Contract SET Stage = N'Archived', StageSinceUtc = SYSUTCDATETIME(), IsLocked = 1, WhoseTurn = NULL, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                WHERE IsDemo = 1 AND Stage <> N'Archived';
                UPDATE contracts.SupplierDocument SET IsRemoved = 1, UpdatedUtc = SYSUTCDATETIME() WHERE IsRemoved = 0 AND SupplierId IN (SELECT Id FROM contracts.Supplier WHERE IsDemo = 1);
                UPDATE contracts.BusinessReview SET IsRemoved = 1, UpdatedUtc = SYSUTCDATETIME() WHERE IsRemoved = 0 AND SupplierId IN (SELECT Id FROM contracts.Supplier WHERE IsDemo = 1);
                UPDATE contracts.SupplierContact SET IsRemoved = 1, UpdatedUtc = SYSUTCDATETIME() WHERE IsRemoved = 0 AND SupplierId IN (SELECT Id FROM contracts.Supplier WHERE IsDemo = 1);
                """, new { me = me.Id }, tx);
            foreach (var (id, reference) in old)
                await audit.WriteAsync(c, tx, me.AsActor(), "demo.archived", "Contract", id,
                    $"{me.Name} refreshed the demo data, so the demo contract {reference} was archived.", contractId: id);
            return old.Count;
        }

        // ------------------------------------------------------------ The fresh set

        public async Task RunAsync()
        {
            // Demo people (they can't sign in). No Group Head is set, so the real one isn't moved.
            priya = await PersonAsync("Priya Patel (demo)", "demo.ppatel", owner: true);
            sam = await PersonAsync("Sam Nguyen (demo)", "demo.snguyen", legal: true);
            leah = await PersonAsync("Leah Brooks (demo)", "demo.lbrooks", legal: true);
            grant = await PersonAsync("Grant Wallace (demo)", "demo.gwallace", approver: true);
            groupHead = await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Person WHERE IsGroupHead = 1 AND IsActive = 1", transaction: tx);
            await ListsAsync();

            // Everyone active, the person loading it first.
            var everyone = (await c.QueryAsync<Person>(
                "SELECT * FROM contracts.Person WHERE IsActive = 1 ORDER BY CASE WHEN Id = @me THEN 0 ELSE 1 END, Name", new { me = me.Id }, tx)).ToList();
            var owners = everyone.Where(p => p.IsContractOwner).Take(MaxPerRole).ToList();
            var legals = everyone.Where(p => p.IsLegal).Take(MaxPerRole).ToList();
            var approvers = everyone.Where(p => p.IsApprover || p.IsGroupHead).Take(MaxPerRole).ToList();
            var procurement = everyone.Where(p => p.IsProcurement).Take(MaxPerRole).ToList();
            People = everyone.Count(p => p.IsContractOwner || p.IsLegal || p.IsApprover || p.IsGroupHead || p.IsProcurement);

            var main = me.IsContractOwner ? me.Id : priya;
            await StagesAsync(main);
            await RenewalsAsync(main);
            await VariationsAsync(main);
            await ImportedAsync(main);
            for (var i = 0; i < owners.Count; i++) await ForOwnerAsync(owners[i], i, legals);
            for (var i = 0; i < legals.Count; i++) await ForLegalAsync(legals[i], i, owners);
            for (var i = 0; i < approvers.Count; i++) await ForApproverAsync(approvers[i], i, owners);
            await SuppliersAsync(procurement.Count > 0 ? procurement : [me]);
        }

        private async Task ListsAsync()
        {
            var freight = await CategoryAsync("Freight", null);
            linehaul = await CategoryAsync("Linehaul", freight);
            coldChain = await CategoryAsync("Cold chain", freight);
            packaging = await CategoryAsync("Packaging", null);
            facilities = await CategoryAsync("Facilities", null);
            cleaning = await CategoryAsync("Cleaning", facilities);
            waste = await CategoryAsync("Waste", facilities);
            it = await CategoryAsync("IT services", null);
            peopleServices = await CategoryAsync("People services", null);
            // Supplier tiers from spend categories (D-108): suppliers get the highest tier among their contracts' categories.
            foreach (var (category, tierName) in new[] { (freight, "Strategic"), (packaging, "Preferred"), (it, "Preferred"), (facilities, "Approved"), (peopleServices, "Transactional") })
                await c.ExecuteAsync("""
                    UPDATE contracts.Category SET TierId = (SELECT TOP (1) Id FROM contracts.ListItem WHERE ListName = N'SupplierTier' AND ValueKey = UPPER(@tierName) AND IsActive = 1)
                    WHERE Id = @category AND TierId IS NULL
                    """, new { category, tierName }, tx);
            services = await ItemAsync("AgreementType", "Services agreement");
            supply = await ItemAsync("AgreementType", "Supply agreement");
            beef = await ItemAsync("BusinessUnit", "Beef");
            corporate = await ItemAsync("BusinessUnit", "Corporate");
            north = await ItemAsync("Site", "North plant");
            south = await ItemAsync("Site", "South plant");
            office = await ItemAsync("Site", "Head office");
            meatsEntity = await ItemAsync("Entity", "Northern Meats Pty Ltd (demo)");
            foodsEntity = await ItemAsync("Entity", "Southern Foods Pty Ltd (demo)");
            procurementFn = await ItemAsync("Function", "Procurement");
            operationsFn = await ItemAsync("Function", "Operations");
            logisticsFn = await ItemAsync("Function", "Logistics");
            scf = await SupplierAsync(Marker);
            bluegum = await SupplierAsync("Bluegum Packaging (demo)");
            harbour = await SupplierAsync("Harbourside Cleaning (demo)");
            ironbark = await SupplierAsync("Ironbark IT Services (demo)");
            coastal = await SupplierAsync("Coastal Cold Storage (demo)");
            redgum = await SupplierAsync("Redgum Uniforms (demo)");
            keystone = await SupplierAsync("Keystone Security (demo)");
            wattle = await SupplierAsync("Wattle Pest Control (demo)");
            pool =
            [
                ("Linehaul freight Melbourne to Brisbane", scf, linehaul, 1_850_000m),
                ("Cold room maintenance", coastal, coldChain, 140_000m),
                ("Shrink bags and liners", bluegum, packaging, 620_000m),
                ("Amenities cleaning", harbour, cleaning, 210_000m),
                ("Organic waste removal", harbour, waste, 88_000m),
                ("Laptop leasing", ironbark, it, 340_000m),
                ("Workwear and PPE", redgum, peopleServices, 275_000m),
                ("After-hours security", keystone, facilities, 160_000m),
                ("Bird and pest management", wattle, facilities, 42_000m),
                ("Chilled container hire", coastal, coldChain, 510_000m),
                ("Carton board supply", bluegum, packaging, 1_240_000m),
                ("Help desk support", ironbark, it, 230_000m),
            ];
        }

        private Spec FromPool(int index, int owner, DateTime? start, DateTime? end, int? notice, bool legal, int? backup = null)
        {
            var (title, supplier, category, value) = pool[index % pool.Length];
            var site = (index % 3) switch { 0 => north, 1 => south, _ => office };
            return new(title, supplier, category, index % 2 == 0 ? services : supply, category == it ? corporate : beef, site, owner, backup, value, start, end, notice, legal);
        }

        /// <summary>Someone other than this person who owns contracts (cycling through them), else the demo owner.</summary>
        private int OwnerOtherThan(List<Person> owners, int notId, int i)
        {
            var others = owners.Where(o => o.Id != notId).ToList();
            return others.Count > 0 ? others[i % others.Count].Id : priya != notId ? priya : me.Id;
        }

        private int LegalFor(List<Person> legals, int ownerId, int i)
        {
            var others = legals.Where(l => l.Id != ownerId).ToList();
            return others.Count > 0 ? others[i % others.Count].Id : i % 2 == 0 ? sam : leah;
        }

        // ------------------------------------------------------------ Every stage, once (the main owner's)

        private async Task StagesAsync(int owner)
        {
            // Drafts: one ready, one missing required details.
            await ContractAsync(new("Office cleaning 2027", harbour, cleaning, services, corporate, office, owner, null, 185_000m, D(30), D(30 + 730), 60, Legal: true));
            await ContractAsync(new("Pallet wrap supply", null, null, supply, beef, north, owner, null, null, null, null, null, Legal: true));

            // Legal: two new for anyone in Legal to pick up.
            var linehaulId = await ContractAsync(new("Linehaul freight Brisbane to Sydney", scf, linehaul, services, beef, south, owner, priya == owner ? null : priya, 1_350_000m, D(45), D(45 + 1095), 90, Legal: true));
            await LegalReviewAsync(linehaulId, since: 2, whoseTurn: "Legal", legalPerson: null, status: "NotStarted", note: null,
                [("SentToLegal", owner, 2, "Legal", null, "NotStarted", "New three-year linehaul agreement. Supplier wants a fuel levy clause.", false)]);
            var cateringId = await ContractAsync(new("Staff canteen catering", harbour, peopleServices, services, beef, north, priya, null, 380_000m, D(40), D(40 + 730), 60, Legal: true));
            await LegalReviewAsync(cateringId, since: 0.5, whoseTurn: "Legal", legalPerson: null, status: "NotStarted", note: null,
                [("SentToLegal", priya, 0.5, "Legal", null, "NotStarted", "Catering for both plants. Food safety clauses need a look.", false)]);

            // Legal: waiting on the supplier.
            var coldId = await ContractAsync(new("Cold storage overflow", coastal, coldChain, services, beef, north, priya, owner == priya ? null : owner, 640_000m, D(20), D(20 + 365), 60, Legal: true));
            await LegalReviewAsync(coldId, since: 6, whoseTurn: "Legal", legalPerson: sam, status: "WaitingOnSupplier", note: "Insurance certificate requested",
                [("SentToLegal", priya, 6, "Legal", null, "NotStarted", "Short-term overflow storage for the peak season.", false),
                 ("Taken", sam, 5, "Legal", sam, "Reviewing", null, false),
                 ("StatusChanged", sam, 4, "Legal", sam, "WaitingOnSupplier", "Insurance certificate requested", false)]);

            // Signing: sent back from approval with a reason.
            var wasteId = await ContractAsync(new("Waste collection South plant", harbour, waste, services, beef, south, owner, null, 96_000m, D(7), D(7 + 730), 30, Legal: true));
            await ClearedAsync(wasteId, leah, daysAgo: 10, "Cleared. Indemnity wording updated.");
            await DocumentAsync(wasteId, "InternalSignOff", "Cover page - sign off.pdf", owner, 5);
            await SignedCopyAsync(wasteId, owner, 4);
            await c.ExecuteAsync("UPDATE contracts.Contract SET EffectiveDate = @d WHERE Id = @wasteId", new { d = D(7), wasteId }, tx);
            await ApprovalAsync(wasteId, null, "SentBack", grant, groupHead, daysAgo: 2, "The signed copy is missing page 4 (the schedule of rates).");
            await StageAsync(wasteId, "Signing", 2);

            // Effective: some further out, an evergreen with a review date, a Confidential one, and one unlocked right now by the loader.
            await EffectiveAsync(new("Forklift servicing", scf, facilities, services, beef, north, owner, null, 74_000m, D(-300), D(40), 20, Legal: false, AutoRenewMonths: 12), grant);
            await EffectiveAsync(new("Software licences", ironbark, it, supply, corporate, office, owner, null, 120_000m, D(-500), null, 30, Legal: false, Evergreen: true, Review: D(25)), grant);
            await EffectiveAsync(new("Executive recruitment services", ironbark, peopleServices, services, corporate, office, owner, null, 300_000m, D(-90), D(275), 30, Legal: true, Confidential: true), grant, sam);
            var securityId = await EffectiveAsync(new("Security patrols", keystone, facilities, services, corporate, office, owner, null, 155_000m, D(-305), D(260), 60, Legal: true), grant, sam);
            if (me.IsAdmin)
                await c.ExecuteAsync("""
                    INSERT INTO contracts.Unlock (ContractId, ById, Reason, StartedUtc, ExpiresUtc) VALUES (@securityId, @me, @reason, @started, @expires)
                    """, new { securityId, me = me.Id, reason = "Demo: correcting the supplier's ABN, which was typed wrongly when it was registered.", started = Ago(0.02), expires = now.AddHours(3) }, tx);

            // Withdrawn.
            var canteenId = await ContractAsync(new("Canteen services", harbour, facilities, services, beef, south, priya, null, 450_000m, null, null, null, Legal: true));
            await c.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Withdrawn', StageSinceUtc = @at, IsLocked = 1, WithdrawnReason = N'The supplier withdrew their quote.' WHERE Id = @canteenId",
                new { at = Ago(20), canteenId }, tx);
        }

        // ------------------------------------------------------------ Renewals and endings

        private async Task RenewalsAsync(int owner)
        {
            // Notice deadlines in 5 days and passed, with no decision (Urgent).
            await EffectiveAsync(new("Pest control", wattle, facilities, services, beef, south, owner, null, 38_000m, D(-355), D(10), 5, Legal: false), grant);
            await EffectiveAsync(new("Uniform laundering", redgum, peopleServices, services, beef, north, owner, null, 64_000m, D(-700), D(25), 30, Legal: false, AutoRenewMonths: 24), grant);

            // Decisions made: let it end, renegotiate, and end early on a later date.
            var letEnd = await EffectiveAsync(new("Fuel cards", scf, linehaul, supply, beef, south, owner, null, 90_000m, D(-700), D(35), 30, Legal: false), grant);
            await DecisionAsync(letEnd, "LetItEnd", owner, "Moving fuel to the new whole-of-group arrangement.");
            var renegotiate = await EffectiveAsync(new("Packaging design services", bluegum, packaging, services, corporate, office, owner, null, 150_000m, D(-650), D(70), 60, Legal: true), grant, leah);
            await DecisionAsync(renegotiate, "Renegotiate", owner, "Rates are 12% above market. Asking for a better price before deciding.");
            var endEarly = await EffectiveAsync(new("Courier services", scf, linehaul, services, corporate, office, owner, null, 55_000m, D(-200), D(530), 30, Legal: false), grant);
            var p = new DynamicParameters(new { endEarly, owner, reason = "Service has been poor for three months; ending under clause 18." });
            p.Add("planned", D(25), System.Data.DbType.Date);
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET PlannedEndDate = @planned, EndedReason = @reason, EndedById = @owner, RenewalDecision = N'LetItEnd',
                    RenewalDecisionNote = @reason, RenewalDecisionUtc = SYSUTCDATETIME(), RenewalDecisionById = @owner
                WHERE Id = @endEarly
                """, p, tx);

            // A renewal under way: the old contract (Renew) and its renewal draft.
            var old = await EffectiveAsync(new("Refrigerated transport", coastal, coldChain, services, beef, north, owner, null, 2_400_000m, D(-1000), D(60), 90, Legal: true), grant, leah);
            await DecisionAsync(old, "Renew", owner, "Good service and fair rates. Renewing for three years.");
            var renewal = await ContractAsync(new("Refrigerated transport (renewal)", coastal, coldChain, services, beef, north, owner, null, 2_550_000m, D(61), D(61 + 1095), 90, Legal: true));
            await c.ExecuteAsync("UPDATE contracts.Contract SET RenewsContractId = @old WHERE Id = @renewal", new { old, renewal }, tx);

            // Ended last month, and archived long ago.
            var ended = await EffectiveAsync(new("Temporary labour hire", redgum, peopleServices, services, beef, south, owner, null, 420_000m, D(-760), D(-20), 30, Legal: false), grant);
            await EndAsync(ended, D(-20), "Let it end: it reached its end date.", "Ended");
            var archived = await EffectiveAsync(new("Printer fleet", ironbark, it, supply, corporate, office, owner, null, 60_000m, D(-1500), D(-400), 30, Legal: false), grant);
            await EndAsync(archived, D(-400), "Let it end: replaced by the managed print service.", "Archived");
        }

        private async Task DecisionAsync(int id, string decision, int by, string note) =>
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET RenewalDecision = @decision, RenewalDecisionNote = @note, RenewalDecisionUtc = @at, RenewalDecisionById = @by WHERE Id = @id
                """, new { id, decision, note, at = Ago(3), by }, tx);

        private async Task EndAsync(int id, DateTime endedOn, string reason, string stage)
        {
            var p = new DynamicParameters(new { id, reason, stage, at = now.AddDays((endedOn - today).TotalDays) });
            p.Add("endedOn", endedOn, System.Data.DbType.Date);
            await c.ExecuteAsync("UPDATE contracts.Contract SET Stage = @stage, StageSinceUtc = @at, EndedOn = @endedOn, EndedReason = @reason WHERE Id = @id", p, tx);
        }

        // ------------------------------------------------------------ Variations

        private async Task VariationsAsync(int owner)
        {
            // Approved and applied: the history of a change.
            var cartons = await EffectiveAsync(new("Cartons and trays", bluegum, packaging, supply, beef, north, owner, null, 1_250_000m, D(-400), D(700), 90, Legal: true), grant, sam);
            var vid = await VariationAsync(cartons, "Approved", "Volumes up 10% with the new South plant line; price per unit unchanged.", new Dictionary<string, object> { ["value"] = 1_250_000m },
                owner, daysAgo: 60, legal: false,
                applied: JsonSerializer.Serialize(new[] { new { Key = "value", Label = "Value", Old = "$1,120,000.00", New = "$1,250,000.00" } }));
            await DocumentAsync(cartons, "Variation", "Variation 1 - volumes (signed).pdf", owner, 50, vid);
            await ApprovalAsync(cartons, vid, "Approved", grant, groupHead, daysAgo: 45, null);
        }

        private async Task<int> VariationAsync(int contractId, string stage, string reason, Dictionary<string, object> changes, int owner, double daysAgo, bool legal,
            string? whoseTurn = null, int? legalPerson = null, string? applied = null)
        {
            var number = await c.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(Number), 0) + 1 FROM contracts.Variation WHERE ContractId = @contractId", new { contractId }, tx);
            var id = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Variation (ContractId, Number, Reason, ChangesJson, NeedsLegalReview, Stage, StageSinceUtc, WhoseTurn, LegalPersonId, TurnSinceUtc,
                    AppliedChangesJson, CreatedUtc, CreatedById, UpdatedUtc)
                OUTPUT INSERTED.Id
                VALUES (@contractId, @number, @reason, @changes, @legal, @stage, @since, @whoseTurn, @legalPerson, @turnSince, @applied, @created, @owner, @since)
                """, new
            {
                contractId, number, reason, changes = JsonSerializer.Serialize(changes), legal, stage, since = Ago(daysAgo / 2), whoseTurn, legalPerson,
                turnSince = whoseTurn is null ? (DateTime?)null : Ago(1), applied, created = Ago(daysAgo), owner,
            }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "demo.variation", "Variation", id, $"{me.Name} loaded demo Variation {number} (fictional).", contractId: contractId);
            return id;
        }

        // ------------------------------------------------------------ Imported from the old system

        private async Task ImportedAsync(int owner)
        {
            var linen = await ImportAsync(new("Linen hire", harbour, cleaning, services, beef, north, owner, null, 96_000m, D(-500), D(230), 60, Legal: false), "Effective");
            await ImportedFilesAsync(linen, ["signed", "cover", "draft"]);
            var trailers = await ImportAsync(new("Refrigerated trailers", coastal, coldChain, services, beef, south, priya, null, 780_000m, D(-1200), D(-60), 90, Legal: false), "Ended");
            await ImportedFilesAsync(trailers, ["signed", "variation", "cover"]);
            var couriers = await ImportAsync(new("Interstate couriers", scf, linehaul, services, corporate, office, owner, null, 130_000m, D(-300), D(65), 30, Legal: false), "Effective");
            await ImportedFilesAsync(couriers, ["signed", "variation", "supporting"]);
        }

        private async Task<int> ImportAsync(Spec s, string stage)
        {
            var id = await ContractAsync(s);
            var legacy = $"MED-{80000 + id}";
            var p = new DynamicParameters(new { id, stage, legacy, me = me.Id, imported = Ago(2), from = ImportedFrom, registered = s.Start!.Value.Date.AddHours(2) }); // midday Brisbane
            p.Add("effective", s.Start, System.Data.DbType.Date);
            p.Add("endedOn", stage == "Ended" ? s.End : null, System.Data.DbType.Date);
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = @stage, StageSinceUtc = @imported, IsLocked = 1, EffectiveDate = @effective, RegisteredUtc = @registered, RegisteredById = @me,
                    LegacyReference = @legacy, ImportedUtc = @imported, ImportedFrom = @from,
                    EndedOn = @endedOn, EndedReason = CASE WHEN @stage = N'Ended' THEN N'Imported as ended: Expired in the old system.' END
                WHERE Id = @id
                """, p, tx);
            return id;
        }

        private async Task ImportedFilesAsync(int id, string[] kinds)
        {
            var legacy = await c.ExecuteScalarAsync<string>("SELECT LegacyReference FROM contracts.Contract WHERE Id = @id", new { id }, tx);
            foreach (var k in kinds)
            {
                var (kind, name) = k switch
                {
                    "signed" => ("SignedCopy", $"{legacy} agreement signed.pdf"),
                    "cover" => ("InternalSignOff", $"{legacy} Cover page - sign off.pdf"),
                    "draft" => ("Draft", $"{legacy} agreement draft v3.pdf"),
                    "variation" => ("Variation", $"{legacy} Deed of variation 1.pdf"),
                    _ => ("Supporting", $"{legacy} rate card.pdf"),
                };
                await DocumentAsync(id, kind, name, me.Id, 2);
            }
        }

        // ------------------------------------------------------------ Something for each person, by role

        private async Task ForOwnerAsync(Person o, int i, List<Person> legals)
        {
            var at = i * 4;
            var legal = LegalFor(legals, o.Id, i);
            // A draft.
            await ContractAsync(FromPool(at, o.Id, D(30), D(30 + 730), 60, legal: true));
            // Back from Legal: their turn.
            var back = await ContractAsync(FromPool(at + 1, o.Id, D(21), D(21 + 1095), 90, legal: true));
            await LegalReviewAsync(back, since: 1, whoseTurn: "Owner", legalPerson: legal, status: "WaitingOnOwner", note: null,
                [("SentToLegal", o.Id, 7, "Legal", null, "NotStarted", "Please review; the supplier has sent their standard terms.", false),
                 ("Taken", legal, 6, "Legal", legal, "Reviewing", null, false),
                 ("SentToOwner", legal, 1, "Owner", legal, "WaitingOnOwner", "Liability is uncapped in clause 14. Please ask the supplier for a cap of $2m. My mark-up is attached.", true)]);
            // A notice deadline coming up with no renewal decision yet.
            await EffectiveAsync(FromPool(at + 2, o.Id, D(-340), D(45 + i % 15), 30, legal: false), grant);
            // And one more, so different people see different things.
            switch (i % 3)
            {
                case 0: // Legal cleared it: sign it and send it for approval.
                    var signing = await ContractAsync(FromPool(at + 3, o.Id, D(14), D(14 + 730), 60, legal: true));
                    await ClearedAsync(signing, legal, daysAgo: 2, "Cleared. Standard terms; price review clause agreed.");
                    await DocumentAsync(signing, "FinalAgreement", "Final agreement for signing.pdf", legal, 2);
                    break;
                case 1: // An evergreen contract due for review.
                    await EffectiveAsync(FromPool(at + 3, o.Id, D(-800), null, 30, legal: false) with { Evergreen = true, Review = D(45 + i % 10) }, grant);
                    break;
                default: // A Variation they've started.
                    var varied = await EffectiveAsync(FromPool(at + 3, o.Id, D(-200), D(530), 60, legal: false), grant);
                    await VariationAsync(varied, "Draft", "Extend by 12 months at the same rates, as agreed with the supplier.",
                        new Dictionary<string, object> { ["endDate"] = D(530 + 365).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, o.Id, daysAgo: 2, legal: true);
                    break;
            }
        }

        private async Task ForLegalAsync(Person l, int i, List<Person> owners)
        {
            var owner = OwnerOtherThan(owners, l.Id, i);
            var at = 5 + i * 3;
            // A review on their desk.
            var review = await ContractAsync(FromPool(at, owner, D(30), D(30 + 1095), 90, legal: true));
            await LegalReviewAsync(review, since: 3, whoseTurn: "Legal", legalPerson: l.Id, status: "Reviewing", note: null,
                [("SentToLegal", owner, 4, "Legal", null, "NotStarted", "New agreement on the supplier's paper. Payment terms are 7 days; we need 30.", false),
                 ("Taken", l.Id, 3, "Legal", l.Id, "Reviewing", null, false)]);
            // A Variation with them.
            var varied = await EffectiveAsync(FromPool(at + 1, owner, D(-400), D(330), 60, legal: false), grant);
            var vid = await VariationAsync(varied, "LegalReview", "Add the North plant to the service area, with a new rate schedule.",
                new Dictionary<string, object> { ["value"] = pool[(at + 1) % pool.Length].Value + 60_000m }, owner, daysAgo: 4, legal: true, whoseTurn: "Legal", legalPerson: l.Id);
            await TurnAsync(varied, 1, "SentToLegal", owner, 4, "Legal", null, "NotStarted", "New schedule of rates attached.", [], vid);
            await TurnAsync(varied, 2, "Taken", l.Id, 3, "Legal", l.Id, "Reviewing", null, [], vid);
        }

        private async Task ForApproverAsync(Person a, int i, List<Person> owners)
        {
            var owner = OwnerOtherThan(owners, a.Id, i);
            var at = 8 + i * 2;
            // Signed and waiting for approval (raised and owned by someone else, so they can approve it).
            var waiting = await ContractAsync(FromPool(at, owner, D(-1), D(-1 + 1095), 90, legal: true));
            await ClearedAsync(waiting, i % 2 == 0 ? sam : leah, daysAgo: 8, "Cleared. Volume commitments are within policy.");
            await DocumentAsync(waiting, "InternalSignOff", "Cover page - sign off.pdf", owner, 3);
            await SignedCopyAsync(waiting, owner, 1);
            await c.ExecuteAsync("UPDATE contracts.Contract SET EffectiveDate = @d WHERE Id = @waiting", new { d = D(-1), waiting }, tx);
            await StageAsync(waiting, "Approval", 1);
            // A Variation waiting for approval (the first few approvers).
            if (i < 3)
            {
                var varied = await EffectiveAsync(FromPool(at + 1, owner, D(-500), D(230), 60, legal: false), grant);
                var vid = await VariationAsync(varied, "Approval", "Extend to the end of next financial year while the tender runs.",
                    new Dictionary<string, object> { ["endDate"] = D(230 + 180).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, owner, daysAgo: 3, legal: false);
                await DocumentAsync(varied, "Variation", "Variation letter (signed).pdf", owner, 1, vid);
            }
        }

        // ------------------------------------------------------------ Suppliers (Procurement)

        private async Task SuppliersAsync(List<Person> looking)
        {
            var tier = async (string name) => await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.ListItem WHERE ListName = N'SupplierTier' AND Value = @name", new { name }, tx);
            var type = async (string name) => await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.ListItem WHERE ListName = N'SupplierDocumentType' AND Value = @name", new { name }, tx);
            int[] order = [scf, coastal, bluegum, harbour, ironbark, redgum, keystone, wattle];
            string[] tiers = ["Strategic", "Strategic", "Preferred", "Approved", "Preferred", "Approved", "Transactional", "Transactional"];
            for (var i = 0; i < order.Length; i++)
            {
                var risk = i switch { 1 => "High", 4 => "Medium", _ => "Low" };
                var reason = risk switch { "High" => "Single cold storage provider for the North plant; no backup site.", "Medium" => "Key systems depend on them; contract up for tender next year.", _ => null };
                // Tiers come from their contracts' categories (D-108); one is set by hand, to show both.
                var p = new DynamicParameters(new { id = order[i], tierId = i == 6 ? await tier(tiers[i]) : null, risk, reason });
                p.Add("next", i < looking.Count ? D(20 + i * 5) : (DateTime?)null, System.Data.DbType.Date);
                await c.ExecuteAsync("""
                    UPDATE contracts.Supplier SET TierId = @tierId, RiskRating = @risk, RiskReason = @reason, NextReviewDue = @next, RelationshipOwnerId = NULL, IsDemo = 1 WHERE Id = @id
                    """, p, tx);
                await c.ExecuteAsync("""
                    INSERT INTO contracts.SupplierContact (SupplierId, Name, Role, Email, Phone, CreatedById) VALUES
                        (@id, N'Jordan Lee (demo)', N'Account manager', N'jordan.lee@example.com', N'07 3000 0000', @me),
                        (@id, N'Casey Brown (demo)', N'Accounts', N'accounts@example.com', NULL, @me)
                    """, new { id = order[i], me = me.Id }, tx);
            }

            // Each Procurement person looks after a supplier: documents expiring, a review with actions, the next review coming up.
            for (var i = 0; i < looking.Count; i++)
            {
                var p = looking[i];
                var supplierId = order[i % order.Length];
                await c.ExecuteAsync("UPDATE contracts.Supplier SET RelationshipOwnerId = @owner WHERE Id = @supplierId", new { owner = p.Id, supplierId }, tx);
                var insurance = await SupplierDocumentAsync(supplierId, await type("Insurance certificate"), "Public liability insurance certificate", D(25), p.Id);
                var food = await SupplierDocumentAsync(supplierId, await type("Food safety or quality certificate"), "HACCP certificate", D(-10), p.Id);
                await SupplierDocumentAsync(supplierId, await type("Modern slavery statement"), "Modern slavery statement 2026", null, p.Id);
                foreach (var d in new[] { insurance, food })
                    await c.ExecuteAsync("INSERT INTO contracts.SupplierDocumentWatch (DocumentId, PersonId) VALUES (@d, @p)", new { d, p = p.Id }, tx);

                var reviewId = await c.ExecuteScalarAsync<int>("""
                    INSERT INTO contracts.BusinessReview (SupplierId, HeldOn, Summary, SupplierAttendees, Rating, QualityScore, DeliveryScore, ServiceScore, CostScore, CreatedUtc, CreatedById)
                    OUTPUT INSERTED.Id
                    VALUES (@supplierId, @held, @summary, NULL, N'NeedsImprovement', 4, 3, 3, 4, @at, @by)
                    """, new
                {
                    supplierId, held = D(-85), at = Ago(84), by = p.Id,
                    summary = "Quarterly review. Delivery in full and on time was 91% (target 97%). Agreed a recovery plan; quality and pricing are on track.",
                }, tx);
                foreach (var who in new[] { p.Id, me.Id }.Distinct())
                    await c.ExecuteAsync("INSERT INTO contracts.BusinessReviewAttendee (ReviewId, PersonId) VALUES (@reviewId, @who)", new { reviewId, who }, tx);
                // The supplier's account manager attended, and owes an action (D-104).
                var jordan = await c.ExecuteScalarAsync<int?>("SELECT TOP (1) Id FROM contracts.SupplierContact WHERE SupplierId = @supplierId AND IsRemoved = 0 AND Name = N'Jordan Lee (demo)' ORDER BY Id DESC", new { supplierId }, tx);
                if (jordan is not null) await c.ExecuteAsync("""
                    INSERT INTO contracts.BusinessReviewContact (ReviewId, ContactId) VALUES (@reviewId, @jordan);
                    INSERT INTO contracts.BusinessReviewAction (ReviewId, Text, SupplierContactId, DueOn, CreatedUtc) VALUES (@reviewId, N'Send the written delivery recovery plan', @jordan, @supplierDue, @at);
                    """, new { reviewId, jordan, supplierDue = D(12), at = Ago(84) }, tx);
                await c.ExecuteAsync("""
                    INSERT INTO contracts.BusinessReviewAction (ReviewId, Text, OwnerId, DueOn, CreatedUtc) VALUES (@reviewId, N'Check the delivery recovery plan figures with the plant', @owner, @due, @at);
                    INSERT INTO contracts.BusinessReviewAction (ReviewId, Text, OwnerId, DueOn, DoneUtc, DoneById, CreatedUtc) VALUES (@reviewId, N'Send the supplier our forecast volumes', @owner, @past, @done, @owner, @at);
                    """, new { reviewId, owner = p.Id, due = D(5), past = D(-60), done = Ago(62), at = Ago(84) }, tx);
                await SupplierDocumentAsync(supplierId, await type("Business review"), "Quarterly business review deck", null, p.Id, reviewId);
                await audit.WriteAsync(c, tx, me.AsActor(), "demo.supplier", "Supplier", supplierId, $"{me.Name} loaded demo supplier details, documents and a business review (fictional).");
            }
        }

        private async Task<int> SupplierDocumentAsync(int supplierId, int? typeId, string title, DateTime? expires, int by, int? reviewId = null)
        {
            var bytes = Pdf(title, "Fictional supplier document");
            var (stored, sha) = await StoreAsync(bytes);
            var p = new DynamicParameters(new { supplierId, typeId, title, by, reviewId, at = Ago(30) });
            p.Add("expires", expires, System.Data.DbType.Date);
            var id = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.SupplierDocument (SupplierId, TypeId, Title, ExpiresOn, CreatedUtc, CreatedById, ReviewId) OUTPUT INSERTED.Id
                VALUES (@supplierId, @typeId, @title, @expires, @at, @by, @reviewId)
                """, p, tx);
            await c.ExecuteAsync("""
                INSERT INTO contracts.SupplierDocumentVersion (DocumentId, VersionNumber, FileName, ContentType, SizeBytes, Sha256, StoredName, UploadedUtc, UploadedById)
                VALUES (@id, 1, @fileName, N'application/pdf', @size, @sha, @stored, @at, @by)
                """, new { id, fileName = $"{title}.pdf", size = bytes.Length, sha, stored, at = Ago(30), by }, tx);
            return id;
        }

        // ------------------------------------------------------------ Building blocks

        private async Task<int> ContractAsync(Spec s)
        {
            var reference = await ContractNumbers.NextAsync(c, tx);
            var autoRenews = s.AutoRenewMonths is not null && !s.Evergreen && s.End is not null;
            var p = new DynamicParameters(new
            {
                reference, title = s.Title, description = "Fictional demo contract for trying JBS Contracts on QA.", s.Supplier, s.Category, s.AgreementType, s.BusinessUnit, s.Site,
                s.Owner, s.Backup, s.Confidential, s.Value, s.Evergreen, s.Notice, s.Legal,
                entity = s.BusinessUnit == corporate ? foodsEntity : meatsEntity,
                function = s.Category == linehaul || s.Category == coldChain ? logisticsFn : s.Category == facilities || s.Category == cleaning ? operationsFn : procurementFn,
                autoRenews, term = autoRenews ? s.AutoRenewMonths : null,
            });
            p.Add("start", s.Start, System.Data.DbType.Date);
            p.Add("end", s.End, System.Data.DbType.Date);
            p.Add("review", s.Review, System.Data.DbType.Date);
            // Raised by its owner (as in real life), so approvers who aren't involved can approve it.
            var id = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Contract (Reference, Title, Description, SupplierId, CategoryId, AgreementTypeId, BusinessUnitId, SiteId, EntityId, FunctionId, OwnerId, BackupOwnerId,
                    IsConfidential, Value, CurrencyCode, StartDate, EndDate, IsEvergreen, AutoRenews, RenewalTermMonths, NoticePeriodDays, ReviewDate, NeedsLegalReview,
                    CreatedById, UpdatedById, IsDemo)
                OUTPUT INSERTED.Id
                VALUES (@reference, @title, @description, @Supplier, @Category, @AgreementType, @BusinessUnit, @Site, @entity, @function, @Owner, @Backup,
                    @Confidential, @Value, N'AUD', @start, @end, @Evergreen, @autoRenews, @term, @Notice, @review, @Legal, @Owner, @Owner, 1)
                """, p, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "demo.contract", "Contract", id, $"{me.Name} loaded the demo contract {reference} {s.Title} (fictional; it never sends email).", contractId: id);
            Contracts++;
            return id;
        }

        private Task StageAsync(int id, string stage, double daysAgo) =>
            c.ExecuteAsync("UPDATE contracts.Contract SET Stage = @stage, StageSinceUtc = @at WHERE Id = @id", new { stage, at = Ago(daysAgo), id }, tx);

        private async Task LegalReviewAsync(int id, double since, string whoseTurn, int? legalPerson, string status, string? note,
            (string Action, int By, double DaysAgo, string? WhoseTurn, int? LegalPerson, string? Status, string? Note, bool MarkUp)[] turns)
        {
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = N'LegalReview', StageSinceUtc = @stageSince, WhoseTurn = @whoseTurn, LegalPersonId = @legalPerson,
                    LegalStatus = @status, LegalNote = @note, TurnSinceUtc = @turnSince
                WHERE Id = @id
                """, new { stageSince = Ago(turns[0].DaysAgo), whoseTurn, legalPerson, status, note, turnSince = Ago(since), id }, tx);
            var number = 0;
            foreach (var t in turns)
            {
                int[] docs = t.MarkUp ? [await DocumentAsync(id, "LegalMarkup", "Clause 14 mark-up.pdf", t.By, t.DaysAgo)] : [];
                await TurnAsync(id, ++number, t.Action, t.By, t.DaysAgo, t.WhoseTurn, t.LegalPerson, t.Status, t.Note, docs);
            }
        }

        private async Task ClearedAsync(int id, int legal, double daysAgo, string outcome)
        {
            var owner = await c.ExecuteScalarAsync<int>("SELECT OwnerId FROM contracts.Contract WHERE Id = @id", new { id }, tx);
            await TurnAsync(id, 1, "SentToLegal", owner, daysAgo + 5, "Legal", null, "NotStarted", null, []);
            await TurnAsync(id, 2, "Taken", legal, daysAgo + 4, "Legal", legal, "Reviewing", null, []);
            await TurnAsync(id, 3, "Cleared", legal, daysAgo, null, legal, null, outcome, []);
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = N'Signing', StageSinceUtc = @at, LegalPersonId = @legal, LegalOutcome = @outcome, LegalClearedUtc = @at, LegalClearedById = @legal
                WHERE Id = @id
                """, new { at = Ago(daysAgo), legal, outcome, id }, tx);
        }

        private async Task<int> EffectiveAsync(Spec s, int approver, int? legal = null)
        {
            var id = await ContractAsync(s);
            if (legal is { } l) await ClearedAsync(id, l, daysAgo: 40, "Cleared. Standard terms.");
            await SignedCopyAsync(id, s.Owner, 35);
            var effective = s.Start ?? D(-30);
            await ApprovalAsync(id, null, "Approved", approver, groupHead, daysAgo: 30, null);
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = N'Effective', StageSinceUtc = @at, IsLocked = 1, EffectiveDate = @effective, RegisteredUtc = @at,
                    RegisteredById = @approver, RegisteredAsBackup = 1
                WHERE Id = @id
                """, new { at = Ago(30), effective, approver, id }, tx);
            return id;
        }

        private Task<int> SignedCopyAsync(int id, int by, double daysAgo) => DocumentAsync(id, "SignedCopy", "Signed agreement.pdf", by, daysAgo);

        private async Task ApprovalAsync(int id, int? variationId, string decision, int by, int? groupHeadId, double daysAgo, string? reason) =>
            await c.ExecuteAsync("INSERT INTO contracts.Approval (ContractId, VariationId, Decision, ById, AsBackupFor, AtUtc, Reason) VALUES (@id, @variationId, @decision, @by, @groupHeadId, @at, @reason)",
                new { id, variationId, decision, by, groupHeadId, at = Ago(daysAgo), reason }, tx);

        private async Task TurnAsync(int id, int number, string action, int by, double daysAgo, string? whoseTurn, int? legalPerson, string? status, string? note,
            int[] documentVersionIds, int? variationId = null)
        {
            var turnId = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.LegalTurn (ContractId, VariationId, Number, Action, ById, AtUtc, WhoseTurn, LegalPersonId, Status, Note)
                OUTPUT INSERTED.Id VALUES (@id, @variationId, @number, @action, @by, @at, @whoseTurn, @legalPerson, @status, @note)
                """, new { id, variationId, number, action, by, at = Ago(daysAgo), whoseTurn, legalPerson, status, note }, tx);
            foreach (var v in documentVersionIds)
                await c.ExecuteAsync("INSERT INTO contracts.LegalTurnDocument (LegalTurnId, DocumentVersionId) VALUES (@turnId, @v)", new { turnId, v }, tx);
        }

        /// <summary>A small fictional PDF, stored like an upload. Returns the version id.</summary>
        private async Task<int> DocumentAsync(int contractId, string kind, string fileName, int by, double daysAgo, int? variationId = null)
        {
            var reference = await c.ExecuteScalarAsync<string>("SELECT Reference + N' ' + Title FROM contracts.Contract WHERE Id = @contractId", new { contractId }, tx);
            var bytes = Pdf(Path.GetFileNameWithoutExtension(fileName), reference!);
            var (stored, sha) = await StoreAsync(bytes);
            var documentId = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Document (ContractId, VariationId, Kind, Title, CreatedById, CreatedUtc) OUTPUT INSERTED.Id VALUES (@contractId, @variationId, @kind, @title, @by, @at)
                """, new { contractId, variationId, kind, title = Path.GetFileNameWithoutExtension(fileName), by, at = Ago(daysAgo) }, tx);
            return await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.DocumentVersion (DocumentId, VersionNumber, FileName, ContentType, SizeBytes, Sha256, StoredName, UploadedUtc, UploadedById)
                OUTPUT INSERTED.Id VALUES (@documentId, 1, @fileName, N'application/pdf', @size, @sha, @stored, @at, @by)
                """, new { documentId, fileName, size = bytes.Length, sha, stored, at = Ago(daysAgo), by }, tx);
        }

        private async Task<(string Stored, string Sha)> StoreAsync(byte[] bytes)
        {
            var stored = $"{now:yyyy}/{Guid.NewGuid():N}.pdf";
            var path = Path.Combine(documents, stored.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            written.Add(path);
            return (stored, Convert.ToHexString(SHA256.HashData(bytes)));
        }

        public void DeleteFiles(ILogger logger)
        {
            foreach (var f in written)
                try { File.Delete(f); } catch (Exception ex) { logger.LogWarning(ex, "Could not remove a demo file {File}.", f); }
        }

        private async Task<int> PersonAsync(string name, string user, bool owner = false, bool legal = false, bool approver = false)
        {
            var account = $"{domain}\\{user}";
            var existing = await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Person WHERE AccountKey = UPPER(@account)", new { account }, tx);
            if (existing is { } id) return id;
            return await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Person (WindowsAccount, Name, Email, IsContractOwner, IsLegal, IsApprover, CreatedById, UpdatedById)
                OUTPUT INSERTED.Id VALUES (@account, @name, @email, @owner, @legal, @approver, @me, @me)
                """, new { account, name, email = $"{user}@example.com", owner, legal, approver, me = me.Id }, tx);
        }

        private async Task<int> SupplierAsync(string name) =>
            await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Supplier WHERE NameKey = UPPER(@name)", new { name }, tx)
            ?? await c.ExecuteScalarAsync<int>("INSERT INTO contracts.Supplier (Name, Notes, CreatedById, UpdatedById, IsDemo) OUTPUT INSERTED.Id VALUES (@name, N'Fictional demo supplier.', @me, @me, 1)",
                new { name, me = me.Id }, tx);

        private async Task<int> CategoryAsync(string name, int? parentId) =>
            await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Category WHERE ParentKey = ISNULL(@parentId, 0) AND NameKey = UPPER(@name)", new { name, parentId }, tx)
            ?? await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Category (ParentId, Name, SortOrder) OUTPUT INSERTED.Id
                VALUES (@parentId, @name, (SELECT ISNULL(MAX(SortOrder), -1) + 1 FROM contracts.Category WHERE ParentKey = ISNULL(@parentId, 0)))
                """, new { name, parentId }, tx);

        private async Task<int> ItemAsync(string list, string value) =>
            await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.ListItem WHERE ListName = @list AND ValueKey = UPPER(@value)", new { list, value }, tx)
            ?? await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.ListItem (ListName, Value, SortOrder) OUTPUT INSERTED.Id
                VALUES (@list, @value, (SELECT ISNULL(MAX(SortOrder), -1) + 1 FROM contracts.ListItem WHERE ListName = @list))
                """, new { list, value }, tx);

        /// <summary>A one-page PDF saying it is a fictional demo document.</summary>
        private static byte[] Pdf(string title, string contract)
        {
            static string Esc(string s) => new string(s.Select(ch => ch < 32 || ch > 126 ? '-' : ch).ToArray()).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            var content = $"BT /F1 18 Tf 72 750 Td ({Esc(title)}) Tj /F1 12 Tf 0 -28 Td ({Esc(contract)}) Tj 0 -20 Td (Demo document: fictional, for trying JBS Contracts on QA.) Tj ET";
            string[] objects =
            [
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            ];
            var pdf = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Length; i++)
            {
                offsets.Add(pdf.Length);
                pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            var xref = pdf.Length;
            pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) pdf.Append($"{o:D10} 00000 n \n");
            pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(pdf.ToString());
        }
    }
}
