using System.Globalization;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

public sealed class ContractRow
{
    public int Id { get; set; }
    public string Reference { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int? SupplierId { get; set; }
    public int? CategoryId { get; set; }
    public int? AgreementTypeId { get; set; }
    public int? BusinessUnitId { get; set; }
    public int? SiteId { get; set; }
    public int OwnerId { get; set; }
    public int? BackupOwnerId { get; set; }
    public bool IsConfidential { get; set; }
    public decimal? Value { get; set; }
    public string CurrencyCode { get; set; } = "AUD";
    public string? SapOutlineAgreement { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsEvergreen { get; set; }
    public int? NoticePeriodDays { get; set; }
    public DateTime? ReviewDate { get; set; }
    public bool NeedsLegalReview { get; set; }
    public string Stage { get; set; } = "Draft";
    public DateTime StageSinceUtc { get; set; }
    public bool IsLocked { get; set; }
    public string? WithdrawnReason { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int CreatedById { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    // Joined names
    public string? SupplierName { get; set; }
    public string? CategoryName { get; set; }
    public string? ParentCategoryName { get; set; }
    public string? AgreementTypeName { get; set; }
    public string? BusinessUnitName { get; set; }
    public string? SiteName { get; set; }
    public string OwnerName { get; set; } = "";
    public string? BackupOwnerName { get; set; }
    public string CreatedByName { get; set; } = "";

    /// <summary>The last day to give notice: end date (or, for an evergreen contract, review date) minus the notice period.</summary>
    public DateTime? NoticeDeadline => NoticePeriodDays is not { } days ? null
        : (IsEvergreen ? ReviewDate : EndDate) is { } anchor ? anchor.AddDays(-days) : null;

    public string? CategoryDisplay => CategoryName is null ? null : ParentCategoryName is null ? CategoryName : $"{ParentCategoryName} > {CategoryName}";
}

public sealed record SaveContract(
    string? Title, string? Description, int? SupplierId, int? CategoryId, int? AgreementTypeId, int? BusinessUnitId, int? SiteId,
    int? OwnerId, int? BackupOwnerId, bool? IsConfidential, decimal? Value, string? CurrencyCode, string? SapOutlineAgreement,
    DateOnly? StartDate, DateOnly? EffectiveDate, DateOnly? EndDate, bool? IsEvergreen, int? NoticePeriodDays, DateOnly? ReviewDate,
    bool? NeedsLegalReview, string? RowVersion);

public sealed record WithdrawContract(string? Reason, string? RowVersion);

public static class ContractEndpoints
{
    public static readonly Dictionary<string, string> StageNames = new()
    {
        ["Draft"] = "Draft", ["LegalReview"] = "Legal review", ["Signing"] = "Signing", ["Approval"] = "Approval",
        ["Effective"] = "Effective", ["Ended"] = "Ended", ["Archived"] = "Archived", ["Withdrawn"] = "Withdrawn",
    };

    private const string SelectContract = """
        SELECT k.*, s.Name AS SupplierName, cat.Name AS CategoryName, parent.Name AS ParentCategoryName,
               at.Value AS AgreementTypeName, bu.Value AS BusinessUnitName, site.Value AS SiteName,
               o.Name AS OwnerName, b.Name AS BackupOwnerName, cr.Name AS CreatedByName
        FROM contracts.Contract k
        LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
        LEFT JOIN contracts.Category cat ON cat.Id = k.CategoryId
        LEFT JOIN contracts.Category parent ON parent.Id = cat.ParentId
        LEFT JOIN contracts.ListItem at ON at.Id = k.AgreementTypeId
        LEFT JOIN contracts.ListItem bu ON bu.Id = k.BusinessUnitId
        LEFT JOIN contracts.ListItem site ON site.Id = k.SiteId
        JOIN contracts.Person o ON o.Id = k.OwnerId
        LEFT JOIN contracts.Person b ON b.Id = k.BackupOwnerId
        JOIN contracts.Person cr ON cr.Id = k.CreatedById
        """;

    public static void MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        var contracts = app.MapGroup("/api/contracts");

        contracts.MapGet("/", async (string? view, string? q, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var where = new List<string> { ContractAccess.VisibleSql("k") };
            switch (view)
            {
                case "mine": where.Add("(k.OwnerId = @meId OR k.BackupOwnerId = @meId) AND k.Stage NOT IN (N'Withdrawn', N'Archived')"); break;
                case "drafts": where.Add("k.Stage = N'Draft'"); break;
                case "closed": where.Add("k.Stage IN (N'Ended', N'Archived', N'Withdrawn')"); break;
                default: where.Add("k.Stage NOT IN (N'Withdrawn', N'Archived')"); break;
            }
            var search = (q ?? "").Trim();
            if (search.Length > 0)
                where.Add("(k.Reference LIKE @like OR k.Title LIKE @like OR s.Name LIKE @like OR k.SapOutlineAgreement LIKE @like OR s.VendorNumber LIKE @like)");
            await using var c = await db.OpenAsync();
            var rows = await c.QueryAsync<ContractRow>($"{SelectContract} WHERE {string.Join(" AND ", where)} ORDER BY k.UpdatedUtc DESC",
                ContractAccess.Params(me!, new { like = $"%{EscapeLike(search)}%" }));
            return Results.Ok(rows.Select(r => ListView(r)));
        });

        contracts.MapGet("/{id:int}", async (int id, CurrentUser current, Db db) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var row = await c.QuerySingleOrDefaultAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id });
            // A Confidential contract someone can't see looks exactly like one that doesn't exist.
            if (row is null || !ContractAccess.CanSee(me!, row.IsConfidential, row.OwnerId, row.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            var documents = await DocumentEndpoints.ListAsync(c, id);
            var activity = await c.QueryAsync("SELECT Id, OccurredUtc, ActorName, Action, Summary FROM contracts.AuditEvent WHERE ContractId = @id ORDER BY Id DESC", new { id });
            return Results.Ok(new
            {
                contract = DetailView(row),
                canEdit = ContractAccess.CanEdit(me!, row.Stage, row.IsLocked),
                canWithdraw = me!.IsContractOwner && row.Stage == "Draft",
                canUpload = DocumentEndpoints.CanUpload(me, row),
                documents,
                activity = activity.Select(a => new { a.Id, OccurredUtc = DateTime.SpecifyKind((DateTime)a.OccurredUtc, DateTimeKind.Utc), a.ActorName, a.Action, a.Summary }),
            });
        });

        contracts.MapPost("/", async (SaveContract body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (!me!.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can add contracts. Ask your admin if you need to.");
            await using var c = await db.OpenAsync();
            await using var tx = c.BeginTransaction();
            var (id, reference, errors) = await CreateDraftAsync(c, tx, audit, me, body);
            if (errors is not null) return ApiResults.Invalid("Check the highlighted details.", errors);
            await tx.CommitAsync();
            return Results.Created($"/api/contracts/{id}", new { id, reference });
        });

        contracts.MapPut("/{id:int}", async (int id, SaveContract body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = c.BeginTransaction();
            var before = await LoadForUpdateAsync(c, tx, id);
            if (before is null || !ContractAccess.CanSee(me!, before.IsConfidential, before.OwnerId, before.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            if (!me!.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can change contracts.");
            if (!ContractAccess.CanEdit(me, before.Stage, before.IsLocked))
                return ApiResults.NotAllowed(before.IsLocked
                    ? "This contract is Effective and locked. Changes are made through a Variation."
                    : $"You can't change this contract while it's in {StageNames[before.Stage]}.");
            if (Convert.ToBase64String(before.RowVersion) != body.RowVersion)
                return ApiResults.Changed("Someone else changed this contract while you were editing. Your changes weren't saved. Reload to see the latest, then make your changes again.");
            var (errors2, after) = await UpdateDraftAsync(c, tx, audit, me, before, body);
            if (errors2 is not null) return ApiResults.Invalid("Check the highlighted details.", errors2);
            await tx.CommitAsync();
            return Results.Ok(new { id, rowVersion = Convert.ToBase64String(after!.RowVersion) });
        });

        contracts.MapPost("/{id:int}/withdraw", async (int id, WithdrawContract body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = c.BeginTransaction();
            var before = await LoadForUpdateAsync(c, tx, id);
            if (before is null || !ContractAccess.CanSee(me!, before.IsConfidential, before.OwnerId, before.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            if (!me!.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can withdraw contracts.");
            if (before.Stage != "Draft") return ApiResults.NotAllowed($"You can't withdraw this contract while it's in {StageNames[before.Stage]}.");
            if (Convert.ToBase64String(before.RowVersion) != body.RowVersion)
                return ApiResults.Changed("Someone else changed this contract a moment ago. Reload it and try again.");
            var reason = (body.Reason ?? "").Trim();
            if (reason.Length == 0) return ApiResults.Invalid("Say why it's being withdrawn.", new() { ["reason"] = "Add a short reason, for example \"Supplier pulled out\"." });
            if (reason.Length > 1000) return ApiResults.Invalid("Keep the reason shorter.", new() { ["reason"] = "Keep it under 1,000 characters." });
            await c.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Withdrawn', StageSinceUtc = SYSUTCDATETIME(), IsLocked = 1, WithdrawnReason = @reason, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me WHERE Id = @id",
                new { reason, me = me.Id, id }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "contract.withdrawn", "Contract", id, $"{me.Name} withdrew {before.Reference}: {reason}", new { reason }, contractId: id);
            await tx.CommitAsync();
            return Results.Ok(new { id });
        });
    }


    /// <summary>Creates a draft (from the form or an Excel import). Returns the errors by field when the details don't pass.</summary>
    public static async Task<(int Id, string Reference, Dictionary<string, string>? Errors)> CreateDraftAsync(SqlConnection c, SqlTransaction tx, AuditLog audit, Person me, SaveContract body, string via = "")
    {
        var (input, errors) = await ValidateAsync(c, tx, body with { OwnerId = body.OwnerId ?? me.Id }, before: null);
        if (errors is not null) return (0, "", errors);
        var number = await c.ExecuteScalarAsync<int>("UPDATE contracts.Counter SET NextValue = NextValue + 1 OUTPUT DELETED.NextValue WHERE Name = N'ContractNumber'", transaction: tx);
        var reference = $"JBSAUS-{number.ToString("0000", CultureInfo.InvariantCulture)}";
        var id = await c.ExecuteScalarAsync<int>("""
            INSERT INTO contracts.Contract (Reference, Title, Description, SupplierId, CategoryId, AgreementTypeId, BusinessUnitId, SiteId, OwnerId, BackupOwnerId,
                IsConfidential, Value, CurrencyCode, SapOutlineAgreement, StartDate, EffectiveDate, EndDate, IsEvergreen, NoticePeriodDays, ReviewDate, NeedsLegalReview,
                CreatedById, UpdatedById)
            OUTPUT INSERTED.Id
            VALUES (@reference, @Title, @Description, @SupplierId, @CategoryId, @AgreementTypeId, @BusinessUnitId, @SiteId, @OwnerId, @BackupOwnerId,
                @IsConfidential, @Value, @CurrencyCode, @SapOutlineAgreement, @StartDate, @EffectiveDate, @EndDate, @IsEvergreen, @NoticePeriodDays, @ReviewDate, @NeedsLegalReview,
                @me, @me)
            """, Params(input!, new { reference, me = me.Id }), tx);
        var saved = await c.QuerySingleAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id }, tx);
        await audit.WriteAsync(c, tx, me.AsActor(), "contract.created", "Contract", id,
            $"{me.Name} started the draft {reference} {saved.Title}" + (saved.OwnerId != me.Id ? $", owned by {saved.OwnerName}" : "") + $"{via}.",
            Snapshot(saved), contractId: id);
        return (id, reference, null);
    }

    /// <summary>Updates a draft the caller may edit (from the form or an Excel import), auditing each changed field in plain words.</summary>
    public static async Task<(Dictionary<string, string>? Errors, ContractRow? After)> UpdateDraftAsync(SqlConnection c, SqlTransaction tx, AuditLog audit, Person me, ContractRow before, SaveContract body, string via = "")
    {
        var id = before.Id;
        var (input, errors) = await ValidateAsync(c, tx, body, before);
        if (errors is not null) return (errors, null);
        await c.ExecuteAsync("""
            UPDATE contracts.Contract SET Title = @Title, Description = @Description, SupplierId = @SupplierId, CategoryId = @CategoryId,
                AgreementTypeId = @AgreementTypeId, BusinessUnitId = @BusinessUnitId, SiteId = @SiteId, OwnerId = @OwnerId, BackupOwnerId = @BackupOwnerId,
                IsConfidential = @IsConfidential, Value = @Value, CurrencyCode = @CurrencyCode, SapOutlineAgreement = @SapOutlineAgreement,
                StartDate = @StartDate, EffectiveDate = @EffectiveDate, EndDate = @EndDate, IsEvergreen = @IsEvergreen, NoticePeriodDays = @NoticePeriodDays,
                ReviewDate = @ReviewDate, NeedsLegalReview = @NeedsLegalReview, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
            WHERE Id = @id
            """, Params(input!, new { id, me = me.Id }), tx);
        var after = await c.QuerySingleAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id }, tx);
        var (oldValues, newValues) = (Snapshot(before), Snapshot(after));
        var changed = oldValues.Keys.Where(k => oldValues[k] != newValues[k]).ToList();
        if (changed.Count > 0)
        {
            var described = changed.Select(k => newValues[k] is null ? $"cleared {k}" : oldValues[k] is null ? $"set {k} to {newValues[k]}" : $"{k} from {oldValues[k]} to {newValues[k]}");
            await audit.WriteAsync(c, tx, me.AsActor(), "contract.changed", "Contract", id, $"{me.Name} changed {string.Join("; ", described)}{via}.",
                new { before = changed.ToDictionary(k => k, k => oldValues[k]), after = changed.ToDictionary(k => k, k => newValues[k]) }, contractId: id);
        }
        return (null, after);
    }

    /// <summary>The fields that would change, by label (for import previews).</summary>
    public static async Task<(List<string> Changed, Dictionary<string, string>? Errors)> PreviewChangesAsync(SqlConnection c, SqlTransaction tx, ContractRow before, SaveContract body)
    {
        var (input, errors) = await ValidateAsync(c, tx, body, before);
        if (errors is not null) return ([], errors);
        var i = input!;
        var changed = new List<string>();
        void Check(string label, object? a, object? b) { if (!Equals(a, b)) changed.Add(label); }
        Check("title", before.Title, i.Title); Check("description", before.Description, i.Description); Check("supplier", before.SupplierId, i.SupplierId);
        Check("category", before.CategoryId, i.CategoryId); Check("agreement type", before.AgreementTypeId, i.AgreementTypeId);
        Check("business unit", before.BusinessUnitId, i.BusinessUnitId); Check("site", before.SiteId, i.SiteId); Check("owner", before.OwnerId, i.OwnerId);
        Check("backup owner", before.BackupOwnerId, i.BackupOwnerId); Check("Confidential", before.IsConfidential, i.IsConfidential);
        Check("value", before.Value, i.Value); Check("currency", before.CurrencyCode, i.CurrencyCode); Check("SAP outline agreement number", before.SapOutlineAgreement, i.SapOutlineAgreement);
        Check("start date", D(before.StartDate), i.StartDate); Check("effective date", D(before.EffectiveDate), i.EffectiveDate); Check("end date", D(before.EndDate), i.EndDate);
        Check("evergreen", before.IsEvergreen, i.IsEvergreen); Check("notice period", before.NoticePeriodDays, i.NoticePeriodDays); Check("review date", D(before.ReviewDate), i.ReviewDate);
        Check("needs Legal review", before.NeedsLegalReview, i.NeedsLegalReview);
        return (changed, null);
    }

    /// <summary>Checks a contract's details without saving (import previews). Null when they pass.</summary>
    public static async Task<Dictionary<string, string>?> CheckAsync(SqlConnection c, SqlTransaction tx, SaveContract body, ContractRow? before) =>
        (await ValidateAsync(c, tx, body, before)).Errors;

    public static async Task<ContractRow?> LoadByReferenceAsync(SqlConnection c, SqlTransaction tx, string reference) =>
        await c.QuerySingleOrDefaultAsync<ContractRow>($"{SelectContract} WHERE k.Reference = @reference", new { reference }, tx);

    public static async Task<IEnumerable<ContractRow>> ListVisibleAsync(SqlConnection c, SqlTransaction tx, Person me) =>
        await c.QueryAsync<ContractRow>($"{SelectContract} WHERE {ContractAccess.VisibleSql("k")} ORDER BY k.Reference", ContractAccess.Params(me), tx);

    /// <summary>Locks the contract row for this transaction (so two people can't change it at once), then reads it with its names.</summary>
    public static async Task<ContractRow?> LoadForUpdateAsync(SqlConnection c, SqlTransaction tx, int id)
    {
        await c.ExecuteAsync("SELECT Id FROM contracts.Contract WITH (UPDLOCK, ROWLOCK) WHERE Id = @id", new { id }, tx);
        return await c.QuerySingleOrDefaultAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id }, tx);
    }

    private static string EscapeLike(string s) => s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    private static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    public static object ListView(ContractRow r) => new
    {
        r.Id, r.Reference, r.Title, r.SupplierName, r.Stage, StageName = StageNames[r.Stage], StageSinceUtc = DateTime.SpecifyKind(r.StageSinceUtc, DateTimeKind.Utc),
        r.OwnerId, r.OwnerName, r.BackupOwnerId, r.IsConfidential, r.CategoryDisplay, r.Value, r.CurrencyCode,
        EndDate = D(r.EndDate), r.IsEvergreen, NoticeDeadline = D(r.NoticeDeadline), UpdatedUtc = DateTime.SpecifyKind(r.UpdatedUtc, DateTimeKind.Utc),
    };

    private static object DetailView(ContractRow r) => new
    {
        r.Id, r.Reference, r.Title, r.Description, r.SupplierId, r.SupplierName, r.CategoryId, r.CategoryDisplay, r.AgreementTypeId, r.AgreementTypeName,
        r.BusinessUnitId, r.BusinessUnitName, r.SiteId, r.SiteName, r.OwnerId, r.OwnerName, r.BackupOwnerId, r.BackupOwnerName, r.IsConfidential,
        r.Value, r.CurrencyCode, r.SapOutlineAgreement, StartDate = D(r.StartDate), EffectiveDate = D(r.EffectiveDate), EndDate = D(r.EndDate), r.IsEvergreen,
        r.NoticePeriodDays, ReviewDate = D(r.ReviewDate), NoticeDeadline = D(r.NoticeDeadline), r.NeedsLegalReview, r.Stage, StageName = StageNames[r.Stage],
        StageSinceUtc = DateTime.SpecifyKind(r.StageSinceUtc, DateTimeKind.Utc), r.IsLocked, r.WithdrawnReason,
        CreatedUtc = DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc), r.CreatedByName, RowVersion = Convert.ToBase64String(r.RowVersion),
    };

    /// <summary>Plain-words values for the Activity history and the audit record (label to display value).</summary>
    private static Dictionary<string, string?> Snapshot(ContractRow r)
    {
        static string? Date(DateTime? d) => d?.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("en-AU"));
        return new()
        {
            ["title"] = r.Title,
            ["description"] = string.IsNullOrEmpty(r.Description) ? null : "(text)",
            ["supplier"] = r.SupplierName,
            ["category"] = r.CategoryDisplay,
            ["agreement type"] = r.AgreementTypeName,
            ["business unit"] = r.BusinessUnitName,
            ["site"] = r.SiteName,
            ["owner"] = r.OwnerName,
            ["backup owner"] = r.BackupOwnerName,
            ["Confidential"] = r.IsConfidential ? "yes" : "no",
            ["value"] = r.Value is { } v ? $"{v.ToString("N2", CultureInfo.GetCultureInfo("en-AU"))} {r.CurrencyCode}" : null,
            ["SAP outline agreement number"] = r.SapOutlineAgreement,
            ["start date"] = Date(r.StartDate),
            ["effective date"] = Date(r.EffectiveDate),
            ["end date"] = Date(r.EndDate),
            ["evergreen"] = r.IsEvergreen ? "yes" : "no",
            ["notice period"] = r.NoticePeriodDays is { } n ? $"{n} days" : null,
            ["review date"] = Date(r.ReviewDate),
            ["needs Legal review"] = r.NeedsLegalReview ? "yes" : "no",
        };
    }

