using System.Linq.Expressions;
using Ecp.Audit;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

public sealed record ContractListItem(
    Guid Id, Guid OrganisationId, string AgreementReference, string Title, string SupplierName, string CategoryName, string Status,
    string Classification, bool RecordLocked, Guid? PredecessorContractId = null, Guid? SuccessorContractId = null, DateOnly? ReviewDate = null, DateOnly? EndDate = null,
    string? ExtensionType = null, bool CriticalNotice = false, Guid CategoryId = default);

/// <summary>
/// Filters for the register and the operational views. Every one is applied on top of the caller's scopes and classification ceiling, never instead of them.
/// <see cref="StatusIn"/> takes workflow state codes (the lifecycle module supplies them from the workflow definition).
/// </summary>
public sealed record ContractFilter(string? Search = null, string? Status = null, Guid? CategoryId = null, Guid? SupplierId = null, int Page = 1, int PageSize = 25,
    IReadOnlyList<string>? StatusIn = null, DateOnly? EndsOnOrBefore = null, DateOnly? EndsOnOrAfter = null, DateOnly? ReviewOnOrBefore = null, bool? CriticalNotice = null, bool OwnedByMe = false,
    bool? HasSuccessor = null, bool? IsRenewal = null, string? Classification = null, IReadOnlyList<string>? ExcludeExtensionTypes = null, IReadOnlyList<Guid>? Ids = null);

public sealed record ContractDetails(
    Guid Id, Guid OrganisationId, string AgreementReference, string Title, string? Description, Guid SupplierId, string SupplierName, Guid CategoryId,
    string CategoryName, Guid? BusinessUnitId, Guid? SiteId, string Status, bool RecordLocked, string Classification, string? CurrencyCode, decimal? EstimatedValue,
    DateOnly? StartDate, DateOnly? EffectiveDate, DateOnly? ReviewDate, DateOnly? EndDate, DateTime? RegisteredDateUtc, string? RegisteredBy,
    bool IsLegacyLoad, DateTime CreatedUtc, string CreatedBy, bool CanEdit,
    ContractProvenanceInfo? Provenance = null, bool IsArchived = false, string? ArchiveReason = null,
    ContractProtection? Protection = null, ContractReminderInfo? Reminders = null, ContractRenewalInfo? Renewal = null, bool LegalReviewRequired = false, string? ExtensionType = null,
    ContractClassifiers? Classifiers = null);

/// <summary>The classifying facts of a contract beyond the basics, as they are stored. The supplier's own ABN, vendor number and type are shown with the contract but belong to the supplier.</summary>
public sealed record ContractClassifiers(
    Guid? LegalEntityId, string? ContractType, string? AgreementType, string? RiskLevel, string? FunctionCode, string? SapOutlineAgreementNumber, int? NoticePeriodValue, string? NoticePeriodUnit,
    string? SupplierType, string? SupplierTaxIdentifier, string? SupplierVendorNumber);

/// <summary>Why a record cannot be edited, in the words shown on the contract: locked (and by what), content-frozen by its workflow state, or open under an audited emergency unlock.</summary>
public sealed record ContractProtection(bool IsLocked, string? LockReason, string? LockedBy, DateTime? LockedUtc, bool ContentFrozen, string DocumentPolicy, Guid? ActiveUnlockId, DateTime? UnlockExpiresUtc, string? WorkflowRef);

public sealed record ContractReminderInfo(int? LeadDays, int? NoticePeriodDays, bool CriticalNotice);

/// <summary>Renewal links. A reference is shown only when the caller may read the linked agreement.</summary>
public sealed record ContractRenewalInfo(Guid? PredecessorId, string? PredecessorReference, Guid? SuccessorId, string? SuccessorReference);

/// <summary>Where a migrated record came from: shown on the contract so a user can always trace it back to the source system and the batch that loaded it.</summary>
public sealed record ContractProvenanceInfo(
    string? SourceSystem, string? SourceRecordId, string? SourceAgreementReference, string? SourceStatus, DateTime? SourceExtractedUtc, Guid? MigrationBatchId,
    string? RegisteredDateProvenance, string? SourceRegisteredValue);

public sealed record SupplierListItem(Guid Id, Guid OrganisationId, string Name, string? VendorNumber, string? TaxIdentifier, string Status, string? SupplierType = null);

public sealed record CategoryItem(Guid Id, Guid OrganisationId, string Code, string Name, Guid? ParentCategoryId, Guid? DivisionId, bool IsActive, IReadOnlyList<CategoryOwnerItem> Owners);
public sealed record CategoryOwnerItem(Guid AssignmentId, Guid UserId, bool IsPrimary, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc);

/// <summary>Reads and writes contracts on behalf of the signed-in user. Every method authorises on the server; the unsecured <see cref="IContractService"/> is for trusted internal callers only.</summary>
public interface ISecuredContractService
{
    Task<PagedResult<ContractListItem>> ListAsync(ContractFilter filter, CancellationToken ct = default);
    Task<ContractDetails> GetAsync(Guid id, CancellationToken ct = default);
    Task<Guid> CreateDraftAsync(CreateContractRequest request, CancellationToken ct = default);
    Task UpdateDetailsAsync(Guid id, string title, string? description, CancellationToken ct = default);
    /// <summary>
    /// The owner's edit of the contract's own facts (title, description, type, risk, legal entity, business unit, site, value, dates, notice, ...) by their stable field names.
    /// Refused unless the caller may edit this record and it is not locked or frozen; a change of scope (business unit, site) must stay inside the caller's own scope. Returns what actually changed.
    /// </summary>
    Task<IReadOnlyList<FieldChange>> UpdateFieldsAsync(Guid id, IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default);
    /// <summary>Saves the core fields and the configured fields of a record together: either both are saved and audited, or neither. Returns the core changes.</summary>
    Task<IReadOnlyList<FieldChange>> UpdateAllAsync(Guid id, IReadOnlyDictionary<string, string?> changes, CustomFieldInput? configured, CancellationToken ct = default);
    /// <summary>Allocates the next Agreement Reference for the organisation using its prefix and numbering rule (D3).</summary>
    Task<string> PreviewNextReferenceAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Descriptor for access simulation and tests: the facts the evaluator sees for a stored contract.</summary>
    Task<ResourceDescriptor> DescribeAsync(Guid id, CancellationToken ct = default);
    /// <summary>Counts visible contracts by status (for the home page).</summary>
    Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken ct = default);
    /// <summary>Counts the contracts a filter matches among those the caller may read (never more than they could list).</summary>
    Task<int> CountAsync(ContractFilter filter, CancellationToken ct = default);
    /// <summary>
    /// Changes the classification. Raising is open to a record's editors (but never above the caller's own access); lowering needs <c>classification.lower</c> and a reason (at least ten characters).
    /// A locked record can be reclassified only under an open unlock. Documents below the new level are raised with it. Audited with the old and new level.
    /// </summary>
    Task ChangeClassificationAsync(Guid id, string newClassification, string? reason, CancellationToken ct = default);
    /// <summary>Updates the reminder lead time, notice period and Critical Notice flag of an editable record the caller owns.</summary>
    Task UpdateReminderSettingsAsync(Guid id, int? leadDays, int? noticePeriodDays, bool criticalNotice, CancellationToken ct = default);
    /// <summary>Exports the contracts the filter matches as CSV: the same scopes and classification as the list, formula text neutralised. Needs <c>records.export</c>; the export is audited.</summary>
    Task<string> ExportCsvAsync(ContractFilter filter, CancellationToken ct = default);
}

