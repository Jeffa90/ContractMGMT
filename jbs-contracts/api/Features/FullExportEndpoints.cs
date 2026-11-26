using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// "Download everything" (D-87): one Excel workbook with every record JBS Contracts keeps, a sheet per kind (contracts in every stage with
/// every field, documents, Legal review, approvals, Variations, reminders sent, suppliers, people and lists). For admins and auditors.
/// "Download all documents" is a ZIP of every file version, a folder per contract, with an index; for admins only. Both are audited.
/// </summary>
public static class FullExportEndpoints
{
    private static readonly Dictionary<string, string> LegalActions = new()
    {
        ["SentToLegal"] = "Sent to Legal", ["Taken"] = "Picked up", ["Assigned"] = "Given to a colleague", ["StatusChanged"] = "Status changed",
        ["SentToOwner"] = "Sent to the owner", ["Cleared"] = "Legal cleared",
    };

    private static DateTime? L(DateTime? utc) => utc is { } u ? ReportEndpoints.Local(u) : null;
    private static Excel.Column C(string header, double width = 20, string? note = null) => new(header, note, width);

    private sealed class ContractExtra
    {
        public int Id { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string? CreatedByName { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime StageSinceUtc { get; set; }
        public string? WhoseTurn { get; set; }
        public string? LegalPersonName { get; set; }
        public string? LegalStatus { get; set; }
        public DateTime? LegalClearedUtc { get; set; }
        public string? WithdrawnReason { get; set; }
        public DateTime? RenewalDecisionUtc { get; set; }
        public string? RenewalDecisionByName { get; set; }
        public string? RenewalDecisionNote { get; set; }
        public DateTime? PlannedEndDate { get; set; }
        public string? RenewsReference { get; set; }
        public DateTime? LastReviewedOn { get; set; }
    }

    private sealed class DocumentRow
    {
        public int ContractId { get; set; }
        public string Reference { get; set; } = "";
        public int? VariationNumber { get; set; }
        public string Title { get; set; } = "";
        public string Kind { get; set; } = "";
        public int VersionNumber { get; set; }
        public int CurrentVersion { get; set; }
        public bool IsRemoved { get; set; }
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
        public string StoredName { get; set; } = "";
        public string UploadedByName { get; set; } = "";
        public DateTime UploadedUtc { get; set; }
    }

    private static async Task<List<DocumentRow>> DocumentsAsync(SqlConnection c, Person me) =>
        (await c.QueryAsync<DocumentRow>($"""
            SELECT k.Id AS ContractId, k.Reference, var.Number AS VariationNumber, d.Title, COALESCE(dk.Value, d.Kind) AS Kind, v.VersionNumber, d.CurrentVersion, d.IsRemoved,
                   v.FileName, v.ContentType, v.SizeBytes, v.Sha256, v.StoredName, u.Name AS UploadedByName, v.UploadedUtc
            FROM contracts.DocumentVersion v JOIN contracts.Document d ON d.Id = v.DocumentId JOIN contracts.Contract k ON k.Id = d.ContractId
            JOIN contracts.Person u ON u.Id = v.UploadedById LEFT JOIN contracts.Variation var ON var.Id = d.VariationId
            {DocumentEndpoints.KindJoin}
            WHERE {ContractAccess.VisibleSql("k")}
            ORDER BY k.Reference, d.Id, v.VersionNumber
            """, ContractAccess.Params(me))).ToList();

    private static object?[] DocumentCells(DocumentRow d) =>
    [
        d.Reference, d.VariationNumber is { } n ? $"Variation {n}" : null, d.Title, DocumentEndpoints.Kinds.GetValueOrDefault(d.Kind, d.Kind), d.VersionNumber,
        d.VersionNumber == d.CurrentVersion, d.IsRemoved, d.FileName, d.ContentType, d.SizeBytes, d.UploadedByName, L(d.UploadedUtc), d.Sha256,
    ];

    private static readonly Excel.Column[] DocumentColumns =
    [
        C("Reference", 16), C("Variation", 12), C("Document", 30), C("Kind", 22), C("Version", 9), C("Latest version", 10), C("Removed from the contract", 12),
        C("File name", 36), C("File type", 22), C("Size (bytes)", 12), C("Uploaded by", 22), C("Uploaded", 18),
        C("SHA-256", 66, "A fingerprint of the file: if the file on the server changed, it wouldn't match"),
    ];

    private sealed class SupplierDocRow
    {
        public string Supplier { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime? ExpiresOn { get; set; }
        public DateTime? ReviewHeldOn { get; set; }
        public int VersionNumber { get; set; }
        public int CurrentVersion { get; set; }
        public bool IsRemoved { get; set; }
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = "";
        public string StoredName { get; set; } = "";
        public string UploadedByName { get; set; } = "";
        public DateTime UploadedUtc { get; set; }
    }

    private static async Task<List<SupplierDocRow>> SupplierDocumentsAsync(SqlConnection c) =>
        (await c.QueryAsync<SupplierDocRow>("""
            SELECT s.Name AS Supplier, t.Value AS TypeName, d.Title, d.ExpiresOn, r.HeldOn AS ReviewHeldOn, v.VersionNumber, d.CurrentVersion, d.IsRemoved,
                   v.FileName, v.ContentType, v.SizeBytes, v.Sha256, v.StoredName, u.Name AS UploadedByName, v.UploadedUtc
            FROM contracts.SupplierDocumentVersion v JOIN contracts.SupplierDocument d ON d.Id = v.DocumentId JOIN contracts.Supplier s ON s.Id = d.SupplierId
            JOIN contracts.ListItem t ON t.Id = d.TypeId JOIN contracts.Person u ON u.Id = v.UploadedById LEFT JOIN contracts.BusinessReview r ON r.Id = d.ReviewId
            ORDER BY s.Name, d.Id, v.VersionNumber
            """)).ToList();

    private static object?[] SupplierDocumentCells(SupplierDocRow d) =>
    [
        d.Supplier, d.TypeName, d.Title, ReportEndpoints.D(d.ExpiresOn), ReportEndpoints.D(d.ReviewHeldOn), d.VersionNumber, d.VersionNumber == d.CurrentVersion, d.IsRemoved,
        d.FileName, d.ContentType, d.SizeBytes, d.UploadedByName, L(d.UploadedUtc), d.Sha256,
    ];

    private static readonly Excel.Column[] SupplierDocumentColumns =
    [
        C("Supplier", 30), C("Kind", 26), C("Document", 30), C("Expires", 13), C("Business review held", 14), C("Version", 9), C("Latest version", 10), C("Removed", 10),
        C("File name", 36), C("File type", 22), C("Size (bytes)", 12), C("Uploaded by", 22), C("Uploaded", 18), C("SHA-256", 66),
    ];

    public static void MapFullExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/reports/everything.xlsx", async (CurrentUser current, Db db, AuditLog audit, TimeProvider clock) =>
        {
            var (me, refusal) = await ReportEndpoints.RequireAuditAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var p = ContractAccess.Params(me!);
            var visible = ContractAccess.VisibleSql("k");
            var sheets = new List<Excel.Sheet>();

            // 1. Contracts: every stage, every field, and how each got where it is.
            var register = await ReportEndpoints.RegisterAsync(c, me!, "everything", []);
            var extras = (await c.QueryAsync<ContractExtra>($"""
                SELECT k.Id, k.CreatedUtc, cb.Name AS CreatedByName, k.UpdatedUtc, k.StageSinceUtc, k.WhoseTurn, lp.Name AS LegalPersonName, k.LegalStatus,
                       k.LegalClearedUtc, k.WithdrawnReason, k.RenewalDecisionUtc, rdb.Name AS RenewalDecisionByName, k.RenewalDecisionNote, k.PlannedEndDate,
                       rk.Reference AS RenewsReference, k.LastReviewedOn
                FROM contracts.Contract k LEFT JOIN contracts.Person cb ON cb.Id = k.CreatedById LEFT JOIN contracts.Person lp ON lp.Id = k.LegalPersonId
                LEFT JOIN contracts.Person rdb ON rdb.Id = k.RenewalDecisionById LEFT JOIN contracts.Contract rk ON rk.Id = k.RenewsContractId
                WHERE {visible}
                """, p)).ToDictionary(x => x.Id);
            var people = (await c.QueryAsync<Person>("SELECT * FROM contracts.Person")).ToDictionary(x => x.Id);
            var contractColumns = new List<Excel.Column> { C("Reference", 16) };
            foreach (var x in register.Columns)
            {
                contractColumns.Add(C(x.Label, x.Type is "date" or "yesno" or "number" ? 14 : 22));
                if (x.Key == "value") contractColumns.Add(C("Currency", 10));
            }
            contractColumns.AddRange([C("Stage since", 18), C("Whose turn (Legal review)", 14), C("Legal person", 22), C("Legal status", 18), C("Legal cleared", 18),
                C("Why it was withdrawn", 36), C("Renewal decided", 18), C("Renewal decided by", 22), C("Renewal note", 36), C("Planned end", 14),
                C("Renews", 16), C("Last reviewed", 14), C("Created", 18), C("Created by", 22), C("Last changed", 18)]);
            sheets.Add(new("Contracts", contractColumns, register.Rows.Select(r =>
            {
                var row = new List<object?> { r.Reference };
                foreach (var x in register.Columns)
                {
                    row.Add(x.Added is { } f
                        ? register.Added[r.Id].TryGetValue(f.Key, out var v) ? Fields.ExcelValue(f, v, id => people.GetValueOrDefault(id)?.WindowsAccount) : null
                        : ReportEndpoints.BuiltInValue(r, x.Key));
                    if (x.Key == "value") row.Add(r.CurrencyCode);
                }
                var e = extras[r.Id];
                row.AddRange([L(e.StageSinceUtc), e.WhoseTurn, e.LegalPersonName, e.LegalStatus is { } ls ? LegalEndpoints.StatusNames.GetValueOrDefault(ls, ls) : null,
                    L(e.LegalClearedUtc), e.WithdrawnReason, L(e.RenewalDecisionUtc), e.RenewalDecisionByName, e.RenewalDecisionNote, ReportEndpoints.D(e.PlannedEndDate),
                    e.RenewsReference, ReportEndpoints.D(e.LastReviewedOn), L(e.CreatedUtc), e.CreatedByName, L(e.UpdatedUtc)]);
                return row.ToArray();
            }).ToList()));

            // 2. Documents: one row per file version (the files themselves are in "Download all documents").
            var documents = await DocumentsAsync(c, me!);
            sheets.Add(new("Documents", DocumentColumns, documents.Select(DocumentCells).ToList()));

            // 3. Legal review: every hand-over, with its note and the documents it carried.
            var legal = await c.QueryAsync($"""
                SELECT k.Reference, var.Number AS VariationNumber, t.Number, t.Action, b.Name AS ByName, t.AtUtc, t.WhoseTurn, lp.Name AS LegalPersonName, t.Status, t.Note,
                       (SELECT STRING_AGG(dv.FileName + N' (v' + CAST(dv.VersionNumber AS NVARCHAR(10)) + N')', N'; ')
                        FROM contracts.LegalTurnDocument td JOIN contracts.DocumentVersion dv ON dv.Id = td.DocumentVersionId WHERE td.LegalTurnId = t.Id) AS Documents
                FROM contracts.LegalTurn t JOIN contracts.Contract k ON k.Id = t.ContractId JOIN contracts.Person b ON b.Id = t.ById
                LEFT JOIN contracts.Person lp ON lp.Id = t.LegalPersonId LEFT JOIN contracts.Variation var ON var.Id = t.VariationId
                WHERE {visible}
                ORDER BY k.Reference, t.VariationId, t.Number
                """, p);
            sheets.Add(new("Legal review",
                [C("Reference", 16), C("Variation", 12), C("Step", 7), C("What happened", 20), C("By", 22), C("When", 18), C("Whose turn after", 14), C("Legal person", 22),
                 C("Status", 18), C("Note", 50), C("Documents", 50)],
                legal.Select(t => new object?[]
                {
                    (string)t.Reference, t.VariationNumber is int n ? $"Variation {n}" : null, (int)t.Number, LegalActions.GetValueOrDefault((string)t.Action, (string)t.Action),
                    (string)t.ByName, L((DateTime)t.AtUtc), (string?)t.WhoseTurn, (string?)t.LegalPersonName,
                    t.Status is string st ? LegalEndpoints.StatusNames.GetValueOrDefault(st, st) : null, (string?)t.Note, (string?)t.Documents,
                }).ToList()));

            // 4. Approvals: every decision on a contract or Variation.
            var approvals = await c.QueryAsync($"""
                SELECT k.Reference, var.Number AS VariationNumber, a.Decision, b.Name AS ByName, gh.Name AS AsBackupForName, a.AtUtc, a.Reason
                FROM contracts.Approval a JOIN contracts.Contract k ON k.Id = a.ContractId JOIN contracts.Person b ON b.Id = a.ById
                LEFT JOIN contracts.Person gh ON gh.Id = a.AsBackupFor LEFT JOIN contracts.Variation var ON var.Id = a.VariationId
                WHERE {visible}
                ORDER BY a.AtUtc
                """, p);
            sheets.Add(new("Approvals", [C("Reference", 16), C("Variation", 12), C("Decision", 12), C("By", 22), C("As backup for", 22), C("When", 18), C("Reason", 50)],
                approvals.Select(a => new object?[]
                {
                    (string)a.Reference, a.VariationNumber is int n ? $"Variation {n}" : null, (string)a.Decision switch { "Approved" => "Approved", "Undone" => "Approval undone", _ => "Sent back" },
                    (string)a.ByName, (string?)a.AsBackupForName, L((DateTime)a.AtUtc), (string?)a.Reason,
                }).ToList()));

            // 5. Variations: why, where they are, and what they change (as applied once approved).
            var labels = (await Fields.LoadAsync(c)).ToDictionary(f => f.Key, f => f.Label);
            var variations = await c.QueryAsync($"""
                SELECT k.Reference, v.Number, v.Reason, v.Stage, v.NeedsLegalReview, v.ChangesJson, v.AppliedChangesJson, v.LegalOutcome, v.ClosedReason,
                       cb.Name AS CreatedByName, v.CreatedUtc, v.StageSinceUtc
                FROM contracts.Variation v JOIN contracts.Contract k ON k.Id = v.ContractId JOIN contracts.Person cb ON cb.Id = v.CreatedById
                WHERE {visible}
                ORDER BY k.Reference, v.Number
                """, p);
            sheets.Add(new("Variations",
                [C("Reference", 16), C("Variation", 10), C("Reason", 40), C("Stage", 14), C("Needs Legal review", 10), C("Changes", 60), C("Legal outcome", 36),
                 C("Why it was withdrawn", 36), C("Raised by", 22), C("Raised", 18), C("Stage since", 18)],
                variations.Select(v => new object?[]
                {
                    (string)v.Reference, (int)v.Number, (string)v.Reason, ((string)v.Stage) switch { "LegalReview" => "Legal review", var st => st },
                    (bool)v.NeedsLegalReview, Changes((string)v.ChangesJson, (string?)v.AppliedChangesJson, labels), (string?)v.LegalOutcome, (string?)v.ClosedReason,
                    (string)v.CreatedByName, L((DateTime)v.CreatedUtc), L((DateTime)v.StageSinceUtc),
                }).ToList()));

            // 6. Reminders sent by the daily job.
            var reminders = await c.QueryAsync($"""
                SELECT k.Reference, r.Kind, r.DueDate, r.DaysBefore, r.SentUtc, r.Recipients
                FROM contracts.Reminder r JOIN contracts.Contract k ON k.Id = r.ContractId WHERE {visible} ORDER BY r.SentUtc
                """, p);
            sheets.Add(new("Reminders sent", [C("Reference", 16), C("Reminder", 18), C("About the date", 14), C("Days before", 10), C("Sent", 18), C("Sent to", 60)],
                reminders.Select(r => new object?[]
                {
                    (string)r.Reference, ((string)r.Kind) switch { "Notice" => "Notice deadline", "Escalation" => "Escalation", "End" => "End date", _ => "Review date" },
                    DateOnly.FromDateTime((DateTime)r.DueDate), (int)r.DaysBefore, L((DateTime)r.SentUtc), (string)r.Recipients,
                }).ToList()));

            // 7. Suppliers, people and lists.
            var suppliers = await c.QueryAsync($"""
                SELECT s.Name, s.Abn, s.VendorNumber, s.Notes, s.IsActive, ro.Name AS RelationshipOwner, ISNULL(t.Value, auto.AutoTierName) AS Tier, COALESCE(rk.Value, s.RiskRating) AS RiskRating, s.RiskReason, s.NextReviewDue,
                       (SELECT COUNT(*) FROM contracts.Contract k WHERE k.SupplierId = s.Id) AS Contracts
                FROM contracts.Supplier s LEFT JOIN contracts.Person ro ON ro.Id = s.RelationshipOwnerId LEFT JOIN contracts.ListItem t ON t.Id = s.TierId
                LEFT JOIN contracts.ListItem rk ON rk.ListName = N'SupplierRisk' AND s.RiskRating = N'L' + CAST(rk.Id AS NVARCHAR(10))
                {SupplierPageEndpoints.AutoTier}
                ORDER BY s.Name
                """);
            sheets.Add(new("Suppliers",
                [C("Name", 34), C("ABN", 14), C("SAP vendor number", 16), C("Notes", 40), C("Active", 8), C("Relationship owner", 22), C("Tier", 14), C("Risk", 9),
                 C("Why that risk", 36), C("Next business review due", 14), C("Contracts", 10)],
                suppliers.Select(s => new object?[]
                {
                    (string)s.Name, (string?)s.Abn, (string?)s.VendorNumber, (string?)s.Notes, (bool)s.IsActive, (string?)s.RelationshipOwner, (string?)s.Tier,
                    (string?)s.RiskRating, (string?)s.RiskReason, s.NextReviewDue is DateTime nr ? DateOnly.FromDateTime(nr) : null, (int)s.Contracts,
                }).ToList()));
            var supplierDocuments = await SupplierDocumentsAsync(c);
            sheets.Add(new("Supplier documents", SupplierDocumentColumns, supplierDocuments.Select(SupplierDocumentCells).ToList()));
            var contacts = await c.QueryAsync("""
                SELECT s.Name AS Supplier, x.Name, x.Role, x.Email, x.Phone, x.IsRemoved FROM contracts.SupplierContact x JOIN contracts.Supplier s ON s.Id = x.SupplierId
                ORDER BY s.Name, x.Name
                """);
            sheets.Add(new("Supplier contacts", [C("Supplier", 30), C("Name", 24), C("Role", 24), C("Email", 30), C("Phone", 16), C("Removed", 10)],
                contacts.Select(x => new object?[] { (string)x.Supplier, (string)x.Name, (string?)x.Role, (string?)x.Email, (string?)x.Phone, (bool)x.IsRemoved }).ToList()));
            var reviewRows = await c.QueryAsync("""
                SELECT r.Id, s.Name AS Supplier, r.HeldOn, r.Rating, r.QualityScore, r.DeliveryScore, r.ServiceScore, r.CostScore, r.Summary,
                       CONCAT_WS(N'; ', (SELECT STRING_AGG(k.Name, N'; ') FROM contracts.BusinessReviewContact x JOIN contracts.SupplierContact k ON k.Id = x.ContactId WHERE x.ReviewId = r.Id),
                                 r.SupplierAttendees) AS SupplierAttendees, r.IsRemoved,
                       cb.Name AS CreatedBy, r.CreatedUtc,
                       (SELECT STRING_AGG(p.Name, N'; ') FROM contracts.BusinessReviewAttendee a JOIN contracts.Person p ON p.Id = a.PersonId WHERE a.ReviewId = r.Id) AS Attendees
                FROM contracts.BusinessReview r JOIN contracts.Supplier s ON s.Id = r.SupplierId JOIN contracts.Person cb ON cb.Id = r.CreatedById
                ORDER BY s.Name, r.HeldOn
                """);
            sheets.Add(new("Business reviews",
                [C("Supplier", 30), C("Held", 13), C("Rating", 16), C("Quality", 8), C("Delivery", 8), C("Service", 8), C("Cost", 8), C("Summary", 60), C("JBS attendees", 36),
                 C("Supplier attendees", 36), C("Removed", 10), C("Added by", 22), C("Added", 18)],
                reviewRows.Select(r => new object?[]
                {
                    (string)r.Supplier, DateOnly.FromDateTime((DateTime)r.HeldOn), r.Rating is string rt ? SupplierPageEndpoints.RatingNames[rt] : null,
                    r.QualityScore is byte q ? (int)q : null, r.DeliveryScore is byte dv ? (int)dv : null, r.ServiceScore is byte sv ? (int)sv : null, r.CostScore is byte cs ? (int)cs : null,
                    (string)r.Summary, (string?)r.Attendees, (string?)r.SupplierAttendees, (bool)r.IsRemoved, (string)r.CreatedBy, L((DateTime)r.CreatedUtc),
                }).ToList()));
            var actionRows = await c.QueryAsync("""
                SELECT s.Name AS Supplier, r.HeldOn, a.Text, ISNULL(o.Name, ISNULL(sc.Name + N' (' + s.Name + N')', s.Name)) AS Owner, a.DueOn, a.DoneUtc, db.Name AS DoneBy
                FROM contracts.BusinessReviewAction a JOIN contracts.BusinessReview r ON r.Id = a.ReviewId JOIN contracts.Supplier s ON s.Id = r.SupplierId
                LEFT JOIN contracts.Person o ON o.Id = a.OwnerId LEFT JOIN contracts.SupplierContact sc ON sc.Id = a.SupplierContactId
                LEFT JOIN contracts.Person db ON db.Id = a.DoneById
                ORDER BY s.Name, r.HeldOn, a.DueOn
                """);
            sheets.Add(new("Review actions", [C("Supplier", 30), C("Review held", 13), C("Action", 50), C("Owner", 22), C("Due", 13), C("Done", 18), C("Done by", 22)],
                actionRows.Select(a => new object?[]
                {
                    (string)a.Supplier, DateOnly.FromDateTime((DateTime)a.HeldOn), (string)a.Text, (string)a.Owner, DateOnly.FromDateTime((DateTime)a.DueOn),
                    L((DateTime?)a.DoneUtc), (string?)a.DoneBy,
                }).ToList()));
            sheets.Add(new("People",
                [C("Name", 26), C("Windows account", 20), C("Email", 30), C("Contract owner", 10), C("Legal", 8), C("Approver", 10), C("Group Head", 10), C("Admin", 8),
                 C("Auditor", 8), C("Active", 8), C("Last signed in", 18)],
                people.Values.OrderBy(x => x.Name).Select(x => new object?[]
                {
                    x.Name, x.WindowsAccount, x.Email, x.IsContractOwner, x.IsLegal, x.IsApprover, x.IsGroupHead, x.IsAdmin, x.IsAuditor, x.IsActive,
                    L(x.LastSignInUtc),
                }).ToList()));
            var lists = await c.QueryAsync("""
                SELECT N'Category' AS List, cat.Name AS Value, parent.Name AS Under, cat.IsActive FROM contracts.Category cat LEFT JOIN contracts.Category parent ON parent.Id = cat.ParentId
                UNION ALL SELECT ListName, Value, NULL, IsActive FROM contracts.ListItem
                ORDER BY List, Under, Value
                """);
            sheets.Add(new("Lists", [C("List", 18), C("Value", 30), C("Under", 24), C("Active", 8)],
                lists.Select(l => new object?[] { (string)l.List, (string)l.Value, (string?)l.Under, (bool)l.IsActive }).ToList()));

            await using (var tx = await Db.BeginWriteAsync(c))
            {
                await audit.WriteAsync(c, tx, me!.AsActor(), "report.everything-downloaded", "Report", null,
                    $"{me.Name} downloaded everything: {register.Rows.Count:N0} contracts, {documents.Count:N0} document versions (details only), {people.Count:N0} people.");
                await tx.CommitAsync();
            }
            return ReportEndpoints.File(Excel.WriteBook(sheets), "everything", clock);
        });

        // Every file version, a folder per contract, with an index. Admins only (Jeff, 25 Nov). Built in a temporary file that deletes itself.
        app.MapGet("/api/reports/documents.zip", async (CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsAdmin) return ApiResults.NotAllowed("Only admins can download all documents.");
            await using var c = await db.OpenAsync();
            var documents = await DocumentsAsync(c, me);
            var supplierDocuments = await SupplierDocumentsAsync(c);
            var supplierPaths = new List<string?>();
            var temp = new FileStream(Path.Combine(Path.GetTempPath(), $"jbs-contracts-{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            var paths = new List<string?>();
            var missing = 0;
            using (var zip = new ZipArchive(temp, ZipArchiveMode.Create, leaveOpen: true))
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in documents)
                {
                    var source = settings.DocumentsDirectory is null ? null : Path.Combine(settings.DocumentsDirectory, d.StoredName.Replace('/', Path.DirectorySeparatorChar));
                    if (source is null || !System.IO.File.Exists(source)) { paths.Add(null); missing++; continue; }
                    // "Signed copy - v2 - Linfox signed.pdf"; the title is left out when it's just the file name.
                    var title = string.Equals(Path.GetFileNameWithoutExtension(d.FileName), d.Title, StringComparison.OrdinalIgnoreCase) ? "" : $"{Safe(d.Title)} - ";
                    var name = $"{d.Reference}/{(d.VariationNumber is { } n ? $"Variation {n} - " : "")}{title}v{d.VersionNumber} - {Safe(d.FileName)}";
                    var unique = name;
                    for (var i = 2; !used.Add(unique); i++) unique = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
                    var entry = zip.CreateEntry(unique, CompressionLevel.Fastest);
                    await using (var to = await entry.OpenAsync())
                    await using (var from = System.IO.File.OpenRead(source))
                        await from.CopyToAsync(to);
                    paths.Add(unique);
                }
                // Supplier documents, a folder per supplier.
                foreach (var d in supplierDocuments)
                {
                    var source = settings.DocumentsDirectory is null ? null : Path.Combine(settings.DocumentsDirectory, d.StoredName.Replace('/', Path.DirectorySeparatorChar));
                    if (source is null || !System.IO.File.Exists(source)) { supplierPaths.Add(null); missing++; continue; }
                    var title = string.Equals(Path.GetFileNameWithoutExtension(d.FileName), d.Title, StringComparison.OrdinalIgnoreCase) ? "" : $"{Safe(d.Title)} - ";
                    var name = $"Suppliers/{Safe(d.Supplier)}/{title}v{d.VersionNumber} - {Safe(d.FileName)}";
                    var unique = name;
                    for (var i = 2; !used.Add(unique); i++) unique = $"{Path.GetDirectoryName(name)!.Replace('\\', '/')}/{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
                    var entry = zip.CreateEntry(unique, CompressionLevel.Fastest);
                    await using (var to = await entry.OpenAsync())
                    await using (var from = System.IO.File.OpenRead(source))
                        await from.CopyToAsync(to);
                    supplierPaths.Add(unique);
                }
                // The index: which file is which, matching the Documents sheets of "Download everything".
                var index = Excel.WriteBook([
                    new Excel.Sheet("Documents", [.. DocumentColumns, C("In this ZIP as", 60)],
                        documents.Select((d, i) => DocumentCells(d).Append(paths[i] ?? "Missing from the server").ToArray()).ToList()),
                    new Excel.Sheet("Supplier documents", [.. SupplierDocumentColumns, C("In this ZIP as", 60)],
                        supplierDocuments.Select((d, i) => SupplierDocumentCells(d).Append(supplierPaths[i] ?? "Missing from the server").ToArray()).ToList()),
                ]);
                var indexEntry = zip.CreateEntry("Index of documents.xlsx", CompressionLevel.Fastest);
                await using (var to = await indexEntry.OpenAsync()) await to.WriteAsync(index);
            }
            await using (var tx = await Db.BeginWriteAsync(c))
            {
                await audit.WriteAsync(c, tx, me.AsActor(), "report.documents-downloaded", "Report", null,
                    $"{me.Name} downloaded all documents: {documents.Count + supplierDocuments.Count - missing:N0} files{(missing > 0 ? $" ({missing:N0} missing from the server)" : "")}.");
                await tx.CommitAsync();
            }
            temp.Position = 0;
            return Results.File(temp, "application/zip", $"jbs-contracts-documents-{TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")):yyyy-MM-dd}.zip");
        });
    }

    /// <summary>A name that's safe as part of a path in a ZIP on any computer.</summary>
    private static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new StringBuilder();
        foreach (var ch in name.Trim()) clean.Append(bad.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        var text = clean.ToString().Trim('.', ' ');
        return text.Length == 0 ? "file" : text.Length > 80 ? text[..80] : text;
    }

    /// <summary>A Variation's changes in words: as applied once approved ("End date: 30 Jun 2027 → 30 Jun 2028"), else what's proposed.</summary>
    private static string Changes(string proposedJson, string? appliedJson, Dictionary<string, string> labels)
    {
        if (appliedJson is not null)
        {
            using var applied = JsonDocument.Parse(appliedJson);
            return string.Join("; ", applied.RootElement.EnumerateArray().Select(x =>
                $"{Text(x, "Label")}: {(Text(x, "Old") is { Length: > 0 } o ? o : "blank")} → {(Text(x, "New") is { Length: > 0 } n ? n : "blank")}"));
        }
        using var proposed = JsonDocument.Parse(proposedJson);
        return string.Join("; ", proposed.RootElement.EnumerateObject().Select(x =>
            $"{labels.GetValueOrDefault(x.Name, x.Name)}: {(x.Value.ValueKind == JsonValueKind.Null ? "blank" : x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.GetRawText())} (proposed)"));
    }

    private static string Text(JsonElement x, string name) =>
        x.TryGetProperty(name, out var v) || x.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Null ? "" : v.GetRawText()
            : "";
}
