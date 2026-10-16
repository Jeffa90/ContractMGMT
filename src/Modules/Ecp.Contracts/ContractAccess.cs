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
    string Classification, bool RecordLocked);

public sealed record ContractFilter(string? Search = null, string? Status = null, Guid? CategoryId = null, Guid? SupplierId = null, int Page = 1, int PageSize = 25);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed record ContractDetails(
    Guid Id, Guid OrganisationId, string AgreementReference, string Title, string? Description, Guid SupplierId, string SupplierName, Guid CategoryId,
    string CategoryName, Guid? BusinessUnitId, Guid? SiteId, string Status, bool RecordLocked, string Classification, string? CurrencyCode, decimal? EstimatedValue,
    DateOnly? StartDate, DateOnly? EffectiveDate, DateOnly? ReviewDate, DateOnly? EndDate, DateTime? RegisteredDateUtc, string? RegisteredBy,
    bool IsLegacyLoad, DateTime CreatedUtc, string CreatedBy, bool CanEdit);

public sealed record SupplierListItem(Guid Id, Guid OrganisationId, string Name, string? VendorNumber, string? Abn, string Status);

public sealed record CategoryItem(Guid Id, Guid OrganisationId, string Code, string Name, Guid? ParentCategoryId, Guid? DivisionId, bool IsActive, IReadOnlyList<CategoryOwnerItem> Owners);
public sealed record CategoryOwnerItem(Guid AssignmentId, Guid UserId, bool IsPrimary, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc);

/// <summary>Reads and writes contracts on behalf of the signed-in user. Every method authorises on the server; the unsecured <see cref="IContractService"/> is for trusted internal callers only.</summary>
public interface ISecuredContractService
{
    Task<PagedResult<ContractListItem>> ListAsync(ContractFilter filter, CancellationToken ct = default);
    Task<ContractDetails> GetAsync(Guid id, CancellationToken ct = default);
    Task<Guid> CreateDraftAsync(CreateContractRequest request, CancellationToken ct = default);
    Task UpdateDetailsAsync(Guid id, string title, string? description, CancellationToken ct = default);
    /// <summary>Allocates the next Agreement Reference for the organisation using its prefix and numbering rule (D3).</summary>
    Task<string> PreviewNextReferenceAsync(Guid organisationId, CancellationToken ct = default);
    /// <summary>Descriptor for access simulation and tests: the facts the evaluator sees for a stored contract.</summary>
    Task<ResourceDescriptor> DescribeAsync(Guid id, CancellationToken ct = default);
    /// <summary>Counts visible contracts by status (for the home page).</summary>
    Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken ct = default);
}

public interface ISecuredSupplierService
{
    Task<IReadOnlyList<SupplierListItem>> ListAsync(string? search = null, CancellationToken ct = default);
    Task<SupplierListItem> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ContractListItem>> ContractsOfAsync(Guid supplierId, CancellationToken ct = default);
    Task<Guid> CreateAsync(CreateSupplierRequest request, CancellationToken ct = default);
}

public interface ISecuredCategoryService
{
    Task<IReadOnlyList<CategoryItem>> ListAsync(CancellationToken ct = default);
    Task<Guid> CreateAsync(CreateCategoryRequest request, CancellationToken ct = default);
    Task DeactivateAsync(Guid categoryId, CancellationToken ct = default);
    Task<Guid> AssignOwnerAsync(Guid categoryId, Guid userId, bool isPrimary, string? reason, CancellationToken ct = default);
    Task EndOwnershipAsync(Guid assignmentId, string reason, CancellationToken ct = default);
}

/// <summary>Trusted category operations used by publication of onboarding configuration and Demo seeding (no per-user authorisation).</summary>
public interface ICategoryAdministration
{
    Task<Guid> UpsertCategoryAsync(Guid organisationId, string code, string name, string? parentCode, CancellationToken ct = default);
    Task<Guid> AssignOwnerTrustedAsync(Guid categoryId, Guid userId, bool isPrimary, DateTime effectiveFromUtc, DateTime? effectiveToUtc, string assignedBy, string? reason, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryItem>> ListAllAsync(CancellationToken ct = default);
}

/// <summary>Loads the reference facts (category tree, ownership, structure) the access layer needs for contracts.</summary>
internal sealed class ContractAccessContext(ContractsDbContext db, IOrganisationStructureService structure, IClock clock)
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
        return new ContractAccessFilter.Context(descendants, owned, divisionOf);
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
        return new ResourceDescriptor("Contract", c.TenantId, c.OrganisationId, c.BusinessUnitId, division, c.SiteId, c.CategoryId, path, c.Classification,
            c.RecordLocked, owners, Id: c.Id.ToString());
    }
}