public interface ISecuredSupplierService
{
    Task<IReadOnlyList<SupplierListItem>> ListAsync(string? search = null, CancellationToken ct = default);
    Task<SupplierListItem> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ContractListItem>> ContractsOfAsync(Guid supplierId, CancellationToken ct = default);
    Task<Guid> CreateAsync(CreateSupplierRequest request, CancellationToken ct = default);
    /// <summary>The counterparty's address, for a reader who may see the counterparty. Empty when none is recorded.</summary>
    Task<Ecp.SharedKernel.Localisation.SupplierAddress> GetAddressAsync(Guid supplierId, CancellationToken ct = default);
    /// <summary>Records the address, checked against the organisation's address profile (a required field must be present, an unused field must be empty). Audited.</summary>
    Task SetAddressAsync(Guid supplierId, Ecp.SharedKernel.Localisation.SupplierAddress address, CancellationToken ct = default);
}

public interface ISecuredCategoryService
{
    Task<IReadOnlyList<CategoryItem>> ListAsync(CancellationToken ct = default);
    Task<Guid> CreateAsync(CreateCategoryRequest request, CancellationToken ct = default);
    Task DeactivateAsync(Guid categoryId, CancellationToken ct = default);
    Task<Guid> AssignOwnerAsync(Guid categoryId, Guid userId, bool isPrimary, string? reason, CancellationToken ct = default);
    Task EndOwnershipAsync(Guid assignmentId, string reason, CancellationToken ct = default);
}

/// <summary>Trusted lookups used for idempotent seeding and import (no per-user authorisation; not reachable from request input).</summary>
public interface ISupplierDirectory
{
    Task<Guid?> FindByVendorNumberAsync(Guid organisationId, string vendorNumber, CancellationToken ct = default);
    Task<bool> ContractTitleExistsAsync(Guid organisationId, string title, CancellationToken ct = default);
    Task<bool> AnyContractAsync(CancellationToken ct = default);
}

/// <summary>Trusted Agreement Reference allocation for installation and Demo seeding; request-driven creation allocates through the secured service.</summary>
public interface IAgreementReferenceAllocator
{
    Task<string> AllocateAsync(Guid organisationId, CancellationToken ct = default);
}

/// <summary>Trusted lifecycle application used by Demo seeding and, later, the workflow engine (M4a). Not reachable from request input.</summary>
public interface IContractLifecycleService
{
    Task ApplyStateAsync(Guid contractId, string statusCode, bool locks, CancellationToken ct = default);
}

/// <summary>Trusted category operations used by publication of onboarding configuration and Demo seeding (no per-user authorisation).</summary>
public interface ICategoryAdministration
{
    Task<Guid> UpsertCategoryAsync(Guid organisationId, string code, string name, string? parentCode, CancellationToken ct = default);
    Task<Guid> AssignOwnerTrustedAsync(Guid categoryId, Guid userId, bool isPrimary, DateTime effectiveFromUtc, DateTime? effectiveToUtc, string assignedBy, string? reason, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryItem>> ListAllAsync(CancellationToken ct = default);
    /// <summary>Deactivates a category (never deletes it; contracts may refer to it).</summary>
    Task DeactivateCategoryAsync(Guid categoryId, string reason, CancellationToken ct = default);
    /// <summary>Ends an ownership assignment now.</summary>
    Task EndOwnerTrustedAsync(Guid assignmentId, string reason, CancellationToken ct = default);
}

/// <summary>Loads the reference facts (category tree, ownership, structure) the access layer needs for contracts.</summary>
internal sealed class ContractAccessContext(ContractsDbContext db, IOrganisationStructureService structure, IClock clock, IEnumerable<IPendingApprovalSource> pendingSources)
{
    public async Task<(Dictionary<Guid, Guid?> Parents, Dictionary<Guid, IReadOnlyList<Guid>> Descendants, Dictionary<Guid, IReadOnlyList<Guid>> Ancestry)> CategoryTreeAsync(CancellationToken ct)
    {
        var rows = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentCategoryId }).ToListAsync(ct);
        var parents = rows.ToDictionary(r => r.Id, r => r.ParentCategoryId);
        var ancestry = new Dictionary<Guid, IReadOnlyList<Guid>>();
        foreach (var id in parents.Keys)
        {
            var path = new List<Guid> { id }; var current = parents[id]; var guard = 0;
            while (current is { } p && guard++ < 50) { path.Add(p); current = parents.TryGetValue(p, out var next) ? next : null; }
            ancestry[id] = path;
        }
        var descendants = parents.Keys.ToDictionary(id => id, id => (IReadOnlyList<Guid>)ancestry.Where(kv => kv.Value.Contains(id)).Select(kv => kv.Key).ToList());
        return (parents, descendants, ancestry);
    }

    public async Task<ContractAccessFilter.Context> ListContextAsync(Guid? userId, CancellationToken ct)
    {
        var (_, descendants, _) = await CategoryTreeAsync(ct);
        var now = clock.UtcNow;
        var owned = userId is null ? new HashSet<Guid>() : (await db.CategoryAssignments.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct))
            .Where(a => a.IsActiveAt(now)).Select(a => a.CategoryId).ToHashSet();
        var divisionOf = new Dictionary<Guid, Guid?>();
        foreach (var org in await structure.ListOrganisationsAsync(ct))
            foreach (var bu in (await structure.GetAsync(org.Id, ct)).BusinessUnits) divisionOf[bu.Id] = bu.DivisionId;
        var pendingByGroup = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var source in pendingSources) // sequential: all module contexts share one connection
            foreach (var (contractId, groups) in await source.PendingAsync(null, ct))
                foreach (var group in groups)
                {
                    if (!pendingByGroup.TryGetValue(group, out var list)) pendingByGroup[group] = list = [];
                    list.Add(contractId);
                }
        return new ContractAccessFilter.Context(descendants, owned, divisionOf, pendingByGroup.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Guid>)kv.Value.Distinct().ToList()));
    }

    public async Task<ResourceDescriptor> DescribeAsync(Contract c, CancellationToken ct)
    {
        var (_, _, ancestry) = await CategoryTreeAsync(ct);
        var path = ancestry.TryGetValue(c.CategoryId, out var p) ? p : [c.CategoryId];
        var now = clock.UtcNow;
        var owners = (await db.CategoryAssignments.AsNoTracking().Where(a => path.Contains(a.CategoryId)).ToListAsync(ct))
            .Where(a => a.IsActiveAt(now)).Select(a => a.UserId).ToHashSet();
        Guid? division = null;
        if (c.BusinessUnitId is { } bu) division = (await structure.GetAsync(c.OrganisationId, ct)).DivisionOfBusinessUnit(bu);
        // An approver reaches a contract only while a request is waiting for their group.
        HashSet<string>? waiting = null;
        foreach (var source in pendingSources)
            foreach (var groups in (await source.PendingAsync([c.Id], ct)).Values) (waiting ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(groups);
        return new ResourceDescriptor("Contract", c.TenantId, c.OrganisationId, c.BusinessUnitId, division, c.SiteId, c.CategoryId, path, c.Classification,
            c.RecordLocked, owners, PendingApprovalGroupKeys: waiting, Id: c.Id.ToString());
    }
}

