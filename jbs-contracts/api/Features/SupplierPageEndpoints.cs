using System.Globalization;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// The supplier page (D-88, D-90): everything about one supplier in one place. Its details (relationship owner, tier, risk, contacts, next
/// business review due), its own documents (types from an admin list, versions, optional expiry with an opt-in reminder), every document from
/// its contracts, its contracts, and a structured log of business reviews with actions. Everyone signed in reads it. Procurement and admins
/// change details, contacts and documents; business reviews can also be added by the supplier's relationship owner. Everything is audited.
/// </summary>
public static class SupplierPageEndpoints
{
    public static bool CanManage(Person me) => me.IsProcurement || me.IsAdmin;
    public static bool CanReview(Person me, int? relationshipOwnerId) => CanManage(me) || relationshipOwnerId == me.Id;

    public static readonly Dictionary<string, string> RatingNames = new() { ["Good"] = "Good", ["NeedsImprovement"] = "Needs improvement", ["Poor"] = "Poor" };
    private static string Day(DateTime d) => d.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
    private static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;
    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public sealed class SupplierInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Abn { get; set; }
        public string? VendorNumber { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; }
        public int? RelationshipOwnerId { get; set; }
        public string? RelationshipOwnerName { get; set; }
        public int? TierId { get; set; }
        public string? TierName { get; set; }
        public string? RiskRating { get; set; }
        /// <summary>Low, Medium, High, or the name of a level an admin added (D-98).</summary>
        public string? RiskName { get; set; }
        public string? RiskReason { get; set; }
        public DateTime? NextReviewDue { get; set; }
        public byte[] RowVersion { get; set; } = [];
    }

    private static Task<SupplierInfo?> LoadAsync(SqlConnection c, SqlTransaction? tx, int id, bool forUpdate = false) =>
        c.QuerySingleOrDefaultAsync<SupplierInfo>($"""
            SELECT s.Id, s.Name, s.Abn, s.VendorNumber, s.Notes, s.IsActive, s.RelationshipOwnerId, ro.Name AS RelationshipOwnerName, s.TierId, t.Value AS TierName,
                   s.RiskRating, COALESCE(rk.Value, s.RiskRating) AS RiskName, s.RiskReason, s.NextReviewDue, s.RowVersion
            FROM contracts.Supplier s {(forUpdate ? "WITH (UPDLOCK, HOLDLOCK)" : "")}
            LEFT JOIN contracts.Person ro ON ro.Id = s.RelationshipOwnerId LEFT JOIN contracts.ListItem t ON t.Id = s.TierId
            LEFT JOIN contracts.ListItem rk ON rk.ListName = N'SupplierRisk' AND s.RiskRating = N'L' + CAST(rk.Id AS NVARCHAR(10))
            WHERE s.Id = @id
            """, new { id }, tx);

    private sealed class DocRow
    {
        public int Id { get; set; }
        public int TypeId { get; set; }
        public string TypeName { get; set; } = "";
        public string Title { get; set; } = "";
        public DateTime? ExpiresOn { get; set; }
        public int CurrentVersion { get; set; }
        public int? ReviewId { get; set; }
        public int VersionId { get; set; }
        public int VersionNumber { get; set; }
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime UploadedUtc { get; set; }
        public string UploadedByName { get; set; } = "";
        public bool Watching { get; set; }
        public int Watchers { get; set; }
    }

    private sealed class ContractDocRow
    {
        public int ContractId { get; set; }
        public string Reference { get; set; } = "";
        public string ContractTitle { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public int VersionId { get; set; }
        public int VersionNumber { get; set; }
        public string FileName { get; set; } = "";
        public DateTime UploadedUtc { get; set; }
        public string UploadedByName { get; set; } = "";
    }

    private sealed class ReviewRow
    {
        public int Id { get; set; }
        public DateTime HeldOn { get; set; }
        public string Summary { get; set; } = "";
        public string? SupplierAttendees { get; set; }
        public string? Rating { get; set; }
        public byte? QualityScore { get; set; }
        public byte? DeliveryScore { get; set; }
        public byte? ServiceScore { get; set; }
        public byte? CostScore { get; set; }
        public string CreatedByName { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public byte[] RowVersion { get; set; } = [];
    }

    private sealed class ActionRow
    {
        public int Id { get; set; }
        public int ReviewId { get; set; }
        public string Text { get; set; } = "";
        public int? OwnerId { get; set; }
        public string? OwnerName { get; set; }
        public int? SupplierContactId { get; set; }
        public string? ContactName { get; set; }
        public DateTime DueOn { get; set; }
        public DateTime? DoneUtc { get; set; }
        public string? DoneByName { get; set; }
    }

    // ---------------------------------------------------------------- Inputs

    public sealed record RelationshipInput(string? RowVersion, int? RelationshipOwnerId, int? TierId, string? RiskRating, string? RiskReason, DateOnly? NextReviewDue);
    public sealed record ContactInput(string? Name, string? Role, string? Email, string? Phone);
    public sealed record DocumentInput(int? TypeId, string? Title, DateOnly? ExpiresOn, bool ClearExpiry = false);
    public sealed record RemindInput(bool On);
    /// <summary>For a JBS person (OwnerId, emailed), or for the supplier (ForSupplier, optionally one of its contacts; not emailed, D-104).</summary>
    public sealed record ActionInput(string? Text, int? OwnerId, DateOnly? DueOn, bool ForSupplier = false, int? SupplierContactId = null);
    public sealed record ReviewInput(string? RowVersion, DateOnly? HeldOn, string? Summary, string? SupplierAttendees, List<int>? AttendeeIds, string? Rating,
        int? QualityScore, int? DeliveryScore, int? ServiceScore, int? CostScore, List<ActionInput>? Actions, DateOnly? NextReviewDue, List<int>? ContactIds = null);
    public sealed record DoneInput(bool Done);

    private static string? Clean(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    private static async Task<IResult?> CheckListItemAsync(SqlConnection c, SqlTransaction tx, int? id, string list, string field, string message, Dictionary<string, string> errors)
    {
        if (id is null) return null;
        var ok = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WHERE Id = @id AND ListName = @list AND IsActive = 1", new { id, list }, tx);
        if (ok == 0) errors[field] = message;
        return null;
    }

    private static async Task<bool> ActivePersonAsync(SqlConnection c, SqlTransaction tx, int id) =>
        await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person WHERE Id = @id AND IsActive = 1", new { id }, tx) > 0;

    private static IResult NotFound() => ApiResults.NotFound("We couldn't find that supplier.");
    private static IResult NotProcurement() => ApiResults.NotAllowed("Only people ticked Procurement (and admins) can change a supplier's details and documents.");

    public static void MapSupplierPageEndpoints(this IEndpointRouteBuilder app)
    {
        // ------------------------------------------------------------ The page

        app.MapGet("/api/suppliers/{id:int}", async (int id, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var s = await LoadAsync(c, null, id);
            if (s is null) return NotFound();
            var p = ContractAccess.Params(me!, new { id });
            var contacts = await c.QueryAsync<(int Id, string Name, string? Role, string? Email, string? Phone)>(
                "SELECT Id, Name, Role, Email, Phone FROM contracts.SupplierContact WHERE SupplierId = @id AND IsRemoved = 0 ORDER BY Name", new { id });
            var contracts = await c.QueryAsync<(int Id, string Reference, string Title, string Stage, DateTime? EndDate, bool IsEvergreen, decimal? Value, string CurrencyCode, string OwnerName, bool IsConfidential)>($"""
                SELECT k.Id, k.Reference, k.Title, k.Stage, k.EndDate, k.IsEvergreen, k.Value, k.CurrencyCode, o.Name, k.IsConfidential
                FROM contracts.Contract k JOIN contracts.Person o ON o.Id = k.OwnerId
                WHERE k.SupplierId = @id AND {ContractAccess.VisibleSql("k")}
                ORDER BY CASE k.Stage WHEN N'Effective' THEN 0 WHEN N'Ended' THEN 2 WHEN N'Archived' THEN 3 WHEN N'Withdrawn' THEN 4 ELSE 1 END, k.Reference DESC
                """, p);
            var docs = (await c.QueryAsync<DocRow>("""
                SELECT d.Id, d.TypeId, t.Value AS TypeName, d.Title, d.ExpiresOn, d.CurrentVersion, d.ReviewId, v.Id AS VersionId, v.VersionNumber, v.FileName, v.SizeBytes,
                       v.UploadedUtc, u.Name AS UploadedByName,
                       CAST(CASE WHEN EXISTS (SELECT 1 FROM contracts.SupplierDocumentWatch w WHERE w.DocumentId = d.Id AND w.PersonId = @me) THEN 1 ELSE 0 END AS BIT) AS Watching,
                       (SELECT COUNT(*) FROM contracts.SupplierDocumentWatch w WHERE w.DocumentId = d.Id) AS Watchers
                FROM contracts.SupplierDocument d JOIN contracts.ListItem t ON t.Id = d.TypeId
                JOIN contracts.SupplierDocumentVersion v ON v.DocumentId = d.Id JOIN contracts.Person u ON u.Id = v.UploadedById
                WHERE d.SupplierId = @id AND d.IsRemoved = 0
                ORDER BY d.Id, v.VersionNumber DESC
                """, new { id, me = me!.Id })).ToList();
            var contractDocs = await c.QueryAsync<ContractDocRow>($"""
                SELECT k.Id AS ContractId, k.Reference, k.Title AS ContractTitle, COALESCE(dk.Value, d.Kind) AS Kind, d.Title, v.Id AS VersionId, v.VersionNumber, v.FileName, v.UploadedUtc, u.Name AS UploadedByName
                FROM contracts.Document d JOIN contracts.Contract k ON k.Id = d.ContractId
                JOIN contracts.DocumentVersion v ON v.DocumentId = d.Id AND v.VersionNumber = d.CurrentVersion JOIN contracts.Person u ON u.Id = v.UploadedById
                {DocumentEndpoints.KindJoin}
                WHERE k.SupplierId = @id AND d.IsRemoved = 0 AND {ContractAccess.VisibleSql("k")}
                ORDER BY v.UploadedUtc DESC
                """, p);
            var reviews = (await c.QueryAsync<ReviewRow>("""
                SELECT r.Id, r.HeldOn, r.Summary, r.SupplierAttendees, r.Rating, r.QualityScore, r.DeliveryScore, r.ServiceScore, r.CostScore, cb.Name AS CreatedByName, r.CreatedUtc, r.RowVersion
                FROM contracts.BusinessReview r JOIN contracts.Person cb ON cb.Id = r.CreatedById
                WHERE r.SupplierId = @id AND r.IsRemoved = 0 ORDER BY r.HeldOn DESC, r.Id DESC
                """, new { id })).ToList();
            var attendees = (await c.QueryAsync<(int ReviewId, int PersonId, string Name)>("""
                SELECT a.ReviewId, a.PersonId, p.Name FROM contracts.BusinessReviewAttendee a JOIN contracts.Person p ON p.Id = a.PersonId
                JOIN contracts.BusinessReview r ON r.Id = a.ReviewId WHERE r.SupplierId = @id ORDER BY p.Name
                """, new { id })).ToLookup(a => a.ReviewId);
            var actions = (await ActionsAsync(c, null, id)).ToLookup(a => a.ReviewId);
            var reviewContacts = (await ReviewContactsAsync(c, null, id)).ToLookup(x => x.ReviewId);
            var canManage = CanManage(me);
            var canReview = CanReview(me, s.RelationshipOwnerId);
            object Doc(IGrouping<int, DocRow> g)
            {
                var latest = g.First();
                return new
                {
                    latest.Id, latest.TypeId, latest.TypeName, latest.Title, ExpiresOn = D(latest.ExpiresOn), latest.ReviewId, latest.Watching, latest.Watchers,
                    Versions = g.Select(v => new { Id = v.VersionId, Number = v.VersionNumber, v.FileName, v.SizeBytes, UploadedUtc = Utc(v.UploadedUtc), v.UploadedByName }),
                };
            }
            var docGroups = docs.GroupBy(d => d.Id).ToList();
            return Results.Ok(new
            {
                supplier = new
                {
                    s.Id, s.Name, s.Abn, s.VendorNumber, s.Notes, s.IsActive, s.RelationshipOwnerId, s.RelationshipOwnerName, s.TierId, s.TierName, s.RiskRating, s.RiskName, s.RiskReason,
                    NextReviewDue = D(s.NextReviewDue), RowVersion = Convert.ToBase64String(s.RowVersion),
                },
                can = new { manage = canManage, review = canReview },
                contacts = contacts.Select(x => new { x.Id, x.Name, x.Role, x.Email, x.Phone }),
                contracts = contracts.Select(k => new
                {
                    k.Id, k.Reference, k.Title, k.Stage, StageName = ContractEndpoints.StageNames[k.Stage], EndDate = D(k.EndDate), k.IsEvergreen, k.Value, k.CurrencyCode, k.OwnerName, k.IsConfidential,
                }),
                documents = docGroups.Where(g => g.First().ReviewId is null).Select(Doc),
                contractDocuments = contractDocs.Select(d => new
                {
                    d.ContractId, d.Reference, d.ContractTitle, Kind = DocumentEndpoints.Kinds.GetValueOrDefault(d.Kind, d.Kind), d.Title, d.VersionId, d.VersionNumber, d.FileName,
                    UploadedUtc = Utc(d.UploadedUtc), d.UploadedByName,
                }),
                reviews = reviews.Select(r => new
                {
                    r.Id, HeldOn = DateOnly.FromDateTime(r.HeldOn), r.Summary, r.SupplierAttendees, r.Rating, RatingName = r.Rating is { } rt ? RatingNames[rt] : null,
                    r.QualityScore, r.DeliveryScore, r.ServiceScore, r.CostScore, r.CreatedByName, CreatedUtc = Utc(r.CreatedUtc), RowVersion = Convert.ToBase64String(r.RowVersion),
                    Attendees = attendees[r.Id].Select(a => new { Id = a.PersonId, a.Name }),
                    Contacts = reviewContacts[r.Id].Select(x => new { Id = x.ContactId, x.Name, x.Role }),
                    Actions = actions[r.Id].Select(a => new
                    {
                        a.Id, a.Text, a.OwnerId, OwnerName = Who(a, s.Name), ForSupplier = a.OwnerId is null, a.SupplierContactId, DueOn = DateOnly.FromDateTime(a.DueOn),
                        Done = a.DoneUtc is not null, DoneUtc = a.DoneUtc is { } du ? Utc(du) : (DateTime?)null, a.DoneByName, CanTick = canReview || a.OwnerId == me.Id,
                    }),
                    Documents = docGroups.Where(g => g.First().ReviewId == r.Id).Select(Doc),
                }),
            });
        });

        // ------------------------------------------------------------ Details: relationship owner, tier, risk, next review due

        app.MapPut("/api/suppliers/{id:int}/relationship", async (int id, RelationshipInput body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!CanManage(me!)) return NotProcurement();
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await LoadAsync(c, tx, id, forUpdate: true);
            if (before is null) return NotFound();
            if (body.RowVersion is not null && body.RowVersion != Convert.ToBase64String(before.RowVersion))
                return ApiResults.Changed("Someone else changed this supplier just now. Refresh to see their changes, then try again.");
            var errors = new Dictionary<string, string>();
            if (body.RelationshipOwnerId is { } ro && !await ActivePersonAsync(c, tx, ro)) errors["relationshipOwnerId"] = "Choose someone who has access to JBS Contracts.";
            await CheckListItemAsync(c, tx, body.TierId, "SupplierTier", "tierId", "Choose a tier from the list.", errors);
            // Low, Medium, High, or a level an admin added (not hidden). Every level but Low asks why.
            var addedRisk = body.RiskRating is { Length: > 1 } code && code[0] == 'L' && int.TryParse(code[1..], out var riskItem)
                && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WHERE Id = @riskItem AND ListName = N'SupplierRisk' AND IsActive = 1", new { riskItem }, tx) > 0;
            if (body.RiskRating is not (null or "Low" or "Medium" or "High") && !addedRisk) errors["riskRating"] = "Choose a risk level from the list.";
            var reason = Clean(body.RiskReason, 500);
            if (body.RiskRating is not (null or "Low") && reason is null) errors["riskReason"] = "Say briefly why the risk is at this level.";
            if (errors.Count > 0) return ApiResults.Invalid("Check the details.", errors);

            await c.ExecuteAsync("""
                UPDATE contracts.Supplier SET RelationshipOwnerId = @ro, TierId = @tier, RiskRating = @risk, RiskReason = @reason, NextReviewDue = @next,
                    UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id
                """, new { ro = body.RelationshipOwnerId, tier = body.TierId, risk = body.RiskRating, reason = body.RiskRating is null ? null : reason,
                    next = body.NextReviewDue?.ToDateTime(TimeOnly.MinValue), me = me!.Id, id }, tx);
            var after = await LoadAsync(c, tx, id);
            var changes = new List<string>();
            if (before.RelationshipOwnerName != after!.RelationshipOwnerName) changes.Add($"relationship owner to {after.RelationshipOwnerName ?? "nobody"}");
            if (before.TierName != after.TierName) changes.Add($"tier to {after.TierName ?? "none"}");
            if (before.RiskRating != after.RiskRating || before.RiskReason != after.RiskReason) changes.Add($"risk to {after.RiskName ?? "not rated"}{(after.RiskReason is { } r ? $" (\"{r}\")" : "")}");
            if (before.NextReviewDue != after.NextReviewDue) changes.Add($"next business review due to {(after.NextReviewDue is { } n ? Day(n) : "not set")}");
            if (changes.Count > 0)
                await audit.WriteAsync(c, tx, me.AsActor(), "supplier.relationship-changed", "Supplier", id, $"{me.Name} changed {after.Name}'s {string.Join(", ", changes)}.",
                    new { before = new { before.RelationshipOwnerName, before.TierName, before.RiskName, before.RiskReason, NextReviewDue = D(before.NextReviewDue) },
                          after = new { after.RelationshipOwnerName, after.TierName, after.RiskName, after.RiskReason, NextReviewDue = D(after.NextReviewDue) } });
            await tx.CommitAsync();
            return Results.Ok(new { rowVersion = Convert.ToBase64String(after.RowVersion) });
        });

        // ------------------------------------------------------------ Contacts

        async Task<IResult> SaveContactAsync(int id, int? contactId, ContactInput body, CurrentUser current, Db db, AuditLog audit)
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var errors = new Dictionary<string, string>();
            var name = Clean(body.Name, 200);
            var email = Clean(body.Email, 254);
            if (name is null) errors["name"] = "Type their name.";
            if (email is not null && !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) errors["email"] = "That doesn't look like an email address.";
            if (errors.Count > 0) return ApiResults.Invalid("Check the contact.", errors);
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var s = await LoadAsync(c, tx, id);
            if (s is null) return NotFound();
            // Adding a contact is also open to whoever can add business reviews (from the review form, D-104); changing one isn't.
            if (!(CanManage(me!) || (contactId is null && CanReview(me!, s.RelationshipOwnerId)))) return NotProcurement();
            var args = new { id, contactId, name, role = Clean(body.Role, 200), email, phone = Clean(body.Phone, 50), me = me!.Id };
            if (contactId is null)
            {
                contactId = await c.ExecuteScalarAsync<int>("""
                    INSERT INTO contracts.SupplierContact (SupplierId, Name, Role, Email, Phone, CreatedById) OUTPUT INSERTED.Id VALUES (@id, @name, @role, @email, @phone, @me)
                    """, args, tx);
                await audit.WriteAsync(c, tx, me.AsActor(), "supplier.contact-added", "Supplier", id, $"{me.Name} added {name} as a contact for {s.Name}.", args);
            }
            else
            {
                var n = await c.ExecuteAsync("""
                    UPDATE contracts.SupplierContact SET Name = @name, Role = @role, Email = @email, Phone = @phone, UpdatedUtc = SYSUTCDATETIME()
                    WHERE Id = @contactId AND SupplierId = @id AND IsRemoved = 0
                    """, args, tx);
                if (n == 0) return ApiResults.NotFound("We couldn't find that contact.");
                await audit.WriteAsync(c, tx, me.AsActor(), "supplier.contact-changed", "Supplier", id, $"{me.Name} changed {s.Name}'s contact {name}.", args);
            }
            await tx.CommitAsync();
            return Results.Ok(new { id = contactId });
        }
        app.MapPost("/api/suppliers/{id:int}/contacts", (int id, ContactInput body, CurrentUser current, Db db, AuditLog audit) => SaveContactAsync(id, null, body, current, db, audit));
        app.MapPut("/api/suppliers/{id:int}/contacts/{contactId:int}", (int id, int contactId, ContactInput body, CurrentUser current, Db db, AuditLog audit) =>
            SaveContactAsync(id, contactId, body, current, db, audit));
        app.MapPost("/api/suppliers/{id:int}/contacts/{contactId:int}/remove", async (int id, int contactId, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!CanManage(me!)) return NotProcurement();
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var name = await c.QuerySingleOrDefaultAsync<string>("SELECT Name FROM contracts.SupplierContact WHERE Id = @contactId AND SupplierId = @id AND IsRemoved = 0", new { id, contactId }, tx);
            if (name is null) return ApiResults.NotFound("We couldn't find that contact.");
            await c.ExecuteAsync("UPDATE contracts.SupplierContact SET IsRemoved = 1, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @contactId", new { contactId }, tx);
            var supplier = await c.ExecuteScalarAsync<string>("SELECT Name FROM contracts.Supplier WHERE Id = @id", new { id }, tx);
            await audit.WriteAsync(c, tx, me!.AsActor(), "supplier.contact-removed", "Supplier", id, $"{me.Name} removed {name} from {supplier}'s contacts.");
            await tx.CommitAsync();
            return Results.Ok(new { });
        });

        // ------------------------------------------------------------ The supplier's own documents

        // Add a document (type, optional expiry), a new version of one (documentId), or a review's deck (reviewId).
        app.MapPost("/api/suppliers/{id:int}/documents", async (int id, HttpRequest request, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (settings.DocumentsDirectory is not { } root)
                return ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "no-storage", "Documents can't be saved yet because the server has no data folder set. Tell your admin.");
            if (!request.HasFormContentType) return ApiResults.Invalid("Choose a file to upload.", new() { ["file"] = "Choose a file to upload." });
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            int? typeId = int.TryParse(form["typeId"], out var t) ? t : null;
            int? documentId = int.TryParse(form["documentId"], out var d) ? d : null;
            int? reviewId = int.TryParse(form["reviewId"], out var r) ? r : null;
            DateOnly? expiresOn = DateOnly.TryParseExact(form["expiresOn"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var e) ? e : null;
            var errors = new Dictionary<string, string>();
            if (DocumentEndpoints.FileProblem(file) is { } problem) errors["file"] = problem;
            if (!string.IsNullOrEmpty(form["expiresOn"]) && expiresOn is null) errors["expiresOn"] = "Choose a date, or leave it empty if it doesn't expire.";
            if (errors.Count > 0) return ApiResults.Invalid("Check the file.", errors);

            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var s = await LoadAsync(c, tx, id);
            if (s is null) return NotFound();
            // Review decks: whoever can add reviews; everything else: Procurement and admins.
            if (reviewId is { } rv)
            {
                if (!CanReview(me!, s.RelationshipOwnerId)) return ApiResults.NotAllowed("Only Procurement, admins and the supplier's relationship owner can add to a business review.");
                if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.BusinessReview WHERE Id = @rv AND SupplierId = @id AND IsRemoved = 0", new { rv, id }, tx) == 0)
                    return ApiResults.NotFound("We couldn't find that business review.");
                typeId ??= await c.ExecuteScalarAsync<int?>("SELECT TOP (1) Id FROM contracts.ListItem WHERE ListName = N'SupplierDocumentType' AND Value = N'Business review'", transaction: tx);
            }
            else if (!CanManage(me!)) return NotProcurement();
            if (documentId is null)
            {
                await CheckListItemAsync(c, tx, typeId, "SupplierDocumentType", "typeId", "Choose what kind of document this is.", errors);
                if (typeId is null) errors["typeId"] = "Choose what kind of document this is.";
                if (errors.Count > 0) return ApiResults.Invalid("Check the document.", errors);
            }

            var stored = await DocumentEndpoints.StoreAsync(root, file!);
            try
            {
                int version;
                string title;
                var p = new DynamicParameters(new { id, me = me!.Id });
                p.Add("expires", expiresOn?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
                if (documentId is { } existing)
                {
                    var doc = await c.QuerySingleOrDefaultAsync<(string Title, int CurrentVersion, int? ReviewId)>(
                        "SELECT Title, CurrentVersion, ReviewId FROM contracts.SupplierDocument WITH (UPDLOCK) WHERE Id = @existing AND SupplierId = @id AND IsRemoved = 0", new { existing, id }, tx);
                    if (doc == default) { File.Delete(stored.Path); return ApiResults.NotFound("We couldn't find that document for this supplier."); }
                    if (doc.ReviewId is null && !CanManage(me)) { File.Delete(stored.Path); return NotProcurement(); }
                    version = doc.CurrentVersion + 1;
                    title = doc.Title;
                    p.Add("existing", existing);
                    p.Add("version", version);
                    // A new version usually comes with a new expiry date (a renewed certificate); without one, the old date stays.
                    await c.ExecuteAsync("UPDATE contracts.SupplierDocument SET CurrentVersion = @version, ExpiresOn = COALESCE(@expires, ExpiresOn), UpdatedUtc = SYSUTCDATETIME() WHERE Id = @existing", p, tx);
                }
                else
                {
                    version = 1;
                    title = Path.GetFileNameWithoutExtension(stored.FileName);
                    if (title.Length == 0) title = "Document";
                    if (title.Length > 200) title = title[..200];
                    p.Add("typeId", typeId);
                    p.Add("title", title);
                    p.Add("reviewId", reviewId);
                    documentId = await c.ExecuteScalarAsync<int>("""
                        INSERT INTO contracts.SupplierDocument (SupplierId, TypeId, Title, ExpiresOn, ReviewId, CreatedById) OUTPUT INSERTED.Id VALUES (@id, @typeId, @title, @expires, @reviewId, @me)
                        """, p, tx);
                }
                var versionId = await c.ExecuteScalarAsync<int>("""
                    INSERT INTO contracts.SupplierDocumentVersion (DocumentId, VersionNumber, FileName, ContentType, SizeBytes, Sha256, StoredName, UploadedById)
                    OUTPUT INSERTED.Id VALUES (@documentId, @version, @fileName, @type, @size, @sha, @stored, @me)
                    """, new { documentId, version, fileName = stored.FileName, type = stored.ContentType, size = stored.Size, sha = stored.Sha256, stored = stored.StoredName, me = me.Id }, tx);
                var expiry = expiresOn is { } x ? $", expiring {Day(x.ToDateTime(TimeOnly.MinValue))}" : "";
                await audit.WriteAsync(c, tx, me.AsActor(), version == 1 ? "supplier.document-added" : "supplier.document-version-added", "Supplier", id,
                    version == 1 ? $"{me.Name} added \"{title}\" to {s.Name}{(reviewId is null ? "" : "'s business review")}{expiry}." : $"{me.Name} added version {version} of \"{title}\" for {s.Name}{expiry}.",
                    new { documentId, versionId, stored.FileName, stored.Size, stored.Sha256, expiresOn });
                await tx.CommitAsync();
                return Results.Created($"/api/supplier-documents/{versionId}/file", new { id = documentId, versionId, version });
            }
            catch
            {
                File.Delete(stored.Path);
                throw;
            }
        });

        app.MapPut("/api/suppliers/{id:int}/documents/{documentId:int}", async (int id, int documentId, DocumentInput body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!CanManage(me!)) return NotProcurement();
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await c.QuerySingleOrDefaultAsync<(string Title, int TypeId, DateTime? ExpiresOn, string Supplier)>("""
                SELECT d.Title, d.TypeId, d.ExpiresOn, s.Name FROM contracts.SupplierDocument d WITH (UPDLOCK) JOIN contracts.Supplier s ON s.Id = d.SupplierId
                WHERE d.Id = @documentId AND d.SupplierId = @id AND d.IsRemoved = 0
                """, new { id, documentId }, tx);
            if (before == default) return ApiResults.NotFound("We couldn't find that document for this supplier.");
            var errors = new Dictionary<string, string>();
            var title = Clean(body.Title, 200);
            if (title is null) errors["title"] = "Give it a name.";
            await CheckListItemAsync(c, tx, body.TypeId ?? before.TypeId, "SupplierDocumentType", "typeId", "Choose what kind of document this is.", errors);
            if (errors.Count > 0) return ApiResults.Invalid("Check the document.", errors);
            var p = new DynamicParameters(new { documentId, title, typeId = body.TypeId ?? before.TypeId });
            p.Add("expires", body.ClearExpiry ? null : (body.ExpiresOn?.ToDateTime(TimeOnly.MinValue) ?? before.ExpiresOn), System.Data.DbType.Date);
            await c.ExecuteAsync("UPDATE contracts.SupplierDocument SET Title = @title, TypeId = @typeId, ExpiresOn = @expires, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @documentId", p, tx);
            var expires = p.Get<DateTime?>("expires");
            await audit.WriteAsync(c, tx, me!.AsActor(), "supplier.document-changed", "Supplier", id,
                $"{me.Name} changed {before.Supplier}'s document \"{title}\"{(expires != before.ExpiresOn ? $": expiry {(expires is { } x ? Day(x) : "removed")}" : "")}.",
                new { documentId, before = new { before.Title, before.TypeId, ExpiresOn = D(before.ExpiresOn) }, after = new { title, typeId = body.TypeId ?? before.TypeId, ExpiresOn = D(expires) } });
            await tx.CommitAsync();
            return Results.Ok(new { });
        });

        app.MapPost("/api/suppliers/{id:int}/documents/{documentId:int}/remove", async (int id, int documentId, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var doc = await c.QuerySingleOrDefaultAsync<(string Title, int? ReviewId, string Supplier, int? RelationshipOwnerId)>("""
                SELECT d.Title, d.ReviewId, s.Name, s.RelationshipOwnerId FROM contracts.SupplierDocument d JOIN contracts.Supplier s ON s.Id = d.SupplierId
                WHERE d.Id = @documentId AND d.SupplierId = @id AND d.IsRemoved = 0
                """, new { id, documentId }, tx);
            if (doc == default) return ApiResults.NotFound("We couldn't find that document for this supplier.");
            if (!(CanManage(me!) || (doc.ReviewId is not null && CanReview(me!, doc.RelationshipOwnerId)))) return NotProcurement();
            await c.ExecuteAsync("UPDATE contracts.SupplierDocument SET IsRemoved = 1, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @documentId", new { documentId }, tx);
            await audit.WriteAsync(c, tx, me!.AsActor(), "supplier.document-removed", "Supplier", id, $"{me.Name} removed \"{doc.Title}\" from {doc.Supplier} (the file and its history are kept).", new { documentId });
            await tx.CommitAsync();
            return Results.Ok(new { });
        });

        // "Remind me before it expires": each person, for themselves; off unless they tick it.
        app.MapPost("/api/suppliers/{id:int}/documents/{documentId:int}/remind", async (int id, int documentId, RemindInput body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var doc = await c.QuerySingleOrDefaultAsync<(string Title, DateTime? ExpiresOn, string Supplier)>("""
                SELECT d.Title, d.ExpiresOn, s.Name FROM contracts.SupplierDocument d JOIN contracts.Supplier s ON s.Id = d.SupplierId
                WHERE d.Id = @documentId AND d.SupplierId = @id AND d.IsRemoved = 0
                """, new { id, documentId }, tx);
            if (doc == default) return ApiResults.NotFound("We couldn't find that document for this supplier.");
            if (body.On && doc.ExpiresOn is null) return ApiResults.Invalid("This document has no expiry date.", new() { ["on"] = "Add an expiry date first." });
            var changed = body.On
                ? await c.ExecuteAsync("IF NOT EXISTS (SELECT 1 FROM contracts.SupplierDocumentWatch WHERE DocumentId = @documentId AND PersonId = @me) INSERT INTO contracts.SupplierDocumentWatch (DocumentId, PersonId) VALUES (@documentId, @me)", new { documentId, me = me!.Id }, tx)
                : await c.ExecuteAsync("DELETE FROM contracts.SupplierDocumentWatch WHERE DocumentId = @documentId AND PersonId = @me", new { documentId, me = me!.Id }, tx);
            if (changed > 0)
                await audit.WriteAsync(c, tx, me.AsActor(), body.On ? "supplier.document-remind-on" : "supplier.document-remind-off", "Supplier", id,
                    body.On ? $"{me.Name} asked to be reminded before {doc.Supplier}'s \"{doc.Title}\" expires." : $"{me.Name} stopped expiry reminders for {doc.Supplier}'s \"{doc.Title}\".", new { documentId });
            await tx.CommitAsync();
            return Results.Ok(new { on = body.On });
        });

        app.MapGet("/api/supplier-documents/{versionId:int}/file", async (int versionId, CurrentUser current, Db db, AppSettings settings) =>
        {
            var (_, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var row = await c.QuerySingleOrDefaultAsync<(string FileName, string ContentType, string StoredName)>(
                "SELECT FileName, ContentType, StoredName FROM contracts.SupplierDocumentVersion WHERE Id = @versionId", new { versionId });
            if (row == default) return ApiResults.NotFound("We couldn't find that document.");
            var path = Path.Combine(settings.DocumentsDirectory ?? "", row.StoredName.Replace('/', Path.DirectorySeparatorChar));
            if (settings.DocumentsDirectory is null || !File.Exists(path))
                return ApiResults.Problem(StatusCodes.Status410Gone, "file-missing", "The file for this document is missing from the server. Tell your admin.");
            return Results.File(path, row.ContentType, row.FileName);
        });

        // ------------------------------------------------------------ Business reviews

        async Task<IResult> SaveReviewAsync(int id, int? reviewId, ReviewInput body, CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock)
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var s = await LoadAsync(c, tx, id, forUpdate: true);
            if (s is null) return NotFound();
            if (!CanReview(me!, s.RelationshipOwnerId)) return ApiResults.NotAllowed("Only Procurement, admins and the supplier's relationship owner can add or change business reviews.");

            var today = DateOnly.FromDateTime(HomeEndpoints.Today(clock));
            var errors = new Dictionary<string, string>();
            var summary = Clean(body.Summary, 4000);
            if (body.HeldOn is null) errors["heldOn"] = "Choose the date it was held.";
            else if (body.HeldOn > today) errors["heldOn"] = "It can't be in the future. Add a review once it has been held.";
            if (summary is null) errors["summary"] = "Write a short summary: what was discussed and agreed.";
            if (body.Rating is not (null or "Good" or "NeedsImprovement" or "Poor")) errors["rating"] = "Choose Good, Needs improvement or Poor.";
            foreach (var (key, v) in new[] { ("qualityScore", body.QualityScore), ("deliveryScore", body.DeliveryScore), ("serviceScore", body.ServiceScore), ("costScore", body.CostScore) })
                if (v is not null and not (>= 1 and <= 5)) errors[key] = "Score from 1 (poor) to 5 (excellent).";
            var attendees = (body.AttendeeIds ?? []).Distinct().ToList();
            foreach (var a in attendees)
                if (!await ActivePersonAsync(c, tx, a)) { errors["attendeeIds"] = "Choose people who have access to JBS Contracts."; break; }
            var supplierContacts = (await c.QueryAsync<(int Id, bool IsRemoved)>("SELECT Id, IsRemoved FROM contracts.SupplierContact WHERE SupplierId = @id", new { id }, tx))
                .ToDictionary(x => x.Id, x => x.IsRemoved);
            var contactIds = (body.ContactIds ?? []).Distinct().ToList();
            if (contactIds.Any(x => !supplierContacts.ContainsKey(x))) errors["contactIds"] = "Choose from this supplier's contacts.";
            var actions = body.Actions ?? [];
            for (var i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (Clean(a.Text, 500) is null) errors[$"actions.{i}.text"] = "Say what needs doing.";
                if (ActionWhoProblem(a, supplierContacts, await (a.OwnerId is { } o ? ActivePersonAsync(c, tx, o) : Task.FromResult(false))) is { } who) errors[$"actions.{i}.ownerId"] = who;
                if (a.DueOn is null) errors[$"actions.{i}.dueOn"] = "Choose when it's due.";
            }
            if (errors.Count > 0) return ApiResults.Invalid("Check the review.", errors);

            var args = new
            {
                id, reviewId, held = body.HeldOn!.Value.ToDateTime(TimeOnly.MinValue), summary, attendeesText = Clean(body.SupplierAttendees, 1000), body.Rating,
                q = (byte?)body.QualityScore, d = (byte?)body.DeliveryScore, sv = (byte?)body.ServiceScore, co = (byte?)body.CostScore, me = me!.Id,
            };
            if (reviewId is null)
            {
                reviewId = await c.ExecuteScalarAsync<int>("""
                    INSERT INTO contracts.BusinessReview (SupplierId, HeldOn, Summary, SupplierAttendees, Rating, QualityScore, DeliveryScore, ServiceScore, CostScore, CreatedById)
                    OUTPUT INSERTED.Id VALUES (@id, @held, @summary, @attendeesText, @Rating, @q, @d, @sv, @co, @me)
                    """, args, tx);
            }
            else
            {
                var current_ = await c.QuerySingleOrDefaultAsync<byte[]>("SELECT RowVersion FROM contracts.BusinessReview WITH (UPDLOCK) WHERE Id = @reviewId AND SupplierId = @id AND IsRemoved = 0", new { reviewId, id }, tx);
                if (current_ is null) return ApiResults.NotFound("We couldn't find that business review.");
                if (body.RowVersion is not null && body.RowVersion != Convert.ToBase64String(current_))
                    return ApiResults.Changed("Someone else changed this review just now. Refresh to see their changes, then try again.");
                await c.ExecuteAsync("""
                    UPDATE contracts.BusinessReview SET HeldOn = @held, Summary = @summary, SupplierAttendees = @attendeesText, Rating = @Rating, QualityScore = @q,
                        DeliveryScore = @d, ServiceScore = @sv, CostScore = @co, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @reviewId
                    """, args, tx);
                await c.ExecuteAsync("DELETE FROM contracts.BusinessReviewAttendee WHERE ReviewId = @reviewId", new { reviewId }, tx);
                await c.ExecuteAsync("DELETE FROM contracts.BusinessReviewContact WHERE ReviewId = @reviewId", new { reviewId }, tx);
            }
            foreach (var a in attendees)
                await c.ExecuteAsync("INSERT INTO contracts.BusinessReviewAttendee (ReviewId, PersonId) VALUES (@reviewId, @a)", new { reviewId, a }, tx);
            foreach (var x in contactIds)
                await c.ExecuteAsync("INSERT INTO contracts.BusinessReviewContact (ReviewId, ContactId) VALUES (@reviewId, @x)", new { reviewId, x }, tx);
            var heldText = Day(args.held);
            foreach (var a in actions)
                await AddActionAsync(c, tx, audit, settings, me, s, reviewId.Value, args.held, Clean(a.Text, 500)!, a.ForSupplier ? null : a.OwnerId, a.ForSupplier ? a.SupplierContactId : null, a.DueOn!.Value);
            if (body.NextReviewDue is { } next)
                await c.ExecuteAsync("UPDATE contracts.Supplier SET NextReviewDue = @next, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @id", new { next = next.ToDateTime(TimeOnly.MinValue), id }, tx);
            var scores = string.Join(", ", new[] { ("quality", body.QualityScore), ("delivery", body.DeliveryScore), ("service", body.ServiceScore), ("cost", body.CostScore) }
                .Where(x => x.Item2 is not null).Select(x => $"{x.Item1} {x.Item2}/5"));
            await audit.WriteAsync(c, tx, me.AsActor(), args.reviewId is null ? "supplier.review-added" : "supplier.review-changed", "Supplier", id,
                $"{me.Name} {(args.reviewId is null ? "added" : "changed")} the business review with {s.Name} held {heldText}" +
                $"{(body.Rating is { } rt ? $": {RatingNames[rt]}" : "")}{(scores.Length > 0 ? $" ({scores})" : "")}" +
                $"{(actions.Count > 0 ? $", with {actions.Count} {(actions.Count == 1 ? "action" : "actions")}" : "")}" +
                $"{(body.NextReviewDue is { } nx ? $"; next review due {Day(nx.ToDateTime(TimeOnly.MinValue))}" : "")}.",
                new { reviewId, args.held, summary, attendees, contactIds, args.attendeesText, body.Rating, body.QualityScore, body.DeliveryScore, body.ServiceScore, body.CostScore, body.NextReviewDue });
            await tx.CommitAsync();
            return Results.Ok(new { id = reviewId });
        }

        app.MapPost("/api/suppliers/{id:int}/reviews", (int id, ReviewInput body, CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock) =>
            SaveReviewAsync(id, null, body, current, db, audit, settings, clock));
        app.MapPut("/api/suppliers/{id:int}/reviews/{reviewId:int}", (int id, int reviewId, ReviewInput body, CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock) =>
            SaveReviewAsync(id, reviewId, body, current, db, audit, settings, clock));

        // Another action on an existing review.
        app.MapPost("/api/suppliers/{id:int}/reviews/{reviewId:int}/actions", async (int id, int reviewId, ActionInput body, CurrentUser current, Db db, AuditLog audit, AppSettings settings) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var s = await LoadAsync(c, tx, id);
            if (s is null) return NotFound();
            if (!CanReview(me!, s.RelationshipOwnerId)) return ApiResults.NotAllowed("Only Procurement, admins and the supplier's relationship owner can add actions.");
            var held = await c.ExecuteScalarAsync<DateTime?>("SELECT HeldOn FROM contracts.BusinessReview WHERE Id = @reviewId AND SupplierId = @id AND IsRemoved = 0", new { reviewId, id }, tx);
            if (held is null) return ApiResults.NotFound("We couldn't find that business review.");
            var errors = new Dictionary<string, string>();
            var text = Clean(body.Text, 500);
            if (text is null) errors["text"] = "Say what needs doing.";
            var supplierContacts = (await c.QueryAsync<(int Id, bool IsRemoved)>("SELECT Id, IsRemoved FROM contracts.SupplierContact WHERE SupplierId = @id", new { id }, tx))
                .ToDictionary(x => x.Id, x => x.IsRemoved);
            if (ActionWhoProblem(body, supplierContacts, await (body.OwnerId is { } o ? ActivePersonAsync(c, tx, o) : Task.FromResult(false))) is { } who) errors["ownerId"] = who;
            if (body.DueOn is null) errors["dueOn"] = "Choose when it's due.";
            if (errors.Count > 0) return ApiResults.Invalid("Check the action.", errors);
            var actionId = await AddActionAsync(c, tx, audit, settings, me!, s, reviewId, held.Value, text!, body.ForSupplier ? null : body.OwnerId,
                body.ForSupplier ? body.SupplierContactId : null, body.DueOn!.Value);
            await tx.CommitAsync();
            return Results.Ok(new { id = actionId });
        });

        // The review as a Word document (D-104), to edit and send as the minutes. Anyone who can see the supplier can download it.
        app.MapGet("/api/suppliers/{id:int}/reviews/{reviewId:int}/document", async (int id, int reviewId, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var s = await LoadAsync(c, null, id);
            if (s is null) return NotFound();
            var r = await c.QuerySingleOrDefaultAsync<ReviewRow>("""
                SELECT r.Id, r.HeldOn, r.Summary, r.SupplierAttendees, r.Rating, r.QualityScore, r.DeliveryScore, r.ServiceScore, r.CostScore, cb.Name AS CreatedByName, r.CreatedUtc, r.RowVersion
                FROM contracts.BusinessReview r JOIN contracts.Person cb ON cb.Id = r.CreatedById WHERE r.Id = @reviewId AND r.SupplierId = @id AND r.IsRemoved = 0
                """, new { reviewId, id });
            if (r is null) return ApiResults.NotFound("We couldn't find that business review.");
            var jbs = (await c.QueryAsync<string>("""
                SELECT p.Name FROM contracts.BusinessReviewAttendee a JOIN contracts.Person p ON p.Id = a.PersonId WHERE a.ReviewId = @reviewId ORDER BY p.Name
                """, new { reviewId })).ToList();
            var theirs = (await ReviewContactsAsync(c, null, id, reviewId)).Select(x => x.Role is { } role ? $"{x.Name} ({role})" : x.Name).ToList();
            if (r.SupplierAttendees is { } typed) theirs.Add(typed); // older reviews: the names as typed
            var actions = (await ActionsAsync(c, null, id, reviewId))
                .Select(a => new ReviewDocument.Action(a.Text, Who(a, s.Name), DateOnly.FromDateTime(a.DueOn), a.DoneUtc is not null)).ToList();
            var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).DateTime;
            var bytes = ReviewDocument.Build(new ReviewDocument.Content(
                s.Name, DateOnly.FromDateTime(r.HeldOn), r.Rating is { } rt ? RatingNames[rt] : null,
                [("Quality", r.QualityScore), ("Delivery", r.DeliveryScore), ("Service", r.ServiceScore), ("Cost", r.CostScore)],
                r.Summary, jbs, theirs, actions, s.NextReviewDue is { } nd ? DateOnly.FromDateTime(nd) : null, me!.Name, now));
            var safe = new string(s.Name.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' ? ch : ' ').ToArray()).Trim();
            return Results.File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                $"Business review {safe} {r.HeldOn:yyyy-MM-dd}.docx");
        });

        // Tick an action done (or not done): its owner, or anyone who can add reviews for the supplier.
        app.MapPost("/api/review-actions/{actionId:int}/done", async (int actionId, DoneInput body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var a = await c.QuerySingleOrDefaultAsync<(string Text, int? OwnerId, DateTime? DoneUtc, int SupplierId, string Supplier, int? RelationshipOwnerId)>("""
                SELECT a.Text, a.OwnerId, a.DoneUtc, s.Id, s.Name, s.RelationshipOwnerId FROM contracts.BusinessReviewAction a WITH (UPDLOCK)
                JOIN contracts.BusinessReview r ON r.Id = a.ReviewId JOIN contracts.Supplier s ON s.Id = r.SupplierId
                WHERE a.Id = @actionId AND r.IsRemoved = 0
                """, new { actionId }, tx);
            if (a == default) return ApiResults.NotFound("We couldn't find that action.");
            if (a.OwnerId != me!.Id && !CanReview(me, a.RelationshipOwnerId)) return ApiResults.NotAllowed("Only the action's owner, Procurement, admins or the relationship owner can tick it.");
            if (body.Done == (a.DoneUtc is not null)) return Results.Ok(new { done = body.Done });
            await c.ExecuteAsync(body.Done
                ? "UPDATE contracts.BusinessReviewAction SET DoneUtc = SYSUTCDATETIME(), DoneById = @me WHERE Id = @actionId"
                : "UPDATE contracts.BusinessReviewAction SET DoneUtc = NULL, DoneById = NULL WHERE Id = @actionId", new { actionId, me = me.Id }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), body.Done ? "supplier.action-done" : "supplier.action-reopened", "Supplier", a.SupplierId,
                body.Done ? $"{me.Name} ticked done the {a.Supplier} review action \"{a.Text}\"." : $"{me.Name} reopened the {a.Supplier} review action \"{a.Text}\".", new { actionId });
            await tx.CommitAsync();
            return Results.Ok(new { done = body.Done });
        });
    }

    /// <summary>Why an action's "who" doesn't work, or null: an active JBS person, or the supplier (optionally one of its current contacts).</summary>
    private static string? ActionWhoProblem(ActionInput a, Dictionary<int, bool> supplierContacts, bool ownerIsActive) =>
        a.ForSupplier
            ? a.SupplierContactId is { } sc && (!supplierContacts.TryGetValue(sc, out var removed) || removed) ? "Choose one of this supplier's contacts, or the supplier." : null
            : ownerIsActive ? null : "Choose who will do it.";

    /// <summary>Who an action is for, in words: the JBS person, or the supplier's contact (or the supplier).</summary>
    private static string Who(ActionRow a, string supplier) => a.OwnerName ?? (a.ContactName is { } cn ? $"{cn} ({supplier})" : supplier);

    private static Task<IEnumerable<ActionRow>> ActionsAsync(SqlConnection c, SqlTransaction? tx, int supplierId, int? reviewId = null) => c.QueryAsync<ActionRow>("""
        SELECT a.Id, a.ReviewId, a.Text, a.OwnerId, o.Name AS OwnerName, a.SupplierContactId, sc.Name AS ContactName, a.DueOn, a.DoneUtc, db.Name AS DoneByName
        FROM contracts.BusinessReviewAction a JOIN contracts.BusinessReview r ON r.Id = a.ReviewId LEFT JOIN contracts.Person o ON o.Id = a.OwnerId
        LEFT JOIN contracts.SupplierContact sc ON sc.Id = a.SupplierContactId LEFT JOIN contracts.Person db ON db.Id = a.DoneById
        WHERE r.SupplierId = @supplierId AND (@reviewId IS NULL OR r.Id = @reviewId) ORDER BY a.DueOn, a.Id
        """, new { supplierId, reviewId }, tx);

    private static Task<IEnumerable<(int ReviewId, int ContactId, string Name, string? Role)>> ReviewContactsAsync(SqlConnection c, SqlTransaction? tx, int supplierId, int? reviewId = null) =>
        c.QueryAsync<(int, int, string, string?)>("""
            SELECT x.ReviewId, x.ContactId, k.Name, k.Role FROM contracts.BusinessReviewContact x JOIN contracts.SupplierContact k ON k.Id = x.ContactId
            JOIN contracts.BusinessReview r ON r.Id = x.ReviewId WHERE r.SupplierId = @supplierId AND (@reviewId IS NULL OR r.Id = @reviewId) ORDER BY k.Name
            """, new { supplierId, reviewId }, tx);

    /// <summary>
    /// Adds an action to a review, audited. A JBS person is emailed (unless they gave it to themselves); an action for the supplier
    /// isn't emailed to anyone (D-104): the relationship owner sees it on Home.
    /// </summary>
    private static async Task<int> AddActionAsync(SqlConnection c, SqlTransaction tx, AuditLog audit, AppSettings settings, Person me, SupplierInfo s, int reviewId, DateTime heldOn,
        string text, int? ownerId, int? contactId, DateOnly dueOn)
    {
        var due = dueOn.ToDateTime(TimeOnly.MinValue);
        var actionId = await c.ExecuteScalarAsync<int>("""
            INSERT INTO contracts.BusinessReviewAction (ReviewId, Text, OwnerId, SupplierContactId, DueOn) OUTPUT INSERTED.Id VALUES (@reviewId, @text, @ownerId, @contactId, @due)
            """, new { reviewId, text, ownerId, contactId, due }, tx);
        if (ownerId is null)
        {
            var contact = contactId is null ? null : await c.ExecuteScalarAsync<string>("SELECT Name FROM contracts.SupplierContact WHERE Id = @contactId", new { contactId }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "supplier.action-added", "Supplier", s.Id,
                $"{me.Name} gave {(contact is null ? s.Name : $"{contact} at {s.Name}")} an action from the business review: \"{text}\", due {Day(due)}.",
                new { actionId, reviewId, text, forSupplier = true, contactId, dueOn });
            return actionId;
        }
        var owner = await c.QuerySingleAsync<Person>("SELECT * FROM contracts.Person WHERE Id = @ownerId", new { ownerId }, tx);
        await audit.WriteAsync(c, tx, me.AsActor(), "supplier.action-added", "Supplier", s.Id,
            $"{me.Name} gave {owner.Name} an action from the {s.Name} business review: \"{text}\", due {Day(due)}.", new { actionId, reviewId, text, ownerId, dueOn });
        if (owner.Id != me.Id && owner.IsActive)
        {
            var values = new Dictionary<string, string?>
            {
                ["First name"] = LegalEndpoints.First(owner.Name), ["Supplier"] = s.Name, ["Review date"] = Day(heldOn), ["Due date"] = Day(due), ["Action"] = text,
            };
            await EmailOutbox.QueueAsync(c, tx, "review-action", owner.Email, owner.Name,
                await EmailWording.RenderAsync(c, tx, "ReviewAction", values, settings.SiteUrl is null ? null : $"{settings.SiteUrl}/suppliers/{s.Id}?tab=reviews"),
                personId: owner.Id, supplierId: s.Id, dedupeKey: $"review-action:{actionId}");
        }
        return actionId;
    }
}
