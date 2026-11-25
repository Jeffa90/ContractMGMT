using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Fictional demo data so every screen has something to show on QA (D-66). Admins only, and only where AllowDemoData is on
/// (QA and local; never Production). Everything is clearly fictional: names end in "(demo)", emails use example.com, and no
/// email is sent. Loaded once, in one transaction, and audited. The admin who loads it owns most of the contracts, so their Home fills up.
/// </summary>
public static class DemoData
{
    private const string Marker = "Southern Cross Freight (demo)";

    public static async Task<bool> IsLoadedAsync(Db db)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Supplier WHERE Name = @Marker", new { Marker }) > 0;
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
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Supplier WHERE Name = @Marker", new { Marker }, tx) > 0)
                return ApiResults.Changed("The demo data is already loaded.");
            var seeder = new Seeder(c, tx, me, audit, settings.DocumentsDirectory, HomeEndpoints.Today(clock), clock.GetUtcNow().UtcDateTime, settings.DefaultDomain ?? "DEMO");
            try
            {
                await seeder.RunAsync();
                await audit.WriteAsync(c, tx, me.AsActor(), "demo.loaded", "DemoData", null,
                    $"{me.Name} loaded the demo data: {seeder.Contracts} fictional contracts, with suppliers, people and lists marked (demo).");
                await tx.CommitAsync();
            }
            catch
            {
                seeder.DeleteFiles(logs.CreateLogger("DemoData"));
                throw;
            }
            return Results.Ok(new { contracts = seeder.Contracts });
        });
    }

    private sealed class Seeder(SqlConnection c, SqlTransaction tx, Person me, AuditLog audit, string documents, DateTime today, DateTime now, string domain)
    {
        private readonly List<string> written = [];
        public int Contracts { get; private set; }

        private static DateTime Day(DateTime today, int days) => today.AddDays(days);
        private DateTime D(int days) => Day(today, days);
        private DateTime Ago(double days) => now.AddDays(-days);

        public async Task RunAsync()
        {
            // People (fictional; they can't sign in). No Group Head is set, so the real one isn't moved.
            var priya = await PersonAsync("Priya Patel (demo)", "demo.ppatel", owner: true);
            var sam = await PersonAsync("Sam Nguyen (demo)", "demo.snguyen", legal: true);
            var leah = await PersonAsync("Leah Brooks (demo)", "demo.lbrooks", legal: true);
            var grant = await PersonAsync("Grant Wallace (demo)", "demo.gwallace", approver: true);
            var owner = me.IsContractOwner ? me.Id : priya;
            var groupHead = await c.ExecuteScalarAsync<int?>("SELECT Id FROM contracts.Person WHERE IsGroupHead = 1 AND IsActive = 1", transaction: tx);

            // Lists and suppliers.
            var freight = await CategoryAsync("Freight", null);
            var linehaul = await CategoryAsync("Linehaul", freight);
            var coldChain = await CategoryAsync("Cold chain", freight);
            var packaging = await CategoryAsync("Packaging", null);
            var facilities = await CategoryAsync("Facilities", null);
            var cleaning = await CategoryAsync("Cleaning", facilities);
            var waste = await CategoryAsync("Waste", facilities);
            var it = await CategoryAsync("IT services", null);
            var people = await CategoryAsync("People services", null);
            var services = await ItemAsync("AgreementType", "Services agreement");
            var supply = await ItemAsync("AgreementType", "Supply agreement");
            var beef = await ItemAsync("BusinessUnit", "Beef");
            var corporate = await ItemAsync("BusinessUnit", "Corporate");
            var north = await ItemAsync("Site", "North plant");
            var south = await ItemAsync("Site", "South plant");
            var office = await ItemAsync("Site", "Head office");
            var scf = await SupplierAsync(Marker);
            var bluegum = await SupplierAsync("Bluegum Packaging (demo)");
            var harbour = await SupplierAsync("Harbourside Cleaning (demo)");
            var ironbark = await SupplierAsync("Ironbark IT Services (demo)");
            var coastal = await SupplierAsync("Coastal Cold Storage (demo)");
            var redgum = await SupplierAsync("Redgum Uniforms (demo)");
            var keystone = await SupplierAsync("Keystone Security (demo)");
            var wattle = await SupplierAsync("Wattle Pest Control (demo)");

            // Drafts.
            await ContractAsync(new("Office cleaning 2027", harbour, cleaning, services, corporate, office, owner, null, 185_000m, D(30), D(30 + 730), 60, Legal: true));
            await ContractAsync(new("Pallet wrap supply", null, null, supply, beef, north, owner, null, null, null, null, null, Legal: true)); // missing required fields

            // Legal review: new for Legal to pick up.
            var linehaulId = await ContractAsync(new("Linehaul freight Brisbane to Sydney", scf, linehaul, services, beef, south, owner, priya, 1_350_000m, D(45), D(45 + 1095), 90, Legal: true));
            await LegalReviewAsync(linehaulId, since: 2, whoseTurn: "Legal", legalPerson: null, status: "NotStarted", note: null,
                [("SentToLegal", owner, 2, "Legal", null, "NotStarted", "New three-year linehaul agreement. Supplier wants a fuel levy clause.", false)]);

            // Legal review: Sam has it, waiting on the supplier (Priya owns it; the loader is the backup).
            var coldId = await ContractAsync(new("Cold storage overflow", coastal, coldChain, services, beef, north, priya, owner == me.Id ? me.Id : null, 640_000m, D(20), D(20 + 365), 60, Legal: true));
            await LegalReviewAsync(coldId, since: 6, whoseTurn: "Legal", legalPerson: sam, status: "WaitingOnSupplier", note: "Insurance certificate requested",
                [("SentToLegal", priya, 6, "Legal", null, "NotStarted", "Short-term overflow storage for the peak season.", false),
                 ("Taken", sam, 5, "Legal", sam, "Reviewing", null, false),
                 ("StatusChanged", sam, 4, "Legal", sam, "WaitingOnSupplier", "Insurance certificate requested", false)]);

            // Legal review: back with the owner.
            var itId = await ContractAsync(new("IT managed services", ironbark, it, services, corporate, office, owner, null, 980_000m, D(60), D(60 + 1095), 90, Legal: true));
            await LegalReviewAsync(itId, since: 1, whoseTurn: "Owner", legalPerson: leah, status: "WaitingOnOwner", note: null,
                [("SentToLegal", owner, 9, "Legal", null, "NotStarted", "Renewal of the managed services agreement with new service levels.", false),
                 ("Taken", leah, 8, "Legal", leah, "Reviewing", null, false),
                 ("SentToOwner", leah, 1, "Owner", leah, "WaitingOnOwner", "Liability is uncapped in clause 14. Please negotiate a cap of $2m with the supplier. My mark-up is attached.", true)]);

            // Signing: cleared by Legal, signed copy due.
            var uniformId = await ContractAsync(new("Uniform supply 2027", redgum, people, supply, beef, south, owner, null, 210_000m, D(14), D(14 + 730), 60, Legal: true));
            await ClearedAsync(uniformId, sam, daysAgo: 3, "Cleared. Standard supply terms; price review clause agreed.");

            // Signing: sent back from approval.
            var wasteId = await ContractAsync(new("Waste collection South plant", harbour, waste, services, beef, south, owner, null, 96_000m, D(7), D(7 + 730), 30, Legal: true));
            await ClearedAsync(wasteId, leah, daysAgo: 10, "Cleared. Indemnity wording updated.");
            await SignedCopyAsync(wasteId, owner, 4);
            await c.ExecuteAsync("UPDATE contracts.Contract SET EffectiveDate = @d WHERE Id = @wasteId", new { d = D(7), wasteId }, tx);
            await ApprovalAsync(wasteId, "SentBack", grant, groupHead, daysAgo: 2, "The signed copy is missing page 4 (the schedule of rates).");
            await StageAsync(wasteId, "Signing", 2);

            // Approval: waiting (owned by Priya, so the loader can approve it if they tick Approver).
            var cartonsId = await ContractAsync(new("Cartons and trays", bluegum, packaging, supply, beef, north, priya, null, 1_120_000m, D(-2), D(-2 + 1095), 90, Legal: true));
            await ClearedAsync(cartonsId, sam, daysAgo: 12, "Cleared. Volume commitments are within policy.");
            await SignedCopyAsync(cartonsId, priya, 1);
            await c.ExecuteAsync("UPDATE contracts.Contract SET EffectiveDate = @d WHERE Id = @cartonsId", new { d = D(-2), cartonsId }, tx);
            await StageAsync(cartonsId, "Approval", 1);

            // Effective: notice deadlines in 20 and 5 days, one that has passed, and some further out.
            await EffectiveAsync(new("Forklift servicing", scf, facilities, services, beef, north, owner, null, 74_000m, D(-300), D(40), 20, Legal: false), grant, groupHead);
            await EffectiveAsync(new("Pest control", wattle, facilities, services, beef, south, owner, null, 38_000m, D(-355), D(10), 5, Legal: false), grant, groupHead);
            await EffectiveAsync(new("Security patrols", keystone, facilities, services, corporate, office, owner, null, 155_000m, D(-305), D(60), 63, Legal: true), grant, groupHead, sam);
            await EffectiveAsync(new("Refrigerated transport", coastal, coldChain, services, beef, north, priya, null, 2_400_000m, D(-400), D(400), 90, Legal: true), grant, groupHead, leah);
            await EffectiveAsync(new("Software licences", ironbark, it, supply, corporate, office, owner, null, 120_000m, D(-500), null, 30, Legal: false, Evergreen: true, Review: D(120)), grant, groupHead);
            await EffectiveAsync(new("Executive recruitment services", ironbark, people, services, corporate, office, owner, null, 300_000m, D(-90), D(275), 30, Legal: true, Confidential: true), grant, groupHead, sam);

            // Withdrawn.
            var canteenId = await ContractAsync(new("Canteen services", harbour, facilities, services, beef, south, priya, null, 450_000m, null, null, null, Legal: true));
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = N'Withdrawn', StageSinceUtc = @at, IsLocked = 1, WithdrawnReason = N'The supplier withdrew their quote.' WHERE Id = @canteenId
                """, new { at = Ago(20), canteenId }, tx);
        }

        private sealed record Spec(string Title, int? Supplier, int? Category, int? AgreementType, int? BusinessUnit, int? Site, int Owner, int? Backup,
            decimal? Value, DateTime? Start, DateTime? End, int? Notice, bool Legal, bool Evergreen = false, DateTime? Review = null, bool Confidential = false);

        private async Task<int> ContractAsync(Spec s)
        {
            var number = await c.ExecuteScalarAsync<int>("UPDATE contracts.Counter SET NextValue = NextValue + 1 OUTPUT DELETED.NextValue WHERE Name = N'ContractNumber'", transaction: tx);
            var reference = $"JBSAUS-{number.ToString("0000", CultureInfo.InvariantCulture)}";
            var p = new DynamicParameters(new
            {
                reference, title = s.Title, description = "Fictional demo contract for trying JBS Contracts on QA.", s.Supplier, s.Category, s.AgreementType, s.BusinessUnit, s.Site,
                s.Owner, s.Backup, s.Confidential, s.Value, s.Evergreen, s.Notice, s.Legal, me = me.Id,
            });
            p.Add("start", s.Start, System.Data.DbType.Date);
            p.Add("end", s.End, System.Data.DbType.Date);
            p.Add("review", s.Review, System.Data.DbType.Date);
            var id = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.Contract (Reference, Title, Description, SupplierId, CategoryId, AgreementTypeId, BusinessUnitId, SiteId, OwnerId, BackupOwnerId,
                    IsConfidential, Value, CurrencyCode, StartDate, EndDate, IsEvergreen, NoticePeriodDays, ReviewDate, NeedsLegalReview, CreatedById, UpdatedById)
                OUTPUT INSERTED.Id
                VALUES (@reference, @title, @description, @Supplier, @Category, @AgreementType, @BusinessUnit, @Site, @Owner, @Backup,
                    @Confidential, @Value, N'AUD', @start, @end, @Evergreen, @Notice, @review, @Legal, @me, @me)
                """, p, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "demo.contract", "Contract", id, $"{me.Name} loaded the demo contract {reference} {s.Title} (fictional).", contractId: id);
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
            await TurnAsync(id, 1, "SentToLegal", (await c.ExecuteScalarAsync<int>("SELECT OwnerId FROM contracts.Contract WHERE Id = @id", new { id }, tx)), daysAgo + 5, "Legal", null, "NotStarted", null, []);
            await TurnAsync(id, 2, "Taken", legal, daysAgo + 4, "Legal", legal, "Reviewing", null, []);
            await TurnAsync(id, 3, "Cleared", legal, daysAgo, null, legal, null, outcome, []);
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = N'Signing', StageSinceUtc = @at, LegalPersonId = @legal, LegalOutcome = @outcome, LegalClearedUtc = @at, LegalClearedById = @legal
                WHERE Id = @id
                """, new { at = Ago(daysAgo), legal, outcome, id }, tx);
        }

        private async Task EffectiveAsync(Spec s, int approver, int? groupHead, int? legal = null)
        {
            var id = await ContractAsync(s);
            if (legal is { } l) await ClearedAsync(id, l, daysAgo: 40, "Cleared. Standard terms.");
            await SignedCopyAsync(id, s.Owner, 35);
            var effective = s.Start ?? D(-30);
            await ApprovalAsync(id, "Approved", approver, groupHead, daysAgo: 30, null);
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = N'Effective', StageSinceUtc = @at, IsLocked = 1, EffectiveDate = @effective, RegisteredUtc = @at,
                    RegisteredById = @approver, RegisteredAsBackup = 1
                WHERE Id = @id
                """, new { at = Ago(30), effective, approver, id }, tx);
        }

        private Task<int> SignedCopyAsync(int id, int by, double daysAgo) => DocumentAsync(id, "SignedCopy", "Signed agreement.pdf", by, daysAgo);

        private async Task ApprovalAsync(int id, string decision, int by, int? groupHead, double daysAgo, string? reason) =>
            await c.ExecuteAsync("INSERT INTO contracts.Approval (ContractId, Decision, ById, AsBackupFor, AtUtc, Reason) VALUES (@id, @decision, @by, @groupHead, @at, @reason)",
                new { id, decision, by, groupHead, at = Ago(daysAgo), reason }, tx);

        private async Task TurnAsync(int id, int number, string action, int by, double daysAgo, string? whoseTurn, int? legalPerson, string? status, string? note, int[] documentVersionIds)
        {
            var turnId = await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.LegalTurn (ContractId, Number, Action, ById, AtUtc, WhoseTurn, LegalPersonId, Status, Note)
                OUTPUT INSERTED.Id VALUES (@id, @number, @action, @by, @at, @whoseTurn, @legalPerson, @status, @note)
                """, new { id, number, action, by, at = Ago(daysAgo), whoseTurn, legalPerson, status, note }, tx);
            foreach (var v in documentVersionIds)
                await c.ExecuteAsync("INSERT INTO contracts.LegalTurnDocument (LegalTurnId, DocumentVersionId) VALUES (@turnId, @v)", new { turnId, v }, tx);
        }

        /// <summary>A small fictional PDF, stored like an upload. Returns the version id.</summary>
        private async Task<int> DocumentAsync(int contractId, string kind, string fileName, int by, double daysAgo)
        {
            var reference = await c.ExecuteScalarAsync<string>("SELECT Reference + N' ' + Title FROM contracts.Contract WHERE Id = @contractId", new { contractId }, tx);
            var bytes = Pdf(fileName.Replace(".pdf", ""), reference!);
            var stored = $"{now:yyyy}/{Guid.NewGuid():N}.pdf";
            var path = Path.Combine(documents, stored.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
            written.Add(path);
            var documentId = await c.ExecuteScalarAsync<int>("INSERT INTO contracts.Document (ContractId, Kind, Title, CreatedById, CreatedUtc) OUTPUT INSERTED.Id VALUES (@contractId, @kind, @title, @by, @at)",
                new { contractId, kind, title = Path.GetFileNameWithoutExtension(fileName), by, at = Ago(daysAgo) }, tx);
            return await c.ExecuteScalarAsync<int>("""
                INSERT INTO contracts.DocumentVersion (DocumentId, VersionNumber, FileName, ContentType, SizeBytes, Sha256, StoredName, UploadedUtc, UploadedById)
                OUTPUT INSERTED.Id VALUES (@documentId, 1, @fileName, N'application/pdf', @size, @sha, @stored, @at, @by)
                """, new { documentId, fileName, size = bytes.Length, sha = Convert.ToHexString(SHA256.HashData(bytes)), stored, at = Ago(daysAgo), by }, tx);
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
            ?? await c.ExecuteScalarAsync<int>("INSERT INTO contracts.Supplier (Name, Notes, CreatedById, UpdatedById) OUTPUT INSERTED.Id VALUES (@name, N'Fictional demo supplier.', @me, @me)",
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