internal sealed class SecuredContractService(
    ContractsDbContext db, IAccessService access, ContractAccessContext context, IContractService contracts, IUnitOfWork uow,
    ReferenceAllocator allocator, IOrganisationStructureService structure, ITenantContext tenant, IClock clock, ICutoverFreeze freeze,
    IAuditWriter audit, Ecp.Documents.IDocumentClassification documentClassification, CurrentUser currentUser, IContractLifecyclePort port,
    IContractCustomFields customFields, IEnumerable<IContractCreationPolicy> creationPolicies, ICoreFieldLists coreLists, TaxIdentifierGate taxGate) : ISecuredContractService
{
    private async Task<IQueryable<Contract>> FilteredAsync(ContractFilter f, CancellationToken ct)
    {
        var predicate = await VisiblePredicateAsync(ct);
        var query = db.Contracts.AsNoTracking().Where(predicate).Where(c => !c.IsArchived);
        if (!string.IsNullOrWhiteSpace(f.Status)) query = query.Where(c => c.Status == f.Status);
        if (f.StatusIn is { Count: > 0 } states) { var set = states.ToArray(); query = query.Where(c => set.Contains(c.Status)); }
        if (f.Ids is { } ids) { var set = ids.ToArray(); query = query.Where(c => set.Contains(c.Id)); }
        if (f.CategoryId is { } cat) query = query.Where(c => c.CategoryId == cat);
        if (f.SupplierId is { } sup) query = query.Where(c => c.SupplierId == sup);
        if (!string.IsNullOrWhiteSpace(f.Classification)) { var level = f.Classification.Trim(); query = query.Where(c => c.Classification == level); }
        if (f.EndsOnOrBefore is { } endBefore) query = query.Where(c => c.EndDate != null && c.EndDate <= endBefore);
        if (f.EndsOnOrAfter is { } endAfter) query = query.Where(c => c.EndDate != null && c.EndDate >= endAfter);
        if (f.ReviewOnOrBefore is { } reviewBefore) query = query.Where(c => c.ReviewDate != null && c.ReviewDate <= reviewBefore);
        if (f.CriticalNotice is { } critical) query = query.Where(c => c.CriticalNotice == critical);
        if (f.HasSuccessor is { } hasSuccessor) query = hasSuccessor ? query.Where(c => c.SuccessorContractId != null) : query.Where(c => c.SuccessorContractId == null);
        if (f.IsRenewal is { } isRenewal) query = isRenewal ? query.Where(c => c.PredecessorContractId != null) : query.Where(c => c.PredecessorContractId == null);
        if (f.ExcludeExtensionTypes is { Count: > 0 } excluded) { var set = excluded.ToArray(); query = query.Where(c => c.ExtensionType == null || !set.Contains(c.ExtensionType)); }
        if (f.OwnedByMe)
        {
            var ctx = await context.ListContextAsync(access.CurrentUserId, ct);
            var mine = ctx.OwnedCategoryIds.SelectMany(id => ctx.DescendantsOf.TryGetValue(id, out var d) ? d : [id]).Distinct().ToArray();
            query = query.Where(c => mine.Contains(c.CategoryId));
        }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            query = query.Where(c => c.AgreementReference.Contains(term.ToUpper()) || c.ContractTitle.Contains(term));
        }
        return query;
    }

    public async Task<PagedResult<ContractListItem>> ListAsync(ContractFilter f, CancellationToken ct = default)
    {
        var query = await FilteredAsync(f, ct);
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, f.Page); var size = Math.Clamp(f.PageSize, 1, 200);
        var items = await (from c in query
                           join s in db.Suppliers.AsNoTracking() on new { c.TenantId, Id = c.SupplierId } equals new { s.TenantId, s.Id }
                           join g in db.Categories.AsNoTracking() on new { c.TenantId, Id = c.CategoryId } equals new { g.TenantId, g.Id }
                           orderby c.AgreementReference
                           select new ContractListItem(c.Id, c.OrganisationId, c.AgreementReference, c.ContractTitle, s.Name, g.Name, c.Status, c.Classification, c.RecordLocked,
                               c.PredecessorContractId, c.SuccessorContractId, c.ReviewDate, c.EndDate, c.ExtensionType, c.CriticalNotice, c.CategoryId))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ContractListItem>(items, total, page, size);
    }

    public async Task<int> CountAsync(ContractFilter f, CancellationToken ct = default) => await (await FilteredAsync(f, ct)).CountAsync(ct);

    public async Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken ct = default)
    {
        var predicate = await VisiblePredicateAsync(ct);
        return await db.Contracts.AsNoTracking().Where(predicate).Where(c => !c.IsArchived).GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
    }

    private async Task<Expression<Func<Contract, bool>>> VisiblePredicateAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("contracts.read", ct);
        if (grants.Count == 0) return c => false;
        return ContractAccessFilter.Build(grants, await context.ListContextAsync(access.CurrentUserId, ct));
    }

    public async Task<ContractDetails> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) throw new ResourceNotFoundException(); // absent, or another tenant's (filtered out): the answer is the same 404
        var descriptor = await context.DescribeAsync(c, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        var supplierRow = await db.Suppliers.AsNoTracking().Where(s => s.Id == c.SupplierId).Select(s => new { s.Name, s.SupplierType, s.TaxIdentifier, s.VendorNumber }).FirstOrDefaultAsync(ct);
        var supplier = supplierRow?.Name ?? "";
        var taxVisible = (await taxGate.ReadableOrganisationsAsync(await access.GrantsAsync("contracts.read", ct), [c.OrganisationId], ct)).Contains(c.OrganisationId);
        var category = await db.Categories.AsNoTracking().Where(s => s.Id == c.CategoryId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "";
        var canEdit = !c.ContentFrozen && (await access.CheckSilentAsync("contracts.edit_owned", descriptor, ct)).Allowed;
        // A renewal link names the other agreement only when the caller could open it themselves.
        var linked = new[] { c.PredecessorContractId, c.SuccessorContractId }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var visibleLinks = linked.Length == 0 ? new Dictionary<Guid, string>()
            : await db.Contracts.AsNoTracking().Where(await VisiblePredicateAsync(ct)).Where(x => linked.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.AgreementReference, ct);
        string? Ref(Guid? link) => link is { } l && visibleLinks.TryGetValue(l, out var r) ? r : null;
        var now = clock.UtcNow;
        return new ContractDetails(c.Id, c.OrganisationId, c.AgreementReference, c.ContractTitle, c.Description, c.SupplierId, supplier, c.CategoryId, category,
            c.BusinessUnitId, c.SiteId, c.Status, c.RecordLocked, c.Classification, c.CurrencyCode, c.EstimatedValue, c.StartDate, c.EffectiveDate, c.ReviewDate,
            c.EndDate, c.RegisteredDateUtc, c.RegisteredBy, c.IsLegacyLoad, c.CreatedUtc, c.CreatedBy, canEdit,
            c.SourceRecordId is null && c.MigrationBatchId is null ? null : new ContractProvenanceInfo(c.SourceSystem, c.SourceRecordId, c.SourceAgreementReference, c.SourceStatus,
                c.SourceExtractedUtc, c.MigrationBatchId, c.RegisteredDateProvenance?.ToString(), c.SourceRegisteredValue),
            c.IsArchived, c.ArchiveReason,
            new ContractProtection(c.RecordLocked, c.LockReason, c.LockedBy, c.LockedUtc, c.ContentFrozen, c.DocumentPolicy, c.IsUnlockedAt(now) ? c.ActiveUnlockId : null, c.IsUnlockedAt(now) ? c.UnlockExpiresUtc : null, c.WorkflowRef),
            new ContractReminderInfo(c.ReminderLeadDays, c.NoticePeriodDays, c.CriticalNotice),
            new ContractRenewalInfo(c.PredecessorContractId, Ref(c.PredecessorContractId), c.SuccessorContractId, Ref(c.SuccessorContractId)), c.LegalReviewRequired, c.ExtensionType,
            new ContractClassifiers(c.LegalEntityId, c.ContractType, c.AgreementType, c.RiskLevel, c.FunctionCode, c.SapOutlineAgreementNumber, c.NoticePeriodValue, c.NoticePeriodUnit,
                supplierRow?.SupplierType, taxVisible ? supplierRow?.TaxIdentifier : null, supplierRow?.VendorNumber));
    }

    public async Task ChangeClassificationAsync(Guid id, string newClassification, string? reason, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(c, ct);
        await access.RequireAsync("contracts.read", descriptor, ct); // an unreadable record does not exist for the caller
        var scale = await access.ClassificationScaleAsync(ct);
        var target = scale.Canonical(newClassification) ?? throw new DomainException("invalid_classification", $"Classification '{newClassification}' is not recognised.");
        var current = scale.Canonical(c.Classification) ?? c.Classification;
        if (string.Equals(target, current, StringComparison.Ordinal)) return;
        var lowering = scale.Rank(target) < scale.Rank(current);

        // The caller cannot classify a record beyond their own access: they would be hiding it from themselves, or handing out a level they cannot hold.
        if (!(await access.CheckSilentAsync("contracts.read", descriptor with { Classification = target }, ct)).Allowed)
            throw new ForbiddenException("You cannot set a classification above your own access.");

        string why = (reason ?? "").Trim();
        if (lowering)
        {
            await access.RequireAsync("classification.lower", descriptor, ct);
            if (why.Length < 10) throw new DomainException("reason_required", "Lowering a classification needs a reason (at least ten characters).");
        }
        else
        {
            // Raising is open to those who may edit the record, and to those who may lower classifications (administrators).
            var edit = await access.CheckSilentAsync("contracts.edit_owned", descriptor, ct);
            if (!edit.Allowed && !(await access.CheckSilentAsync("classification.lower", descriptor, ct)).Allowed) await access.RequireAsync("contracts.edit_owned", descriptor, ct);
        }
        // A locked record is reclassified only under an open unlock ("Admin unlock only"); the unlock service passes through here with its own authority.
        var now = clock.UtcNow;
        if (c.RecordLocked && !c.IsUnlockedAt(now)) throw new DomainException("record_locked", "This record is locked. Reclassifying it needs an audited emergency unlock.");

        var actorLabel = currentUser.UserId?.ToString() ?? "system";
        await uow.ExecuteAsync(async cx =>
        {
            var tracked = await db.Contracts.FirstAsync(x => x.Id == id, cx);
            var previous = tracked.SetClassification(target, actorLabel, now);
            try { await db.SaveChangesAsync(cx); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The record was changed by someone else. Reload it and try again."); }
            // The event is classified at the higher level, so it is as hard to see as the more sensitive of the two states of the record.
            var eventLevel = scale.Rank(target) >= scale.Rank(previous) ? target : previous;
            audit.Record(new AuditEntry("contract.classification_changed", nameof(Contract), id.ToString(), OrganisationId: tracked.OrganisationId, Reason: lowering ? why : (why.Length == 0 ? null : why),
                Classification: eventLevel, OldValuesJson: AuditJson.Of(new { classification = previous }), NewValuesJson: AuditJson.Of(new { classification = target, direction = lowering ? "lowered" : "raised" })));
            if (!lowering)
            {
                var below = scale.Levels.Where(l => scale.Rank(l) < scale.Rank(target)).ToArray();
                await documentClassification.RaiseContractDocumentsAsync(id, target, below, $"Contract raised to {target}", cx);
            }
        }, ct);
    }

    public async Task UpdateReminderSettingsAsync(Guid id, int? leadDays, int? noticePeriodDays, bool criticalNotice, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(c, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        await access.RequireAsync("contracts.edit_owned", descriptor, ct);
        if (c.ContentFrozen) throw new DomainException("content_frozen", "The [[contract|lower]] is in a stage where its content is frozen.");
        await port.SetReminderSettingsAsync(id, leadDays, noticePeriodDays, criticalNotice, currentUser.UserId?.ToString() ?? "system", ct);
    }

    public async Task<string> ExportCsvAsync(ContractFilter filter, CancellationToken ct = default)
    {
        // Export honours exactly the list's scopes and classification, and additionally needs the export permission within them.
        var grants = await access.GrantsAsync("records.export", ct);
        if (grants.Count == 0) throw new ForbiddenException("You cannot export records.");
        var visible = await FilteredAsync(filter with { Page = 1 }, ct);
        var exportable = ContractAccessFilter.Build(grants, await context.ListContextAsync(access.CurrentUserId, ct));
        var rows = await (from c in visible.Where(exportable)
                          join s in db.Suppliers.AsNoTracking() on new { c.TenantId, Id = c.SupplierId } equals new { s.TenantId, s.Id }
                          join g in db.Categories.AsNoTracking() on new { c.TenantId, Id = c.CategoryId } equals new { g.TenantId, g.Id }
                          orderby c.AgreementReference
                          select new { c.AgreementReference, c.ContractTitle, Supplier = s.Name, Category = g.Name, c.Status, c.Classification, c.CurrencyCode, c.EstimatedValue, c.StartDate, c.EffectiveDate, c.ReviewDate, c.EndDate })
            .Take(10000).ToListAsync(ct);
        var sb = new System.Text.StringBuilder();
        sb.Append("AgreementReference,ContractTitle,Supplier,Category,Status,Classification,Currency,EstimatedValue,StartDate,EffectiveDate,ReviewDate,EndDate\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(',', new[] { r.AgreementReference, r.ContractTitle, r.Supplier, r.Category, r.Status, r.Classification, r.CurrencyCode ?? "",
                r.EstimatedValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", r.StartDate?.ToString("yyyy-MM-dd") ?? "", r.EffectiveDate?.ToString("yyyy-MM-dd") ?? "",
                r.ReviewDate?.ToString("yyyy-MM-dd") ?? "", r.EndDate?.ToString("yyyy-MM-dd") ?? "" }.Select(CsvCell))).Append("\r\n");
        await uow.ExecuteAsync(cx => { audit.Record(new AuditEntry("contracts.exported", "ContractExport", Guid.NewGuid().ToString(), NewValuesJson: AuditJson.Of(new { rows = rows.Count }))); return Task.CompletedTask; }, ct);
        return sb.ToString();
    }

    /// <summary>A CSV cell with spreadsheet formula text neutralised (a leading =, +, -, @, tab or carriage return is prefixed) and quoting where needed.</summary>
    internal static string CsvCell(string value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public async Task<ResourceDescriptor> DescribeAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ResourceNotFoundException();
        return await context.DescribeAsync(c, ct);
    }

    public async Task<IReadOnlyList<FieldChange>> UpdateFieldsAsync(Guid id, IReadOnlyDictionary<string, string?> changes, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(c, ct);
        await access.RequireAsync("contracts.edit_owned", descriptor, ct);
        var org = await structure.GetAsync(c.OrganisationId, ct);
        if (changes.TryGetValue("businessUnitId", out var buRaw) && Guid.TryParse(buRaw, out var bu) && org.BusinessUnits.All(b => b.Id != bu)) throw new DomainException("business_unit_not_found", "The [[business.unit|lower]] was not found in this [[organisation|lower]].");
        if (changes.TryGetValue("siteId", out var siteRaw) && Guid.TryParse(siteRaw, out var site) && org.Sites.All(x => x.Id != site)) throw new DomainException("site_not_found", "The [[site|lower]] was not found in this [[organisation|lower]].");
        if (changes.TryGetValue("legalEntityId", out var leRaw) && Guid.TryParse(leRaw, out var le) && org.LegalEntities.All(x => x.Id != le)) throw new DomainException("legal_entity_not_found", "The [[legal.entity|lower]] was not found in this [[organisation|lower]].");
        // Moving a contract to another business unit or site must not take it out of the caller's own reach.
        if (changes.ContainsKey("businessUnitId") || changes.ContainsKey("siteId"))
        {
            Guid? newBu = changes.TryGetValue("businessUnitId", out var b) ? (Guid.TryParse(b, out var g) ? g : null) : c.BusinessUnitId;
            Guid? newSite = changes.TryGetValue("siteId", out var st) ? (Guid.TryParse(st, out var g2) ? g2 : null) : c.SiteId;
            await access.RequireAsync("contracts.edit_owned", descriptor with { BusinessUnitId = newBu, SiteId = newSite, DivisionId = org.DivisionOfBusinessUnit(newBu) }, ct);
        }
        var actor = currentUser.UserId?.ToString() ?? "system";
        var canonical = new Dictionary<string, string?>(changes, StringComparer.Ordinal);
        foreach (var (key, current) in new[] { ("contractType", c.ContractType), ("agreementType", c.AgreementType), ("riskLevel", c.RiskLevel), ("functionCode", c.FunctionCode) })
            if (canonical.TryGetValue(key, out var raw)) canonical[key] = await coreLists.CheckAsync(c.OrganisationId, key, raw, current, ct);
        changes = canonical;
        return await uow.ExecuteAsync(async cx =>
        {
            var tracked = await db.Contracts.FirstAsync(x => x.Id == id, cx);
            var applied = tracked.EditFields(changes, actor, clock.UtcNow);
            try { await db.SaveChangesAsync(cx); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The record was changed by someone else. Reload it and try again."); }
            if (applied.Count > 0)
                audit.Record(new AuditEntry("contract.updated", nameof(Contract), id.ToString(), OrganisationId: tracked.OrganisationId, Classification: tracked.Classification,
                    OldValuesJson: AuditJson.Of(applied.ToDictionary(f => f.Field, f => f.Before)), NewValuesJson: AuditJson.Of(applied.ToDictionary(f => f.Field, f => f.After))));
            return applied;
        }, ct);
    }

    public async Task<IReadOnlyList<FieldChange>> UpdateAllAsync(Guid id, IReadOnlyDictionary<string, string?> changes, CustomFieldInput? configured, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var core = await UpdateFieldsAsync(id, changes, c);
            if (configured is { Count: > 0 }) await customFields.SaveAsync(id, configured, c);
            return core;
        }, ct);

    public async Task<string> PreviewNextReferenceAsync(Guid organisationId, CancellationToken ct = default)
    {
        // Any user who may create contracts in this organisation may see the next reference; the category-specific check happens on create.
        var grants = await access.GrantsAsync("contracts.create", ct);
        if (!grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(organisationId))) throw new ForbiddenException("You cannot create contracts in this organisation.");
        return await allocator.AllocateAsync(organisationId, consume: false, ct);
    }

    public async Task<Guid> CreateDraftAsync(CreateContractRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var scale = await access.ClassificationScaleAsync(ct);
        if (!scale.IsKnown(r.Classification)) throw new DomainException("invalid_classification", $"Classification '{r.Classification}' is not recognised.");
        var org = await structure.GetAsync(r.OrganisationId, ct); // 422 if the organisation is not in this tenant
        // Cutover freeze (MIGRATION_SUPPLEMENT 5.6): people cannot create contracts in an organisation while its target freeze is in force; migration batches do not come through here.
        if (await freeze.CreationBlockedAsync(r.OrganisationId, ct) is { } frozen) throw new DomainException("cutover_freeze", frozen);
        if (r.BusinessUnitId is { } bu && org.BusinessUnits.All(b => b.Id != bu)) throw new DomainException("business_unit_not_found", "The [[business.unit|lower]] was not found in this [[organisation|lower]].");
        if (r.SiteId is { } site && org.Sites.All(s => s.Id != site)) throw new DomainException("site_not_found", "The [[site|lower]] was not found in this [[organisation|lower]].");
        if (r.LegalEntityId is { } le && org.LegalEntities.All(l => l.Id != le)) throw new DomainException("legal_entity_not_found", "The [[legal.entity|lower]] was not found in this [[organisation|lower]].");
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CategoryId && c.OrganisationId == r.OrganisationId, ct)
            ?? throw new DomainException("category_not_found", "The [[category|lower]] was not found.");

        // Authorise against the facts of the contract that would be created, never against what the caller claims about themselves.
        var (_, _, ancestry) = await context.CategoryTreeAsync(ct);
        var path = ancestry.TryGetValue(category.Id, out var p) ? p : [category.Id];
        var now = clock.UtcNow;
        var owners = (await db.CategoryAssignments.AsNoTracking().Where(a => path.Contains(a.CategoryId)).ToListAsync(ct)).Where(a => a.IsActiveAt(now)).Select(a => a.UserId).ToHashSet();
        var descriptor = new ResourceDescriptor("Contract", tenantId, r.OrganisationId, r.BusinessUnitId, org.DivisionOfBusinessUnit(r.BusinessUnitId), r.SiteId,
            category.Id, path, r.Classification, false, owners, Id: "new");
        await access.RequireAsync("contracts.create", descriptor, ct);
        // Configured fields are checked as this person (roles, requiredness, types) before a reference number is used or anything is written.
        var initialStatus = creationPolicies.FirstOrDefault() is { } policy ? (await policy.ResolveAsync(r.OrganisationId, ct)).InitialStatus : "";
        await customFields.ValidateForNewAsync(r.OrganisationId, r.ContractType, initialStatus, r.CustomFieldValues(), ct);

        r = r with
        {
            ContractType = await coreLists.CheckAsync(r.OrganisationId, "contractType", r.ContractType, null, ct), AgreementType = await coreLists.CheckAsync(r.OrganisationId, "agreementType", r.AgreementType, null, ct),
            RiskLevel = await coreLists.CheckAsync(r.OrganisationId, "riskLevel", r.RiskLevel, null, ct), FunctionCode = await coreLists.CheckAsync(r.OrganisationId, "functionCode", r.FunctionCode, null, ct)
        };
        var reference = r.AgreementReference;
        if (string.IsNullOrWhiteSpace(reference))
        {
            // Allocate and create in one unit of work so a failed create does not burn a number.
            return await uow.ExecuteAsync(async c =>
            {
                var allocated = await allocator.AllocateAsync(r.OrganisationId, consume: true, c);
                return await contracts.CreateDraftAsync(r with { AgreementReference = allocated }, c);
            }, ct);
        }
        return await contracts.CreateDraftAsync(r, ct);
    }

    public async Task UpdateDetailsAsync(Guid id, string title, string? description, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync("contracts.edit_owned", await context.DescribeAsync(c, ct), ct);
        await contracts.UpdateDetailsAsync(id, title, description, ct);
    }
}