    private sealed record Input(string Title, string? Description, int? SupplierId, int? CategoryId, int? AgreementTypeId, int? BusinessUnitId, int? SiteId,
        int OwnerId, int? BackupOwnerId, bool IsConfidential, decimal? Value, string CurrencyCode, string? SapOutlineAgreement, DateOnly? StartDate,
        DateOnly? EffectiveDate, DateOnly? EndDate, bool IsEvergreen, int? NoticePeriodDays, DateOnly? ReviewDate, bool NeedsLegalReview);

    private static DynamicParameters Params(Input i, object extra)
    {
        var p = new DynamicParameters(extra);
        p.AddDynamicParams(new
        {
            i.Title, i.Description, i.SupplierId, i.CategoryId, i.AgreementTypeId, i.BusinessUnitId, i.SiteId, i.OwnerId, i.BackupOwnerId, i.IsConfidential,
            i.Value, i.CurrencyCode, i.SapOutlineAgreement, i.IsEvergreen, i.NoticePeriodDays, i.NeedsLegalReview,
        });
        p.Add("StartDate", i.StartDate?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
        p.Add("EffectiveDate", i.EffectiveDate?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
        p.Add("EndDate", i.EndDate?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
        p.Add("ReviewDate", i.ReviewDate?.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
        return p;
    }

    /// <summary>
    /// Checks a draft's details. Drafts may be incomplete (only a title is needed); what's required to leave Draft is checked when it is sent on.
    /// Picked items must exist and be in use, unless the contract already had them (a hidden list value stays on contracts that use it).
    /// </summary>
    private static async Task<(Input? Input, Dictionary<string, string>? Errors)> ValidateAsync(SqlConnection c, SqlTransaction tx, SaveContract b, ContractRow? before)
    {
        var f = new Dictionary<string, string>();
        var title = (b.Title ?? "").Trim();
        if (title.Length == 0) f["title"] = "Give the contract a title, for example \"Freight services 2026\".";
        else if (title.Length > 200) f["title"] = "Keep the title under 200 characters.";
        var description = string.IsNullOrWhiteSpace(b.Description) ? null : b.Description.Trim();
        if (description is { Length: > 4000 }) f["description"] = "Keep the description under 4,000 characters.";

        async Task CheckAsync(string field, int? id, int? previous, string sql, string message)
        {
            if (id is null || id == previous) return;
            if (await c.ExecuteScalarAsync<int>(sql, new { id }, tx) == 0) f[field] = message;
        }
        await CheckAsync("supplierId", b.SupplierId, before?.SupplierId, "SELECT COUNT(*) FROM contracts.Supplier WHERE Id = @id AND IsActive = 1", "Choose a supplier from the list.");
        await CheckAsync("categoryId", b.CategoryId, before?.CategoryId, "SELECT COUNT(*) FROM contracts.Category WHERE Id = @id AND IsActive = 1", "Choose a category from the list.");
        await CheckAsync("agreementTypeId", b.AgreementTypeId, before?.AgreementTypeId, "SELECT COUNT(*) FROM contracts.ListItem WHERE Id = @id AND ListName = N'AgreementType' AND IsActive = 1", "Choose an agreement type from the list.");
        await CheckAsync("businessUnitId", b.BusinessUnitId, before?.BusinessUnitId, "SELECT COUNT(*) FROM contracts.ListItem WHERE Id = @id AND ListName = N'BusinessUnit' AND IsActive = 1", "Choose a business unit from the list.");
        await CheckAsync("siteId", b.SiteId, before?.SiteId, "SELECT COUNT(*) FROM contracts.ListItem WHERE Id = @id AND ListName = N'Site' AND IsActive = 1", "Choose a site from the list.");

        var ownerId = b.OwnerId ?? before?.OwnerId;
        if (ownerId is null) f["ownerId"] = "Choose who owns this contract.";
        else await CheckAsync("ownerId", ownerId, before?.OwnerId, "SELECT COUNT(*) FROM contracts.Person WHERE Id = @id AND IsActive = 1 AND IsContractOwner = 1", "The owner must be an active contract owner.");
        if (b.BackupOwnerId is not null)
        {
            if (b.BackupOwnerId == ownerId) f["backupOwnerId"] = "The backup must be someone other than the owner.";
            else await CheckAsync("backupOwnerId", b.BackupOwnerId, before?.BackupOwnerId, "SELECT COUNT(*) FROM contracts.Person WHERE Id = @id AND IsActive = 1 AND IsContractOwner = 1", "The backup must be an active contract owner.");
        }

        var currency = string.IsNullOrWhiteSpace(b.CurrencyCode) ? "AUD" : b.CurrencyCode.Trim().ToUpperInvariant();
        if (currency != before?.CurrencyCode && await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WHERE ListName = N'Currency' AND Value = @currency AND IsActive = 1", new { currency }, tx) == 0)
            f["currencyCode"] = "Choose a currency from the list.";
        if (b.Value is < 0) f["value"] = "The value can't be negative.";
        else if (b.Value is > 999_999_999_999m) f["value"] = "That value is too large. Check the number.";
        var sap = string.IsNullOrWhiteSpace(b.SapOutlineAgreement) ? null : b.SapOutlineAgreement.Trim();
        if (sap is { Length: > 50 }) f["sapOutlineAgreement"] = "Keep the SAP number under 50 characters.";

        var evergreen = b.IsEvergreen ?? false;
        if (evergreen && b.EndDate is not null) f["endDate"] = "An evergreen contract has no end date. Clear the end date, or untick Evergreen.";
        if (b.StartDate is { } s && b.EndDate is { } e && e < s) f["endDate"] = "The end date is before the start date.";
        if (b.NoticePeriodDays is < 0 or > 3650) f["noticePeriodDays"] = "Use a number of days between 0 and 3,650.";

        if (f.Count > 0) return (null, f);
        return (new Input(title, description, b.SupplierId, b.CategoryId, b.AgreementTypeId, b.BusinessUnitId, b.SiteId, ownerId!.Value, b.BackupOwnerId,
            b.IsConfidential ?? false, b.Value, currency, sap, b.StartDate, b.EffectiveDate, b.EndDate, evergreen, b.NoticePeriodDays, b.ReviewDate,
            b.NeedsLegalReview ?? true), null);
    }
}