internal sealed class SecuredContractService(
    ContractsDbContext db, IAccessService access, ContractAccessContext context, IContractService contracts, IUnitOfWork uow,
    ReferenceAllocator allocator, IOrganisationStructureService structure, ITenantContext tenant, IClock clock) : ISecuredContractService
{
    public async Task<PagedResult<ContractListItem>> ListAsync(ContractFilter f, CancellationToken ct = default)
    {
        var predicate = await VisiblePredicateAsync(ct);
        var query = db.Contracts.AsNoTracking().Where(predicate);
        if (!string.IsNullOrWhiteSpace(f.Status)) query = query.Where(c => c.Status == f.Status);
        if (f.CategoryId is { } cat) query = query.Where(c => c.CategoryId == cat);
        if (f.SupplierId is { } sup) query = query.Where(c => c.SupplierId == sup);
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            query = query.Where(c => c.AgreementReference.Contains(term.ToUpper()) || c.ContractTitle.Contains(term));
        }
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, f.Page); var size = Math.Clamp(f.PageSize, 1, 200);
        var items = await (from c in query
                           join s in db.Suppliers.AsNoTracking() on new { c.TenantId, Id = c.SupplierId } equals new { s.TenantId, s.Id }
                           join g in db.Categories.AsNoTracking() on new { c.TenantId, Id = c.CategoryId } equals new { g.TenantId, g.Id }
                           orderby c.AgreementReference
                           select new ContractListItem(c.Id, c.OrganisationId, c.AgreementReference, c.ContractTitle, s.Name, g.Name, c.Status, c.Classification, c.RecordLocked))
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ContractListItem>(items, total, page, size);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountByStatusAsync(CancellationToken ct = default)
    {
        var predicate = await VisiblePredicateAsync(ct);
        return await db.Contracts.AsNoTracking().Where(predicate).GroupBy(c => c.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
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
        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == c.SupplierId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "";
        var category = await db.Categories.AsNoTracking().Where(s => s.Id == c.CategoryId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "";
        var canEdit = (await access.CheckSilentAsync("contracts.edit_owned", descriptor, ct)).Allowed;
        return new ContractDetails(c.Id, c.OrganisationId, c.AgreementReference, c.ContractTitle, c.Description, c.SupplierId, supplier, c.CategoryId, category,
            c.BusinessUnitId, c.SiteId, c.Status, c.RecordLocked, c.Classification, c.CurrencyCode, c.EstimatedValue, c.StartDate, c.EffectiveDate, c.ReviewDate,
            c.EndDate, c.RegisteredDateUtc, c.RegisteredBy, c.IsLegacyLoad, c.CreatedUtc, c.CreatedBy, canEdit);
    }

    public async Task<ResourceDescriptor> DescribeAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ResourceNotFoundException();
        return await context.DescribeAsync(c, ct);
    }

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
        if (r.BusinessUnitId is { } bu && org.BusinessUnits.All(b => b.Id != bu)) throw new DomainException("business_unit_not_found", "The business unit was not found in this organisation.");
        if (r.SiteId is { } site && org.Sites.All(s => s.Id != site)) throw new DomainException("site_not_found", "The site was not found in this organisation.");
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CategoryId && c.OrganisationId == r.OrganisationId, ct)
            ?? throw new DomainException("category_not_found", "The category was not found.");

        // Authorise against the facts of the contract that would be created, never against what the caller claims about themselves.
        var (_, _, ancestry) = await context.CategoryTreeAsync(ct);
        var path = ancestry.TryGetValue(category.Id, out var p) ? p : [category.Id];
        var now = clock.UtcNow;
        var owners = (await db.CategoryAssignments.AsNoTracking().Where(a => path.Contains(a.CategoryId)).ToListAsync(ct)).Where(a => a.IsActiveAt(now)).Select(a => a.UserId).ToHashSet();
        var descriptor = new ResourceDescriptor("Contract", tenantId, r.OrganisationId, r.BusinessUnitId, org.DivisionOfBusinessUnit(r.BusinessUnitId), r.SiteId,
            category.Id, path, r.Classification, false, owners, Id: "new");
        await access.RequireAsync("contracts.create", descriptor, ct);

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
    ContractsDbContext db, IAccessService access, ContractAccessContext context, ISupplierService suppliers, ISecuredContractService contracts, ITenantContext tenant) : ISecuredSupplierService
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
        foreach (var sup in all)
        {
            var visible = grants.Any(g => (g.OrganisationIds is null || g.OrganisationIds.Contains(sup.OrganisationId))
                && (IsOrgWide(g) || supplierIdsWithVisibleContracts.Contains(sup.Id)));
            if (visible) result.Add(new SupplierListItem(sup.Id, sup.OrganisationId, sup.Name, sup.VendorNumber, sup.Abn, sup.Status));
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
            audit.Record(new AuditEntry("category.ownership_ended", nameof(CategoryAssignment), assignmentId.ToString(), OrganisationId: tracked.OrganisationId, Reason: why));
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
        if (await structure.GetOrganisationAsync(organisationId, ct) is null) throw new DomainException("organisation_not_found", "The organisation was not found.");
        var normalised = Guard.NotBlank(code, "Category code", 100).ToUpperInvariant();
        Guid? parentId = null;
        if (!string.IsNullOrWhiteSpace(parentCode))
        {
            var pc = parentCode.Trim().ToUpperInvariant();
            parentId = await db.Categories.Where(c => c.OrganisationId == organisationId && c.Code == pc).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
                ?? throw new DomainException("parent_category_not_found", $"Parent category '{pc}' was not found.");
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
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct) ?? throw new DomainException("category_not_found", "The category was not found.");
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