internal sealed class SecuredSupplierService(
    ContractsDbContext db, IAccessService access, ContractAccessContext context, ISupplierService suppliers, ISecuredContractService contracts, ITenantContext tenant, TaxIdentifierGate taxGate, IUnitOfWork uow, IAuditWriter audit,
    Ecp.Configuration.IRegionalSettingsResolver regional) : ISecuredSupplierService
{
    public async Task<IReadOnlyList<SupplierListItem>> ListAsync(string? search = null, CancellationToken ct = default)
    {
        var grants = await access.GrantsAsync("suppliers.read", ct);
        if (grants.Count == 0) return [];
        var query = db.Suppliers.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim().ToUpperInvariant(); query = query.Where(x => x.NormalisedName.Contains(s) || (x.VendorNumber != null && x.VendorNumber.Contains(s))); }
        var all = await query.OrderBy(x => x.Name).ToListAsync(ct);

        // A supplier is visible when some grant covers its organisation and, for a grant narrowed by category, business unit, site or
        // classification, at least one visible contract with that supplier exists (so a Category Viewer never learns of unrelated suppliers).
        var contractsVisible = ContractAccessFilter.Build(grants, await context.ListContextAsync(access.CurrentUserId, ct));
        var supplierIdsWithVisibleContracts = (await db.Contracts.AsNoTracking().Where(contractsVisible).Select(c => c.SupplierId).Distinct().ToListAsync(ct)).ToHashSet();
        var result = new List<SupplierListItem>();
        var taxReadable = await taxGate.ReadableOrganisationsAsync(grants, all.Select(x => x.OrganisationId), ct);
        foreach (var sup in all)
        {
            var visible = grants.Any(g => (g.OrganisationIds is null || g.OrganisationIds.Contains(sup.OrganisationId))
                && (IsOrgWide(g) || supplierIdsWithVisibleContracts.Contains(sup.Id)));
            if (visible) result.Add(new SupplierListItem(sup.Id, sup.OrganisationId, sup.Name, sup.VendorNumber, taxReadable.Contains(sup.OrganisationId) ? sup.TaxIdentifier : null, sup.Status, sup.SupplierType));
        }
        return result;
    }

    private static bool IsOrgWide(AccessGrant g) => g.BusinessUnitIds.Count == 0 && g.DivisionIds.Count == 0 && g.SiteIds.Count == 0 && g.CategoryIds is null
        && g.Relationship == GrantRelationship.None;

    public async Task<SupplierListItem> GetAsync(Guid id, CancellationToken ct = default)
    {
        // Same answer whether the supplier is missing, in another tenant, or outside the caller's scopes.
        return (await ListAsync(null, ct)).FirstOrDefault(s => s.Id == id) ?? throw new ResourceNotFoundException();
    }

    public async Task<IReadOnlyList<ContractListItem>> ContractsOfAsync(Guid supplierId, CancellationToken ct = default)
    {
        await GetAsync(supplierId, ct);
        return (await contracts.ListAsync(new ContractFilter(SupplierId: supplierId, PageSize: 200), ct)).Items;
    }

    public async Task<Guid> CreateAsync(CreateSupplierRequest request, CancellationToken ct = default)
    {
        await access.RequireAsync("suppliers.manage", new ResourceDescriptor("Supplier", tenant.RequireTenantId(), request.OrganisationId, Id: "new", Dimensions: ScopeDimensions.OrganisationOnly), ct);
        return await suppliers.CreateAsync(request, ct);
    }

    public async Task<Ecp.SharedKernel.Localisation.SupplierAddress> GetAddressAsync(Guid supplierId, CancellationToken ct = default)
    {
        await GetAsync(supplierId, ct);        // the same answer whether the counterparty is missing or out of scope
        return (await db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplierId, ct)).Address;
    }

    public async Task SetAddressAsync(Guid supplierId, Ecp.SharedKernel.Localisation.SupplierAddress address, CancellationToken ct = default)
    {
        var item = await GetAsync(supplierId, ct);
        await access.RequireAsync("suppliers.manage", new ResourceDescriptor("Supplier", tenant.RequireTenantId(), item.OrganisationId, Id: supplierId.ToString(), Dimensions: ScopeDimensions.OrganisationOnly), ct);
        var cleaned = address.Cleaned();
        var problems = cleaned.Check((await regional.GetAsync(item.OrganisationId, ct)).Address);
        if (problems.Count > 0) throw new DomainException(problems[0].Code, "Address: " + problems[0].Message);
        await uow.ExecuteAsync(async c =>
        {
            var supplier = await db.Suppliers.FirstAsync(s => s.Id == supplierId, c);
            var before = supplier.Address;
            supplier.SetAddress(cleaned);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("supplier.address.set", nameof(Supplier), supplierId.ToString(), OrganisationId: item.OrganisationId, OldValuesJson: AuditJson.Of(before), NewValuesJson: AuditJson.Of(cleaned)));
        }, ct);
    }
}

