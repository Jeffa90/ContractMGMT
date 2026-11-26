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
    // Business unit, site, entity and function can each have several values (D-114), kept in ContractListValue. Read as lists from
    // SelectContract: ids comma-separated, names separated by a unit-separator character (names may contain commas).
    public string? BusinessUnitIdList { get; set; }
    public string? SiteIdList { get; set; }
    public string? EntityIdList { get; set; }
    public string? FunctionIdList { get; set; }
    public string? BusinessUnitNameList { get; set; }
    public string? SiteNameList { get; set; }
    public string? EntityNameList { get; set; }
    public string? FunctionNameList { get; set; }
    public int[] BusinessUnitIds => Ids(BusinessUnitIdList);
    public int[] SiteIds => Ids(SiteIdList);
    public int[] EntityIds => Ids(EntityIdList);
    public int[] FunctionIds => Ids(FunctionIdList);
    public string[] BusinessUnitNames => Names(BusinessUnitNameList);
    public string[] SiteNames => Names(SiteNameList);
    public string[] EntityNames => Names(EntityNameList);
    public string[] FunctionNames => Names(FunctionNameList);
    /// <summary>The values joined for reading ("Dinmore, Riverina"), or null when there are none.</summary>
    public string? BusinessUnitName => Joined(BusinessUnitNames);
    public string? SiteName => Joined(SiteNames);
    public string? EntityName => Joined(EntityNames);
    public string? FunctionName => Joined(FunctionNames);
    public const char NameSeparator = '\u001F';
    private static int[] Ids(string? list) => string.IsNullOrEmpty(list) ? [] : [.. list.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
    private static string[] Names(string? list) => string.IsNullOrEmpty(list) ? [] : list.Split(NameSeparator);
    private static string? Joined(string[] names) => names.Length == 0 ? null : string.Join(", ", names);
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
    public bool AutoRenews { get; set; }
    /// <summary>After its end date it carries on under the same terms until replaced (D-121): past it, "Continuing until replaced", not overdue.</summary>
    public bool ContinuesUntilReplaced { get; set; }
    public int? RenewalTermMonths { get; set; }
    public int? NoticePeriodDays { get; set; }
    public DateTime? ReviewDate { get; set; }
    /// <summary>The review date is this many months before the end date, and moves with it (D-105). Null: a date, or none.</summary>
    public int? ReviewMonthsBeforeEnd { get; set; }
    public bool NeedsLegalReview { get; set; }
    public string Stage { get; set; } = "Draft";
    public DateTime StageSinceUtc { get; set; }
    public bool IsLocked { get; set; }
    public string? WithdrawnReason { get; set; }
    public string? WhoseTurn { get; set; }
    public int? LegalPersonId { get; set; }
    public string? LegalStatus { get; set; }
    public string? LegalNote { get; set; }
    public DateTime? TurnSinceUtc { get; set; }
    public string? LegalOutcome { get; set; }
    public DateTime? LegalClearedUtc { get; set; }
    public int? LegalClearedById { get; set; }
    public DateTime? RegisteredUtc { get; set; }
    public string? LegacyReference { get; set; }
    public bool IsDemo { get; set; }
    public DateTime? ImportedUtc { get; set; }
    public string? ImportedFrom { get; set; }
    public int? RegisteredById { get; set; }
    public bool? RegisteredAsBackup { get; set; }
    // The emergency unlock open now, if any (D-79), and the open Variation, if any.
    public int? UnlockId { get; set; }
    public string? UnlockReason { get; set; }
    public DateTime? UnlockExpiresUtc { get; set; }
    public string? UnlockByName { get; set; }
    public int? OpenVariationId { get; set; }
    public string? OpenVariationStage { get; set; }
    public string? RenewalDecision { get; set; }
    public DateTime? RenewalDecisionUtc { get; set; }
    public int? RenewalDecisionById { get; set; }
    public string? RenewalDecisionNote { get; set; }
    public DateTime? PlannedEndDate { get; set; }
    public DateTime? EndedOn { get; set; }
    public string? EndedReason { get; set; }
    public int? EndedById { get; set; }
    public int? RenewsContractId { get; set; }
    public DateTime? LastReviewedOn { get; set; }
    public int? LastReviewedById { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int CreatedById { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
    // Joined names
    public string? SupplierName { get; set; }
    public string? CategoryName { get; set; }
    public string? ParentCategoryName { get; set; }
    public string? FamilyCategoryName { get; set; }
    // Other categories besides the main one (D-115): ids comma-separated, paths separated by a unit-separator character.
    public string? OtherCategoryIdList { get; set; }
    public string? OtherCategoryPathList { get; set; }
    public int[] OtherCategoryIds => string.IsNullOrEmpty(OtherCategoryIdList) ? [] : [.. OtherCategoryIdList.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
    public string[] OtherCategories => string.IsNullOrEmpty(OtherCategoryPathList) ? [] : OtherCategoryPathList.Split(NameSeparator);
    public string? AgreementTypeName { get; set; }
    public string OwnerName { get; set; } = "";
    public string? BackupOwnerName { get; set; }
    public string CreatedByName { get; set; } = "";
    public string? LegalPersonName { get; set; }
    public string? LegalClearedByName { get; set; }
    public string? RegisteredByName { get; set; }
    public string? RenewalDecisionByName { get; set; }
    public string? EndedByName { get; set; }
    public string? RenewsReference { get; set; }
    public int? RenewedById { get; set; }
    public string? RenewedByReference { get; set; }
    public string? RenewedByStage { get; set; }

    /// <summary>The last day to give notice: end date (or, for an evergreen contract, review date) minus the notice period.</summary>
    public DateTime? NoticeDeadline => NoticePeriodDays is not { } days ? null
        : (IsEvergreen ? ReviewDate : EndDate) is { } anchor ? anchor.AddDays(-days) : null;

    /// <summary>The main category as a path: "Family > Category > Subcategory" (D-115).</summary>
    public string? CategoryDisplay => CategoryName is null ? null : string.Join(" > ", new[] { FamilyCategoryName, ParentCategoryName, CategoryName }.OfType<string>());
}

public sealed record SaveContract(
    string? Title, string? Description, int? SupplierId, int? CategoryId, int? AgreementTypeId, int[]? BusinessUnitIds, int[]? SiteIds,
    int? OwnerId, int? BackupOwnerId, bool? IsConfidential, decimal? Value, string? CurrencyCode, string? SapOutlineAgreement,
    DateOnly? StartDate, DateOnly? EffectiveDate, DateOnly? EndDate, bool? IsEvergreen, int? NoticePeriodDays, DateOnly? ReviewDate,
    bool? NeedsLegalReview, string? RowVersion, Dictionary<string, System.Text.Json.JsonElement>? Fields = null,
    int[]? EntityIds = null, int[]? FunctionIds = null, bool? AutoRenews = null, int? RenewalTermMonths = null, int? ReviewMonthsBeforeEnd = null,
    int[]? OtherCategoryIds = null, bool? ContinuesUntilReplaced = null);

public sealed record WithdrawContract(string? Reason, string? RowVersion);

public static class ContractEndpoints
{
    public static readonly Dictionary<string, string> StageNames = new()
    {
        ["Draft"] = "Draft", ["LegalReview"] = "Legal review", ["Signing"] = "Signing", ["Approval"] = "Approval",
        ["Effective"] = "Effective", ["Ended"] = "Ended", ["Archived"] = "Archived", ["Withdrawn"] = "Withdrawn",
    };

    /// <summary>
    /// A contract's business units, sites, entities and functions (D-114), for alias k: four id lists (BusinessUnitIdList, ...) and
    /// four name lists (BusinessUnitNameList, ...), in the lists' order. Used as OUTER APPLY (...) by everything that reads contracts.
    /// </summary>
    public const string ListValuesSql = """
        SELECT
            STRING_AGG(CASE WHEN li.ListName = N'BusinessUnit' THEN CAST(li.Id AS NVARCHAR(MAX)) END, N',') WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS BusinessUnitIdList,
            STRING_AGG(CASE WHEN li.ListName = N'Site' THEN CAST(li.Id AS NVARCHAR(MAX)) END, N',') WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS SiteIdList,
            STRING_AGG(CASE WHEN li.ListName = N'Entity' THEN CAST(li.Id AS NVARCHAR(MAX)) END, N',') WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS EntityIdList,
            STRING_AGG(CASE WHEN li.ListName = N'Function' THEN CAST(li.Id AS NVARCHAR(MAX)) END, N',') WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS FunctionIdList,
            STRING_AGG(CASE WHEN li.ListName = N'BusinessUnit' THEN CAST(li.Value AS NVARCHAR(MAX)) END, NCHAR(31)) WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS BusinessUnitNameList,
            STRING_AGG(CASE WHEN li.ListName = N'Site' THEN CAST(li.Value AS NVARCHAR(MAX)) END, NCHAR(31)) WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS SiteNameList,
            STRING_AGG(CASE WHEN li.ListName = N'Entity' THEN CAST(li.Value AS NVARCHAR(MAX)) END, NCHAR(31)) WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS EntityNameList,
            STRING_AGG(CASE WHEN li.ListName = N'Function' THEN CAST(li.Value AS NVARCHAR(MAX)) END, NCHAR(31)) WITHIN GROUP (ORDER BY li.SortOrder, li.Value) AS FunctionNameList
        FROM contracts.ContractListValue clv JOIN contracts.ListItem li ON li.Id = clv.ListItemId
        WHERE clv.ContractId = k.Id
        """;

    /// <summary>A contract's other categories (D-115), for alias k: OtherCategoryIdList and OtherCategoryPathList, by path.</summary>
    public const string OtherCategoriesSql = """
        SELECT STRING_AGG(CAST(oc.Id AS NVARCHAR(MAX)), N',') WITHIN GROUP (ORDER BY oc.Path) AS OtherCategoryIdList,
               STRING_AGG(oc.Path, NCHAR(31)) WITHIN GROUP (ORDER BY oc.Path) AS OtherCategoryPathList
        FROM (SELECT o.Id, CAST(CONCAT_WS(N' > ', ocf.Name, ocp.Name, o.Name) AS NVARCHAR(MAX)) AS Path
              FROM contracts.ContractCategory cc JOIN contracts.Category o ON o.Id = cc.CategoryId
              LEFT JOIN contracts.Category ocp ON ocp.Id = o.ParentId LEFT JOIN contracts.Category ocf ON ocf.Id = ocp.ParentId
              WHERE cc.ContractId = k.Id) oc
        """;

    private const string SelectContract = $"""
        SELECT k.*, s.Name AS SupplierName, cat.Name AS CategoryName, parent.Name AS ParentCategoryName, family.Name AS FamilyCategoryName, occ.*,
               at.Value AS AgreementTypeName, lv.*,
               o.Name AS OwnerName, b.Name AS BackupOwnerName, cr.Name AS CreatedByName, lp.Name AS LegalPersonName, lc.Name AS LegalClearedByName, rb.Name AS RegisteredByName,
               ul.UnlockId, ul.UnlockReason, ul.UnlockExpiresUtc, ul.UnlockByName, ovr.OpenVariationId, ovr.OpenVariationStage,
               rd.Name AS RenewalDecisionByName, eb.Name AS EndedByName, ren.Reference AS RenewsReference, rn.RenewedById, rn.RenewedByReference, rn.RenewedByStage
        FROM contracts.Contract k
        LEFT JOIN contracts.Supplier s ON s.Id = k.SupplierId
        LEFT JOIN contracts.Category cat ON cat.Id = k.CategoryId
        LEFT JOIN contracts.Category parent ON parent.Id = cat.ParentId
        LEFT JOIN contracts.Category family ON family.Id = parent.ParentId
        OUTER APPLY ({OtherCategoriesSql}) occ
        LEFT JOIN contracts.ListItem at ON at.Id = k.AgreementTypeId
        OUTER APPLY ({ListValuesSql}) lv
        JOIN contracts.Person o ON o.Id = k.OwnerId
        LEFT JOIN contracts.Person b ON b.Id = k.BackupOwnerId
        JOIN contracts.Person cr ON cr.Id = k.CreatedById
        LEFT JOIN contracts.Person lp ON lp.Id = k.LegalPersonId
        LEFT JOIN contracts.Person lc ON lc.Id = k.LegalClearedById
        LEFT JOIN contracts.Person rb ON rb.Id = k.RegisteredById
        LEFT JOIN contracts.Person rd ON rd.Id = k.RenewalDecisionById
        LEFT JOIN contracts.Person eb ON eb.Id = k.EndedById
        LEFT JOIN contracts.Contract ren ON ren.Id = k.RenewsContractId
        OUTER APPLY (SELECT TOP (1) u.Id AS UnlockId, u.Reason AS UnlockReason, u.ExpiresUtc AS UnlockExpiresUtc, up.Name AS UnlockByName
                     FROM contracts.Unlock u JOIN contracts.Person up ON up.Id = u.ById
                     WHERE u.ContractId = k.Id AND u.ClosedUtc IS NULL AND u.ExpiresUtc > SYSUTCDATETIME() ORDER BY u.Id DESC) ul
        OUTER APPLY (SELECT TOP (1) ov.Id AS OpenVariationId, ov.Stage AS OpenVariationStage FROM contracts.Variation ov WHERE ov.ContractId = k.Id AND ov.Stage IN (N'Draft', N'LegalReview', N'Approval')) ovr
        OUTER APPLY (SELECT TOP (1) r2.Id AS RenewedById, r2.Reference AS RenewedByReference, r2.Stage AS RenewedByStage FROM contracts.Contract r2
                     WHERE r2.RenewsContractId = k.Id AND r2.Stage <> N'Withdrawn' ORDER BY r2.Id DESC) rn
        """;

    public static void MapContractEndpoints(this IEndpointRouteBuilder app)
    {
        var contracts = app.MapGroup("/api/contracts");

        contracts.MapGet("/", async (string? view, string? q, string? tile, HttpRequest request, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            var where = new List<string> { ContractAccess.VisibleSql("k") };
            switch (view)
            {
                case "mine": where.Add("(k.OwnerId = @meId OR k.BackupOwnerId = @meId) AND k.Stage NOT IN (N'Withdrawn', N'Archived')"); break;
                case "drafts": where.Add("k.Stage = N'Draft'"); break;
                case "legal": where.Add("k.Stage = N'LegalReview'"); break;
                case "approval": where.Add("k.Stage = N'Approval'"); break;
                case "closed": where.Add("k.Stage IN (N'Ended', N'Archived', N'Withdrawn')"); break;
                default: where.Add("k.Stage NOT IN (N'Withdrawn', N'Archived')"); break;
            }
            var search = (q ?? "").Trim();
            if (search.Length > 0)
                where.Add("(k.Reference LIKE @like OR k.Title LIKE @like OR s.Name LIKE @like OR k.SapOutlineAgreement LIKE @like OR s.VendorNumber LIKE @like OR k.LegacyReference LIKE @like)");
            await using var c = await db.OpenAsync();
            var definitions = await Fields.LoadAsync(c);
            var parameters = ContractAccess.Params(me!, new { like = $"%{EscapeLike(search)}%", today = HomeEndpoints.Today(clock) });
            // A Home tile's contracts (tile=...), only for a tile this person has.
            if (HomeEndpoints.TileSql(tile, me!) is { } tileSql) where.Add($"({tileSql})");
            // Filters: f.{field key}={value}, for the fields an admin made filterable.
            where.AddRange(Fields.FilterSql(definitions,
                request.Query.Where(x => x.Key.StartsWith("f.", StringComparison.Ordinal)).Select(x => KeyValuePair.Create(x.Key[2..], x.Value.ToString())), parameters));
            var rows = await c.QueryAsync<ContractRow>($"{SelectContract} WHERE {string.Join(" AND ", where)} ORDER BY k.UpdatedUtc DESC", parameters);
            var list = rows.ToList();
            var values = await Fields.ReadAsync(c, null, list.Select(r => r.Id), definitions, f => f.ShowInList && !f.IsHidden);
            return Results.Ok(list.Select(r => ListView(r, values[r.Id].ToDictionary(v => v.Key, v => v.Value.Display))));
        });

        contracts.MapGet("/{id:int}", async (int id, CurrentUser current, Db db, TimeProvider clock) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var row = await c.QuerySingleOrDefaultAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id });
            // A Confidential contract someone can't see looks exactly like one that doesn't exist.
            if (row is null || !ContractAccess.CanSee(me!, row.IsConfidential, row.OwnerId, row.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            var documents = await DocumentEndpoints.ListAsync(c, id);
            var definitions = await Fields.LoadAsync(c);
            var values = (await Fields.ReadAsync(c, null, [id], definitions))[id];
            var activity = await c.QueryAsync("SELECT Id, OccurredUtc, ActorName, Action, Summary FROM contracts.AuditEvent WHERE ContractId = @id ORDER BY Id DESC", new { id });
            return Results.Ok(new
            {
                contract = DetailView(row),
                fieldValues = values.ToDictionary(v => v.Key, v => new { value = v.Value.Value, display = v.Value.Display }),
                legal = LegalEndpoints.View(row),
                conversation = await LegalEndpoints.ConversationAsync(c, id),
                actions = LegalEndpoints.Actions(me!, row, definitions.Any(f => !f.IsBuiltIn && !f.IsHidden && f.AfterEffective == "AnyTime")),
                variations = await VariationEndpoints.ListAsync(c, id),
                approvals = await ApprovalEndpoints.HistoryAsync(c, id),
                renewal = RenewalEndpoints.View(row),
                reminders = await RenewalEndpoints.RemindersAsync(c, id),
                sentBack = await ApprovalEndpoints.SentBackAsync(c, row),
                cannotApproveReason = row.Stage == "Approval" && me!.IsApprover && ApprovalEndpoints.IsInvolved(me, row)
                    ? "You can't approve this one because you raised it, own it or are its backup. Another approver can." : null,
                hasSignedCopy = await c.ExecuteScalarAsync<bool>("SELECT CASE WHEN EXISTS (SELECT 1 FROM contracts.Document WHERE ContractId = @id AND Kind = N'SignedCopy' AND IsRemoved = 0) THEN 1 ELSE 0 END", new { id }),
                missingRequired = row.Stage == "Draft" ? (await LegalEndpoints.MissingRequiredAsync(c, null, row, definitions)).Select(m => m.Label) : [],
                legalPeople = me!.IsLegal ? await c.QueryAsync<IdName>("SELECT Id, Name FROM contracts.Person WHERE IsActive = 1 AND IsLegal = 1 ORDER BY Name") : [],
                undoApprovalUntilUtc = await ApprovalEndpoints.UndoUntilAsync(c, null, me!, row, clock.GetUtcNow().UtcDateTime),
                canEdit = ContractAccess.CanEdit(me!, row),
                canWithdraw = ContractAccess.CanWithdraw(me!, row),
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
            await using var tx = await Db.BeginWriteAsync(c);
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
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await LoadForUpdateAsync(c, tx, id);
            if (before is null || !ContractAccess.CanSee(me!, before.IsConfidential, before.OwnerId, before.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            if (!me!.IsContractOwner && !ContractAccess.IsUnlockedFor(me, before)) return ApiResults.NotAllowed("Only contract owners can change contracts.");
            if (!ContractAccess.CanEdit(me, before))
                return ApiResults.NotAllowed(before.IsLocked
                    ? "This contract is Effective and locked. Changes are made through a Variation."
                    : before is { Stage: "LegalReview", WhoseTurn: "Legal" }
                        ? "It's Legal's turn, so the details can't be changed right now. You can still add documents."
                        : $"You can't change this contract while it's in {StageNames[before.Stage]}.");
            if (Convert.ToBase64String(before.RowVersion) != body.RowVersion)
                return ApiResults.Changed("Someone else changed this contract while you were editing. Your changes weren't saved. Reload to see the latest, then make your changes again.");
            // During an emergency unlock every change carries the unlock and its reason (D-79).
            var via = ContractAccess.IsUnlockedFor(me, before) ? $" (emergency unlock by {before.UnlockByName}: {before.UnlockReason})" : "";
            var (errors2, after) = await UpdateDraftAsync(c, tx, audit, me, before, body, via);
            if (errors2 is not null) return ApiResults.Invalid("Check the highlighted details.", errors2);
            await tx.CommitAsync();
            return Results.Ok(new { id, rowVersion = Convert.ToBase64String(after!.RowVersion) });
        });

        contracts.MapPost("/{id:int}/withdraw", async (int id, WithdrawContract body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await LoadForUpdateAsync(c, tx, id);
            if (before is null || !ContractAccess.CanSee(me!, before.IsConfidential, before.OwnerId, before.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            if (!me!.IsContractOwner) return ApiResults.NotAllowed("Only contract owners can withdraw contracts.");
            if (!ContractAccess.CanWithdraw(me, before)) return ApiResults.NotAllowed($"You can't withdraw this contract while it's in {StageNames[before.Stage]}.");
            if (Convert.ToBase64String(before.RowVersion) != body.RowVersion)
                return ApiResults.Changed("Someone else changed this contract a moment ago. Reload it and try again.");
            var reason = (body.Reason ?? "").Trim();
            if (reason.Length == 0) return ApiResults.Invalid("Say why it's being withdrawn.", new() { ["reason"] = "Add a short reason, for example \"Supplier pulled out\"." });
            if (reason.Length > 1000) return ApiResults.Invalid("Keep the reason shorter.", new() { ["reason"] = "Keep it under 1,000 characters." });
            await c.ExecuteAsync("""
                UPDATE contracts.Contract SET Stage = N'Withdrawn', StageSinceUtc = SYSUTCDATETIME(), IsLocked = 1, WithdrawnReason = @reason,
                    WhoseTurn = NULL, LegalStatus = NULL, LegalNote = NULL, TurnSinceUtc = NULL, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                WHERE Id = @id
                """,
                new { reason, me = me.Id, id }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "contract.withdrawn", "Contract", id, $"{me.Name} withdrew {before.Reference}: {reason}", new { reason }, contractId: id);
            // A withdrawn renewal: the contract it was renewing needs a decision again, or its reminders would stay off and it could lapse unnoticed.
            if (before.RenewsContractId is { } renewed)
            {
                var old = await c.QuerySingleOrDefaultAsync<(string Reference, string? Decision)>(
                    "SELECT Reference, RenewalDecision FROM contracts.Contract WITH (UPDLOCK) WHERE Id = @renewed AND Stage = N'Effective'", new { renewed }, tx);
                var otherRenewal = await c.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM contracts.Contract WHERE RenewsContractId = @renewed AND Id <> @id AND Stage NOT IN (N'Withdrawn', N'Archived')", new { renewed, id }, tx);
                if (old.Decision == "Renew" && otherRenewal == 0)
                {
                    await c.ExecuteAsync("""
                        UPDATE contracts.Contract SET RenewalDecision = NULL, RenewalDecisionNote = NULL, RenewalDecisionUtc = NULL, RenewalDecisionById = NULL,
                            UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                        WHERE Id = @renewed
                        """, new { renewed, me = me.Id }, tx);
                    await audit.WriteAsync(c, tx, me.AsActor(), "contract.renewal-decision-cleared", "Contract", renewed,
                        $"The renewal {before.Reference} was withdrawn, so {old.Reference} needs a renewal decision again. Its reminders are back on.",
                        new { withdrawnRenewal = id }, contractId: renewed);
                }
            }
            await tx.CommitAsync();
            return Results.Ok(new { id });
        });
    }


    /// <summary>
    /// Creates a draft (from the form or an Excel import). Returns the errors by field when the details don't pass. It gets the next
    /// contract number, unless it's given one (an old contract keeping its number from the old system, D-102).
    /// </summary>
    public static async Task<(int Id, string Reference, Dictionary<string, string>? Errors)> CreateDraftAsync(SqlConnection c, SqlTransaction tx, AuditLog audit, Person me, SaveContract body, string via = "", string? reference = null)
    {
        var (input, errors) = await ValidateAsync(c, tx, body with { OwnerId = body.OwnerId ?? me.Id }, before: null);
        var definitions = await Fields.LoadAsync(c, tx);
        var (fieldValues, fieldErrors) = await Fields.ParseAsync(c, tx, body.Fields, definitions);
        if (errors is not null || fieldErrors.Count > 0) return (0, "", (errors ?? []).Concat(fieldErrors).ToDictionary(e => e.Key, e => e.Value));
        reference ??= await ContractNumbers.NextAsync(c, tx);
        var id = await c.ExecuteScalarAsync<int>("""
            INSERT INTO contracts.Contract (Reference, Title, Description, SupplierId, CategoryId, AgreementTypeId, OwnerId, BackupOwnerId,
                IsConfidential, Value, CurrencyCode, SapOutlineAgreement, StartDate, EffectiveDate, EndDate, IsEvergreen, AutoRenews, RenewalTermMonths, ContinuesUntilReplaced, NoticePeriodDays, ReviewDate,
                ReviewMonthsBeforeEnd, NeedsLegalReview, CreatedById, UpdatedById)
            OUTPUT INSERTED.Id
            VALUES (@reference, @Title, @Description, @SupplierId, @CategoryId, @AgreementTypeId, @OwnerId, @BackupOwnerId,
                @IsConfidential, @Value, @CurrencyCode, @SapOutlineAgreement, @StartDate, @EffectiveDate, @EndDate, @IsEvergreen, @AutoRenews, @RenewalTermMonths, @ContinuesUntilReplaced, @NoticePeriodDays, @ReviewDate,
                @ReviewMonthsBeforeEnd, @NeedsLegalReview, @me, @me)
            """, Params(input!, new { reference, me = me.Id }), tx);
        await SaveListValuesAsync(c, tx, id, input!);
        await SaveOtherCategoriesAsync(c, tx, id, input!);
        var saved = await c.QuerySingleAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id }, tx);
        var added = await Fields.SaveAsync(c, tx, id, fieldValues, definitions);
        await audit.WriteAsync(c, tx, me.AsActor(), "contract.created", "Contract", id,
            $"{me.Name} started the draft {reference} {saved.Title}" + (saved.OwnerId != me.Id ? $", owned by {saved.OwnerName}" : "") + $"{via}.",
            new { fields = Snapshot(saved), added }, contractId: id);
        return (id, reference, null);
    }

    /// <summary>Updates a draft the caller may edit (from the form or an Excel import), auditing each changed field in plain words.</summary>
    public static async Task<(Dictionary<string, string>? Errors, ContractRow? After)> UpdateDraftAsync(SqlConnection c, SqlTransaction tx, AuditLog audit, Person me, ContractRow before, SaveContract body, string via = "")
    {
        var id = before.Id;
        var (input, errors) = await ValidateAsync(c, tx, body, before);
        var definitions = await Fields.LoadAsync(c, tx);
        var (fieldValues, fieldErrors) = await Fields.ParseAsync(c, tx, body.Fields, definitions);
        if (errors is not null || fieldErrors.Count > 0) return ((errors ?? []).Concat(fieldErrors).ToDictionary(e => e.Key, e => e.Value), null);
        await c.ExecuteAsync("""
            UPDATE contracts.Contract SET Title = @Title, Description = @Description, SupplierId = @SupplierId, CategoryId = @CategoryId,
                AgreementTypeId = @AgreementTypeId,
                OwnerId = @OwnerId, BackupOwnerId = @BackupOwnerId,
                IsConfidential = @IsConfidential, Value = @Value, CurrencyCode = @CurrencyCode, SapOutlineAgreement = @SapOutlineAgreement,
                StartDate = @StartDate, EffectiveDate = @EffectiveDate, EndDate = @EndDate, IsEvergreen = @IsEvergreen, AutoRenews = @AutoRenews, ContinuesUntilReplaced = @ContinuesUntilReplaced,
                RenewalTermMonths = @RenewalTermMonths, NoticePeriodDays = @NoticePeriodDays, ReviewMonthsBeforeEnd = @ReviewMonthsBeforeEnd, ReviewDate = @ReviewDate, NeedsLegalReview = @NeedsLegalReview, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
            WHERE Id = @id
            """, Params(input!, new { id, me = me.Id }), tx);
        await SaveListValuesAsync(c, tx, id, input!);
        await SaveOtherCategoriesAsync(c, tx, id, input!);
        var after = await c.QuerySingleAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id }, tx);
        var (oldValues, newValues) = (Snapshot(before), Snapshot(after));
        var changed = oldValues.Keys.Where(k => oldValues[k] != newValues[k]).ToList();
        var described = changed.Select(k => newValues[k] is null ? $"cleared {k}" : oldValues[k] is null ? $"set {k} to {newValues[k]}" : $"{k} from {oldValues[k]} to {newValues[k]}").ToList();
        var fieldChanges = await Fields.SaveAsync(c, tx, id, fieldValues, definitions);
        described.AddRange(fieldChanges);
        if (described.Count > 0)
        {
            await audit.WriteAsync(c, tx, me.AsActor(), "contract.changed", "Contract", id, $"{me.Name} changed {string.Join("; ", described)}{via}.",
                new { before = changed.ToDictionary(k => k, k => oldValues[k]), after = changed.ToDictionary(k => k, k => newValues[k]), added = fieldChanges }, contractId: id);
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
        Check("category", before.CategoryId, i.CategoryId); Check("other categories", Key(before.OtherCategoryIds), Key(i.OtherCategoryIds)); Check("agreement type", before.AgreementTypeId, i.AgreementTypeId);
        Check("business unit", Key(before.BusinessUnitIds), Key(i.BusinessUnitIds)); Check("site", Key(before.SiteIds), Key(i.SiteIds));
        Check("entity", Key(before.EntityIds), Key(i.EntityIds)); Check("function", Key(before.FunctionIds), Key(i.FunctionIds));
        Check("owner", before.OwnerId, i.OwnerId);
        Check("backup owner", before.BackupOwnerId, i.BackupOwnerId); Check("Confidential", before.IsConfidential, i.IsConfidential);
        Check("value", before.Value, i.Value); Check("currency", before.CurrencyCode, i.CurrencyCode); Check("SAP outline agreement number", before.SapOutlineAgreement, i.SapOutlineAgreement);
        Check("start date", D(before.StartDate), i.StartDate); Check("effective date", D(before.EffectiveDate), i.EffectiveDate); Check("end date", D(before.EndDate), i.EndDate);
        Check("evergreen", before.IsEvergreen, i.IsEvergreen); Check("renews automatically", before.AutoRenews, i.AutoRenews);
        Check("continues until replaced", before.ContinuesUntilReplaced, i.ContinuesUntilReplaced);
        Check("renewal term", before.RenewalTermMonths, i.RenewalTermMonths); Check("notice period", before.NoticePeriodDays, i.NoticePeriodDays); Check("review date", D(before.ReviewDate), i.ReviewDate); Check("review period", before.ReviewMonthsBeforeEnd, i.ReviewMonthsBeforeEnd);
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
    /// <summary>The contract with its names, for reading (no lock).</summary>
    public static async Task<ContractRow?> LoadAsync(SqlConnection c, int id) =>
        await c.QuerySingleOrDefaultAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id });

    public static async Task<ContractRow?> LoadForUpdateAsync(SqlConnection c, SqlTransaction tx, int id)
    {
        await c.ExecuteAsync("SELECT Id FROM contracts.Contract WITH (UPDLOCK, ROWLOCK) WHERE Id = @id", new { id }, tx);
        return await c.QuerySingleOrDefaultAsync<ContractRow>($"{SelectContract} WHERE k.Id = @id", new { id }, tx);
    }

    private static string EscapeLike(string s) => s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    /// <summary>A set of ids as one comparable value.</summary>
    private static string Key(int[] ids) => string.Join(",", ids.Order());

    /// <summary>Saves the contract's business units, sites, entities and functions (D-114), replacing what it had.</summary>
    private static async Task SaveListValuesAsync(SqlConnection c, SqlTransaction tx, int id, Input i)
    {
        var items = i.BusinessUnitIds.Concat(i.SiteIds).Concat(i.EntityIds).Concat(i.FunctionIds).Distinct().ToList();
        await c.ExecuteAsync("DELETE FROM contracts.ContractListValue WHERE ContractId = @id AND ListItemId NOT IN @items", new { id, items }, tx);
        await c.ExecuteAsync("""
            INSERT INTO contracts.ContractListValue (ContractId, ListItemId)
            SELECT @id, @item WHERE NOT EXISTS (SELECT 1 FROM contracts.ContractListValue WHERE ContractId = @id AND ListItemId = @item)
            """, items.Select(item => new { id, item }), tx);
    }

    /// <summary>Saves the contract's other categories (D-115), replacing what it had.</summary>
    private static async Task SaveOtherCategoriesAsync(SqlConnection c, SqlTransaction tx, int id, Input i)
    {
        var items = i.OtherCategoryIds.ToList();
        await c.ExecuteAsync("DELETE FROM contracts.ContractCategory WHERE ContractId = @id AND CategoryId NOT IN @items", new { id, items }, tx);
        await c.ExecuteAsync("""
            INSERT INTO contracts.ContractCategory (ContractId, CategoryId)
            SELECT @id, @item WHERE NOT EXISTS (SELECT 1 FROM contracts.ContractCategory WHERE ContractId = @id AND CategoryId = @item)
            """, items.Select(item => new { id, item }), tx);
    }

    private static DateOnly? D(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    public static object ListView(ContractRow r, Dictionary<string, string>? fields = null) => new
    {
        Fields = fields ?? [],
        r.Id, r.Reference, r.Title, r.SupplierName, r.Stage, StageName = StageNames[r.Stage], StageSinceUtc = DateTime.SpecifyKind(r.StageSinceUtc, DateTimeKind.Utc),
        r.OwnerId, r.OwnerName, r.BackupOwnerId, r.IsConfidential, r.CategoryDisplay, r.Value, r.CurrencyCode,
        EndDate = D(r.EndDate), r.IsEvergreen, r.ContinuesUntilReplaced, NoticeDeadline = D(r.NoticeDeadline), UpdatedUtc = DateTime.SpecifyKind(r.UpdatedUtc, DateTimeKind.Utc),
        r.WhoseTurn, r.LegalPersonName, TurnSinceUtc = Utc(r.TurnSinceUtc),
    };

    public static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private static object DetailView(ContractRow r) => new
    {
        r.Id, r.Reference, r.Title, r.Description, r.SupplierId, r.SupplierName, r.CategoryId, r.CategoryDisplay, r.OtherCategoryIds, r.OtherCategories, r.AgreementTypeId, r.AgreementTypeName,
        r.BusinessUnitIds, r.BusinessUnitName, r.SiteIds, r.SiteName, r.EntityIds, r.EntityName, r.FunctionIds, r.FunctionName, r.OwnerId, r.OwnerName, r.BackupOwnerId, r.BackupOwnerName, r.IsConfidential,
        r.Value, r.CurrencyCode, r.SapOutlineAgreement, StartDate = D(r.StartDate), EffectiveDate = D(r.EffectiveDate), EndDate = D(r.EndDate), r.IsEvergreen,
        r.AutoRenews, r.RenewalTermMonths, r.ContinuesUntilReplaced, r.NoticePeriodDays, ReviewDate = D(r.ReviewDate), r.ReviewMonthsBeforeEnd, NoticeDeadline = D(r.NoticeDeadline), r.NeedsLegalReview, r.Stage, StageName = StageNames[r.Stage],
        StageSinceUtc = DateTime.SpecifyKind(r.StageSinceUtc, DateTimeKind.Utc), r.IsLocked, r.WithdrawnReason,
        RegisteredUtc = Utc(r.RegisteredUtc), r.RegisteredByName, r.RegisteredAsBackup,
        r.LegacyReference, ImportedUtc = Utc(r.ImportedUtc), r.ImportedFrom, r.IsDemo,
        Unlock = r.UnlockId is null ? null : new { Id = r.UnlockId, Reason = r.UnlockReason, ExpiresUtc = Utc(r.UnlockExpiresUtc), ByName = r.UnlockByName },
        r.OpenVariationId,
        r.RenewsContractId, r.RenewsReference, r.RenewedById, r.RenewedByReference, RenewedByStageName = r.RenewedByStage is { } rs ? StageNames[rs] : null,
        EndedOn = D(r.EndedOn), r.EndedReason, r.EndedByName, LastReviewedOn = D(r.LastReviewedOn),
        CreatedUtc = DateTime.SpecifyKind(r.CreatedUtc, DateTimeKind.Utc), r.CreatedByName, RowVersion = Convert.ToBase64String(r.RowVersion),
    };

    /// <summary>Plain-words values for the Activity history and the audit record (label to display value).</summary>
    private static Dictionary<string, string?> Snapshot(ContractRow r)
    {
        static string? Date(DateTime? d) => d?.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return new()
        {
            ["title"] = r.Title,
            ["description"] = string.IsNullOrEmpty(r.Description) ? null : "(text)",
            ["supplier"] = r.SupplierName,
            ["category"] = r.CategoryDisplay,
            ["other categories"] = r.OtherCategories.Length == 0 ? null : string.Join("; ", r.OtherCategories),
            ["agreement type"] = r.AgreementTypeName,
            ["business unit"] = r.BusinessUnitName,
            ["site"] = r.SiteName,
            ["entity"] = r.EntityName,
            ["function"] = r.FunctionName,
            ["owner"] = r.OwnerName,
            ["backup owner"] = r.BackupOwnerName,
            ["Confidential"] = r.IsConfidential ? "yes" : "no",
            ["value"] = r.Value is { } v ? $"{v.ToString("N2", CultureInfo.GetCultureInfo("en-AU"))} {r.CurrencyCode}" : null,
            ["SAP outline agreement number"] = r.SapOutlineAgreement,
            ["start date"] = Date(r.StartDate),
            ["effective date"] = Date(r.EffectiveDate),
            ["end date"] = Date(r.EndDate),
            ["evergreen"] = r.IsEvergreen ? "yes" : "no",
            ["renews automatically"] = r.AutoRenews ? "yes" : "no",
            ["continues until replaced"] = r.ContinuesUntilReplaced ? "yes" : "no",
            ["renewal term"] = r.RenewalTermMonths is { } t ? Months(t) : null,
            ["notice period"] = r.NoticePeriodDays is { } n ? $"{n} days" : null,
            ["review date"] = Date(r.ReviewDate) is { } rd && r.ReviewMonthsBeforeEnd is { } rm ? $"{rd} ({Months(rm)} before the end date)" : Date(r.ReviewDate),
            ["needs Legal review"] = r.NeedsLegalReview ? "yes" : "no",
        };
    }

    /// <summary>"12 months", "1 month".</summary>
    public static string Months(int n) => n == 1 ? "1 month" : $"{n} months";

    private sealed record Input(string Title, string? Description, int? SupplierId, int? CategoryId, int? AgreementTypeId, int[] BusinessUnitIds, int[] SiteIds,
        int[] EntityIds, int[] FunctionIds, int[] OtherCategoryIds, int OwnerId, int? BackupOwnerId, bool IsConfidential, decimal? Value, string CurrencyCode, string? SapOutlineAgreement,
        DateOnly? StartDate, DateOnly? EffectiveDate, DateOnly? EndDate, bool IsEvergreen, bool AutoRenews, int? RenewalTermMonths, int? NoticePeriodDays,
        DateOnly? ReviewDate, int? ReviewMonthsBeforeEnd, bool NeedsLegalReview, bool ContinuesUntilReplaced);

    private static DynamicParameters Params(Input i, object extra)
    {
        var p = new DynamicParameters(extra);
        p.AddDynamicParams(new
        {
            i.Title, i.Description, i.SupplierId, i.CategoryId, i.AgreementTypeId, i.OwnerId, i.BackupOwnerId,
            i.IsConfidential, i.Value, i.CurrencyCode, i.SapOutlineAgreement, i.IsEvergreen, i.AutoRenews, i.RenewalTermMonths, i.ContinuesUntilReplaced, i.NoticePeriodDays, i.ReviewMonthsBeforeEnd, i.NeedsLegalReview,
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
        // A family alone isn't enough (D-115): the main category is a category or a subcategory.
        if (!f.ContainsKey("categoryId") && b.CategoryId is { } cid && cid != before?.CategoryId)
            await CheckAsync("categoryId", cid, null, "SELECT COUNT(*) FROM contracts.Category WHERE Id = @id AND ParentId IS NOT NULL", "Choose a category in the family, not only the family.");
        // Other categories: the same rules, in use unless the contract already had them; the main one isn't repeated.
        var others = (b.OtherCategoryIds ?? []).Distinct().Where(o => o != b.CategoryId).ToArray();
        if (others.Length > 0)
        {
            var previous = before?.OtherCategoryIds ?? [];
            var ok = await c.QueryAsync<int>("SELECT Id FROM contracts.Category WHERE Id IN @others AND ParentId IS NOT NULL AND (IsActive = 1 OR Id IN @previous)", new { others, previous }, tx);
            if (ok.Count() != others.Length) f["otherCategoryIds"] = "Choose other categories from the list (a category or subcategory, not only a family).";
        }
        await CheckAsync("agreementTypeId", b.AgreementTypeId, before?.AgreementTypeId, "SELECT COUNT(*) FROM contracts.ListItem WHERE Id = @id AND ListName = N'AgreementType' AND IsActive = 1", "Choose an agreement type from the list.");
        // Several values each (D-114). Each must be in its list and in use, unless the contract already had it.
        async Task<int[]> ListAsync(string field, string list, int[]? ids, int[]? previous, string message)
        {
            var wanted = (ids ?? []).Distinct().ToArray();
            if (wanted.Length == 0) return [];
            var found = await c.QueryAsync<int>("SELECT Id FROM contracts.ListItem WHERE ListName = @list AND Id IN @wanted AND (IsActive = 1 OR Id IN @previous)",
                new { list, wanted, previous = previous ?? [] }, tx);
            if (found.Count() != wanted.Length) f[field] = message;
            return wanted;
        }
        var businessUnits = await ListAsync("businessUnitIds", "BusinessUnit", b.BusinessUnitIds, before?.BusinessUnitIds, "Choose business units from the list.");
        var sites = await ListAsync("siteIds", "Site", b.SiteIds, before?.SiteIds, "Choose sites from the list.");
        var entities = await ListAsync("entityIds", "Entity", b.EntityIds, before?.EntityIds, "Choose entities from the list.");
        var functions = await ListAsync("functionIds", "Function", b.FunctionIds, before?.FunctionIds, "Choose functions from the list.");

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
        // Renews automatically: needs an end date to move on, and a term to move it by (D-102). Unticked, there's no term.
        var autoRenews = b.AutoRenews ?? false;
        var term = autoRenews ? b.RenewalTermMonths : null;
        if (autoRenews && evergreen) f["autoRenews"] = "An evergreen contract has no end date to renew. Untick Evergreen or Renews automatically.";
        if (term is < 1 or > 120) f["renewalTermMonths"] = "Use a number of months between 1 and 120.";
        else if (autoRenews && term is null) f["renewalTermMonths"] = "Enter how many months each renewal lasts, for example 12.";
        // Continues until replaced (D-121): keeps its end date and doesn't move it on, so neither evergreen nor renewing automatically.
        var continues = b.ContinuesUntilReplaced ?? false;
        if (continues && evergreen) f["continuesUntilReplaced"] = "An evergreen contract has no end date to carry on past. Untick Evergreen or Continues until replaced.";
        else if (continues && autoRenews) f["continuesUntilReplaced"] = "It can't both renew automatically and carry on until replaced. Untick one of them.";

        // Review: a period before the end date works the date out (and it moves with the end date), or a date as given (D-105).
        var reviewMonths = b.ReviewMonthsBeforeEnd;
        var reviewDate = b.ReviewDate;
        if (reviewMonths is { } rm)
        {
            if (rm is < 1 or > 24) f["reviewDate"] = "Choose 1, 3, 6 or 12 months before the end date, or a date.";
            else if (evergreen) f["reviewDate"] = "An evergreen contract has no end date to count back from. Choose a review date instead.";
            else reviewDate = b.EndDate?.AddMonths(-rm); // no end date yet: worked out once there is one
        }

        if (f.Count > 0) return (null, f);
        return (new Input(title, description, b.SupplierId, b.CategoryId, b.AgreementTypeId, businessUnits, sites, entities, functions, others, ownerId!.Value,
            b.BackupOwnerId, b.IsConfidential ?? false, b.Value, currency, sap, b.StartDate, b.EffectiveDate, b.EndDate, evergreen, autoRenews, term,
            b.NoticePeriodDays, reviewDate, reviewMonths, b.NeedsLegalReview ?? true, continues), null);
    }
}

/// <summary>
/// Contract numbers (D-102): JBSAUS and seven digits, carrying on from Medius's numbers (JBSAUS0000409 after JBSAUS0000408). Old
/// contracts keep their number when imported, and the next new number follows the highest. Numbers given before D-102
/// (JBSAUS-0012) never change.
/// </summary>
public static class ContractNumbers
{
    public static string Format(int n) => $"JBSAUS{n.ToString("0000000", CultureInfo.InvariantCulture)}";

    /// <summary>The next free number (one an import already used is skipped).</summary>
    public static async Task<string> NextAsync(SqlConnection c, SqlTransaction tx)
    {
        while (true)
        {
            var n = await c.ExecuteScalarAsync<int>("UPDATE contracts.Counter SET NextValue = NextValue + 1 OUTPUT DELETED.NextValue WHERE Name = N'ContractNumber'", transaction: tx);
            var reference = Format(n);
            if (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Contract WHERE Reference = @reference", new { reference }, tx) == 0) return reference;
        }
    }

    /// <summary>After an import: new numbers carry on after the highest JBSAUS number in use.</summary>
    public static Task CatchUpAsync(SqlConnection c, SqlTransaction tx) => c.ExecuteAsync("""
        UPDATE contracts.Counter SET NextValue = x.Highest + 1
        FROM (SELECT MAX(CAST(SUBSTRING(Reference, 7, 7) AS INT)) AS Highest FROM contracts.Contract
              WHERE Reference LIKE N'JBSAUS[0-9][0-9][0-9][0-9][0-9][0-9][0-9]') x
        WHERE Name = N'ContractNumber' AND x.Highest IS NOT NULL AND x.Highest + 1 > NextValue
        """, transaction: tx);
}