internal sealed class SecuredCategoryService(
    ContractsDbContext db, IAccessService access, ContractAccessContext context, ICategoryService categories, ICategoryAdministration admin, IUnitOfWork uow,
    IAuditWriter audit, ITenantContext tenant, IClock clock, CurrentUser currentUser) : ISecuredCategoryService
{
    public async Task<IReadOnlyList<CategoryItem>> ListAsync(CancellationToken ct = default)
    {
        // Categories are visible in the organisations where the caller holds any category-related authority, narrowed by category scope.
        var grants = (await access.GrantsAsync("contracts.read", ct)).Concat(await access.GrantsAsync("categories.manage", ct))
            .Concat(await access.GrantsAsync("contracts.create", ct)).ToList();
        if (grants.Count == 0) return [];
        var (_, descendants, _) = await context.CategoryTreeAsync(ct);
        var all = await admin.ListAllAsync(ct);
        return all.Where(c => grants.Any(g => (g.OrganisationIds is null || g.OrganisationIds.Contains(c.OrganisationId))
            && (g.CategoryIds is null || g.CategoryIds.Any(id => descendants.TryGetValue(id, out var d) && d.Contains(c.Id))))).ToList();
    }

    public async Task<Guid> CreateAsync(CreateCategoryRequest request, CancellationToken ct = default)
    {
        await access.RequireAsync("categories.manage", new ResourceDescriptor("Category", tenant.RequireTenantId(), request.OrganisationId, Id: "new", Dimensions: ScopeDimensions.OrganisationOnly), ct);
        return await categories.CreateAsync(request, ct);
    }

    public async Task DeactivateAsync(Guid categoryId, CancellationToken ct = default)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync("categories.manage", Describe(category), ct);
        await uow.ExecuteAsync(async c =>
        {
            var tracked = await db.Categories.FirstAsync(x => x.Id == categoryId, c);
            tracked.Deactivate();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("category.deactivated", nameof(Category), categoryId.ToString(), OrganisationId: tracked.OrganisationId));
        }, ct);
    }

    public async Task<Guid> AssignOwnerAsync(Guid categoryId, Guid userId, bool isPrimary, string? reason, CancellationToken ct = default)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync("categories.assign", Describe(category), ct);
        var by = currentUser.UserId?.ToString() ?? "unknown";
        return await admin.AssignOwnerTrustedAsync(categoryId, userId, isPrimary, clock.UtcNow, null, by, reason, ct);
    }

    public async Task EndOwnershipAsync(Guid assignmentId, string reason, CancellationToken ct = default)
    {
        var assignment = await db.CategoryAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assignmentId, ct) ?? throw new ResourceNotFoundException();
        var category = await db.Categories.AsNoTracking().FirstAsync(c => c.Id == assignment.CategoryId, ct);
        await access.RequireAsync("categories.assign", Describe(category), ct);
        var why = Guard.NotBlank(reason, "Reason", 1000);
        await uow.ExecuteAsync(async c =>
        {
            var tracked = await db.CategoryAssignments.FirstAsync(x => x.Id == assignmentId, c);
            tracked.End(clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("category.ownership_ended", nameof(CategoryAssignment), assignmentId.ToString(), OrganisationId: tracked.OrganisationId, Reason: why,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { categoryId = tracked.CategoryId, userId = tracked.UserId })));
        }, ct);
    }

    private static ResourceDescriptor Describe(Category c) => new("Category", c.TenantId, c.OrganisationId, DivisionId: c.DivisionId, CategoryId: c.Id, Id: c.Id.ToString(),
        Dimensions: ScopeDimensions.Organisation | ScopeDimensions.Division);
}

internal sealed class CategoryAdministration(
    ContractsDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IOrganisationStructureService structure) : ICategoryAdministration
{
    public async Task<Guid> UpsertCategoryAsync(Guid organisationId, string code, string name, string? parentCode, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        if (await structure.GetOrganisationAsync(organisationId, ct) is null) throw new DomainException("organisation_not_found", "The [[organisation|lower]] was not found.");
        var normalised = Guard.NotBlank(code, "[[category]] code", 100).ToUpperInvariant();
        Guid? parentId = null;
        if (!string.IsNullOrWhiteSpace(parentCode))
        {
            var pc = parentCode.Trim().ToUpperInvariant();
            parentId = await db.Categories.Where(c => c.OrganisationId == organisationId && c.Code == pc).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
                ?? throw new DomainException("parent_category_not_found", $"Parent [[category|lower]] '{pc}' was not found.");
        }
        return await uow.ExecuteAsync(async c =>
        {
            var existing = await db.Categories.FirstOrDefaultAsync(x => x.OrganisationId == organisationId && x.Code == normalised, c);
            Category category;
            if (existing is null)
            {
                category = Category.Create(tenantId, organisationId, normalised, name, parentId);
                db.Categories.Add(category);
            }
            else { existing.Rename(name); existing.Activate(); category = existing; }
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry(existing is null ? "category.created" : "category.updated", nameof(Category), category.Id.ToString(), OrganisationId: organisationId,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { category.Code, category.Name })));
            return category.Id;
        }, ct);
    }

    public async Task<Guid> AssignOwnerTrustedAsync(Guid categoryId, Guid userId, bool isPrimary, DateTime effectiveFromUtc, DateTime? effectiveToUtc, string assignedBy, string? reason, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct) ?? throw new DomainException("category_not_found", "The [[category|lower]] was not found.");
        var assignment = CategoryAssignment.Create(tenantId, category.OrganisationId, categoryId, userId, isPrimary, effectiveFromUtc, effectiveToUtc, assignedBy, reason, clock.UtcNow);
        return await uow.ExecuteAsync(async c =>
        {
            db.CategoryAssignments.Add(assignment);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("category.ownership_assigned", nameof(CategoryAssignment), assignment.Id.ToString(), OrganisationId: category.OrganisationId,
                Reason: reason, NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { categoryId, userId, isPrimary })));
            return assignment.Id;
        }, ct);
    }

    public async Task DeactivateCategoryAsync(Guid categoryId, string reason, CancellationToken ct = default)
    {
        await uow.ExecuteAsync(async c =>
        {
            var category = await db.Categories.FirstOrDefaultAsync(x => x.Id == categoryId, c) ?? throw new DomainException("category_not_found", "The [[category|lower]] was not found.");
            if (!category.IsActive) return;
            category.Deactivate();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("category.deactivated", nameof(Category), categoryId.ToString(), OrganisationId: category.OrganisationId, Reason: reason));
        }, ct);
    }

    public async Task EndOwnerTrustedAsync(Guid assignmentId, string reason, CancellationToken ct = default)
    {
        await uow.ExecuteAsync(async c =>
        {
            var assignment = await db.CategoryAssignments.FirstOrDefaultAsync(x => x.Id == assignmentId, c);
            if (assignment is null || !assignment.IsActiveAt(clock.UtcNow)) return;
            assignment.End(clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("category.ownership_ended", nameof(CategoryAssignment), assignmentId.ToString(), OrganisationId: assignment.OrganisationId, Reason: reason,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { categoryId = assignment.CategoryId, userId = assignment.UserId })));
        }, ct);
    }

    public async Task<IReadOnlyList<CategoryItem>> ListAllAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Code).ToListAsync(ct);
        var assignments = await db.CategoryAssignments.AsNoTracking().ToListAsync(ct);
        return categories.Select(c => new CategoryItem(c.Id, c.OrganisationId, c.Code, c.Name, c.ParentCategoryId, c.DivisionId, c.IsActive,
            assignments.Where(a => a.CategoryId == c.Id && a.IsActiveAt(now)).Select(a => new CategoryOwnerItem(a.Id, a.UserId, a.IsPrimary, a.EffectiveFromUtc, a.EffectiveToUtc)).ToList())).ToList();
    }
}

internal sealed class CategoryHierarchyProvider(ContractAccessContext context) : ICategoryHierarchy
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> AncestryAsync(CancellationToken ct = default)
        => (await context.CategoryTreeAsync(ct)).Ancestry;
}

internal sealed class TrustedReferenceAllocator(ReferenceAllocator allocator, IUnitOfWork uow) : IAgreementReferenceAllocator
{
    public Task<string> AllocateAsync(Guid organisationId, CancellationToken ct = default)
        => uow.ExecuteAsync(c => allocator.AllocateAsync(organisationId, consume: true, c), ct);
}

internal sealed class ContractLifecycleService(ContractsDbContext db, IUnitOfWork uow, IAuditWriter audit, IClock clock, IActorContext actor) : IContractLifecycleService
{
    public async Task ApplyStateAsync(Guid contractId, string statusCode, bool locks, CancellationToken ct = default)
    {
        await uow.ExecuteAsync(async c =>
        {
            var contract = await db.Contracts.FirstOrDefaultAsync(x => x.Id == contractId, c) ?? throw new DomainException("contract_not_found", "The [[contract|lower]] was not found.");
            var old = contract.Status;
            contract.ApplyWorkflowState(statusCode, locks, actor.ActorId ?? "system", clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("contract.state_applied", nameof(Contract), contractId.ToString(), OrganisationId: contract.OrganisationId, Classification: contract.Classification,
                OldValuesJson: AuditJson.Of(new { status = old }), NewValuesJson: AuditJson.Of(new { status = statusCode, locked = contract.RecordLocked })));
        }, ct);
    }
}

internal sealed class SupplierDirectory(ContractsDbContext db) : ISupplierDirectory
{
    public Task<Guid?> FindByVendorNumberAsync(Guid organisationId, string vendorNumber, CancellationToken ct = default)
        => db.Suppliers.AsNoTracking().Where(s => s.OrganisationId == organisationId && s.VendorNumber == vendorNumber).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);

    public Task<bool> ContractTitleExistsAsync(Guid organisationId, string title, CancellationToken ct = default)
        => db.Contracts.AsNoTracking().AnyAsync(c => c.OrganisationId == organisationId && c.ContractTitle == title, ct);

    public Task<bool> AnyContractAsync(CancellationToken ct = default) => db.Contracts.AsNoTracking().AnyAsync(ct);
}
