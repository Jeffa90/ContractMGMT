using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.Setup.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Setup.Onboarding;

public sealed record OnboardingOutcome(int Created, int Reused, int Ended);

/// <summary>
/// Makes a published onboarding release effective. Every step is an idempotent upsert: matching items that already exist are reused, nothing is deleted,
/// and the only things ended are items an earlier onboarding release itself created that the new release no longer lists. It runs inside the publishing
/// unit of work, so a failure leaves neither the release nor any partial effect behind.
/// </summary>
internal sealed class OnboardingApplier(
    SetupDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IOrganisationStructureService structure, ICategoryAdministration categories,
    IUserService users, IRoleAssignmentService assignments, IGroupMappingService mappings, IServiceProvider services)
{
    // The configuration service depends on every configuration kind, and this applier is used by one of them: it is resolved when needed to avoid a construction cycle.
    private IConfigurationService Configuration => services.GetRequiredService<IConfigurationService>();
    private IConfigurationBootstrap Bootstrap => services.GetRequiredService<IConfigurationBootstrap>();

    private const string Legal = "LegalEntity", Division = "Division", Unit = "BusinessUnit", Site = "Site", Category = "Category", Owner = "Owner", Viewer = "Viewer", Mapping = "Mapping";

    public async Task<OnboardingOutcome> ApplyAsync(Guid organisationId, OnboardingDocument next, OnboardingDocument? previous, int release, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var label = $"onboarding:release {release}";
        var now = clock.UtcNow;
        var ledger = await db.OnboardingItems.Where(i => i.OrganisationId == organisationId).ToListAsync(ct);
        int created = 0, reused = 0, ended = 0;

        OnboardingItem? Row(string type, string key) => ledger.FirstOrDefault(i => i.ItemType == type && i.ItemKey == key);
        void Record(string type, string key, bool existedBefore, Guid? refId)
        {
            var row = Row(type, key);
            if (row is null)
            {
                if (existedBefore) { reused++; return; }               // matched something that was already there: not ours to retire later
                var item = OnboardingItem.Create(tenantId, organisationId, type, key, refId, release, now);
                db.OnboardingItems.Add(item); ledger.Add(item); created++;
            }
            else if (row.EndedRelease is not null) { row.Reopen(refId, release, now); created++; }
            else { reused++; }
        }
        async Task RetireAsync(string type, IReadOnlySet<string> keep, Func<OnboardingItem, Task> end)
        {
            foreach (var row in ledger.Where(i => i.ItemType == type && i.EndedRelease is null && !keep.Contains(i.ItemKey)).ToList())
            {
                await end(row); row.End(release, now); ended++;
            }
        }

        // --- structure --------------------------------------------------------------------------------------------------------------------
        var before = await structure.GetAsync(organisationId, ct);
        foreach (var e in next.Structure.LegalEntities)
        { var k = StructureKey.Normalise(e.Key); await structure.UpsertLegalEntityAsync(organisationId, k, e.Name, e.RegistrationNumber, ct); Record(Legal, k, before.LegalEntities.Any(x => x.Key == k), null); }
        foreach (var e in next.Structure.Divisions)
        { var k = StructureKey.Normalise(e.Key); await structure.UpsertDivisionAsync(organisationId, k, e.Name, ct); Record(Division, k, before.Divisions.Any(x => x.Key == k), null); }
        foreach (var e in next.Structure.BusinessUnits)
        { var k = StructureKey.Normalise(e.Key); await structure.UpsertBusinessUnitAsync(organisationId, k, e.Name, e.ParentKey, ct); Record(Unit, k, before.BusinessUnits.Any(x => x.Key == k), null); }
        foreach (var e in next.Structure.Sites)
        { var k = StructureKey.Normalise(e.Key); await structure.UpsertSiteAsync(organisationId, k, e.Name, e.TimeZoneId, ct); Record(Site, k, before.Sites.Any(x => x.Key == k), null); }
        await RetireAsync(Site, Keys(next.Structure.Sites), r => structure.DeactivateAsync(StructureKind.Site, organisationId, r.ItemKey, ct));
        await RetireAsync(Unit, Keys(next.Structure.BusinessUnits), r => structure.DeactivateAsync(StructureKind.BusinessUnit, organisationId, r.ItemKey, ct));
        await RetireAsync(Division, Keys(next.Structure.Divisions), r => structure.DeactivateAsync(StructureKind.Division, organisationId, r.ItemKey, ct));
        await RetireAsync(Legal, Keys(next.Structure.LegalEntities), r => structure.DeactivateAsync(StructureKind.LegalEntity, organisationId, r.ItemKey, ct));

        // --- categories (parents before children) ---------------------------------------------------------------------------------------
        var existingCategories = (await categories.ListAllAsync(ct)).Where(c => c.OrganisationId == organisationId).ToList();
        var categoryIds = existingCategories.ToDictionary(c => c.Code, c => c.Id);
        var depth = Depths(next.Categories);
        foreach (var c in next.Categories.OrderBy(c => depth[OnboardingDocument.Key(c.Code)]))
        {
            var code = OnboardingDocument.Key(c.Code);
            var id = await categories.UpsertCategoryAsync(organisationId, code, c.Name, string.IsNullOrWhiteSpace(c.ParentCode) ? null : c.ParentCode, ct);
            categoryIds[code] = id;
            Record(Category, code, existingCategories.Any(x => x.Code == code), id);
        }
        await RetireAsync(Category, next.Categories.Select(c => OnboardingDocument.Key(c.Code)).ToHashSet(), r => categories.DeactivateCategoryAsync(categoryIds[r.ItemKey], label, ct));

        // --- people -------------------------------------------------------------------------------------------------------------------------
        async Task<Guid> UserAsync(string email)
            => (await users.FindByEmailAsync(email, ct))?.Id ?? throw new DomainException("user_not_found", $"No user with the address '{email.Trim()}' exists in this tenant.");
        Guid Category_(string code) => categoryIds.TryGetValue(OnboardingDocument.Key(code), out var id) ? id : throw new DomainException("category_not_found", $"[[category]] '{code}' was not found.");
        var structureNow = await structure.GetAsync(organisationId, ct);
        Guid Unit_(string key) => structureNow.BusinessUnits.FirstOrDefault(x => x.Key == StructureKey.Normalise(key))?.Id ?? throw new DomainException("business_unit_not_found", $"[[business.unit|sentence]] '{key}' was not found.");
        Guid Site_(string key) => structureNow.Sites.FirstOrDefault(x => x.Key == StructureKey.Normalise(key))?.Id ?? throw new DomainException("site_not_found", $"[[site]] '{key}' was not found.");

        // category-owner assignments
        foreach (var o in next.CategoryOwners)
        {
            var key = Canonical.Owner(o); var userId = await UserAsync(o.Email); var categoryId = Category_(o.CategoryCode);
            var current = (await categories.ListAllAsync(ct)).Single(c => c.Id == categoryId).Owners.FirstOrDefault(x => x.UserId == userId);
            if (current is not null) { Record(Owner, key, existedBefore: true, current.AssignmentId); continue; }
            var id = await categories.AssignOwnerTrustedAsync(categoryId, userId, o.IsPrimary, now, null, label, "Assigned by organisation onboarding", ct);
            Record(Owner, key, existedBefore: false, id);
        }
        await RetireAsync(Owner, next.CategoryOwners.Select(Canonical.Owner).ToHashSet(), r => r.RefId is { } id ? categories.EndOwnerTrustedAsync(id, label, ct) : Task.CompletedTask);

        // viewer scopes
        foreach (var v in next.ViewerScopes)
        {
            var key = Canonical.Viewer(v); var userId = await UserAsync(v.Email);
            var scope = OnboardingScopes.For(organisationId, v.Ceiling, v.CategoryCodes, v.BusinessUnitKeys, v.SiteKeys, Category_, Unit_, Site_);
            var match = (await assignments.ListForUserAsync(userId, includeEnded: false, ct)).FirstOrDefault(a => a.IsActive && a.RoleCode == RoleCodes.Viewer && a.ViewerKind == v.ViewerKind && SameScope(a.Scope, scope));
            if (match is not null) { Record(Viewer, key, existedBefore: true, match.Id); continue; }
            var id = await assignments.GrantTrustedAsync(new GrantRequest(userId, RoleCodes.Viewer, v.ViewerKind, scope, Reason: "Organisation onboarding"), AssignmentSource.Onboarding, label, null, ct);
            Record(Viewer, key, existedBefore: false, id);
        }
        await RetireAsync(Viewer, next.ViewerScopes.Select(Canonical.Viewer).ToHashSet(),
            r => r.RefId is { } id ? assignments.RevokeTrustedAsync(id, AssignmentSource.Onboarding, $"No longer listed by {label}", ct) : Task.CompletedTask);

        // identity-group mappings
        foreach (var g in next.GroupMappings)
        {
            var key = Canonical.Mapping(g);
            var scope = OnboardingScopes.For(organisationId, g.Ceiling, g.CategoryCodes, g.BusinessUnitKeys, g.SiteKeys, Category_, Unit_, Site_);
            var match = (await mappings.ListAsync(ct)).FirstOrDefault(m => m.IsActive && m.ProviderKey == g.ProviderKey.Trim() && m.GroupKey == g.GroupKey.Trim() && m.RoleCode == g.RoleCode
                && m.ViewerKind == g.ViewerKind && SameScope(m.Scope, scope));
            if (match is not null) { Record(Mapping, key, existedBefore: true, match.Id); continue; }
            var id = await mappings.AddAsync(g.ProviderKey.Trim(), g.GroupKey.Trim(), g.RoleCode, g.ViewerKind, scope, null, ct);
            Record(Mapping, key, existedBefore: false, id);
        }
        await RetireAsync(Mapping, next.GroupMappings.Select(Canonical.Mapping).ToHashSet(), r => r.RefId is { } id ? mappings.DeactivateAsync(id, ct) : Task.CompletedTask);

        // --- solution pack selection ---------------------------------------------------------------------------------------------------------
        if (next.PackId is { } packId)
        {
            var latest = await Configuration.LatestReleaseAsync("SolutionPack", Guid.Empty, ct);
            if (latest is null || !latest.ContentJson.Contains($"\"{packId}\"", StringComparison.Ordinal))
                await Bootstrap.DraftValidatePublishAsync("SolutionPack", Guid.Empty, System.Text.Json.JsonSerializer.Serialize(new { packId }), label, $"Selected by onboarding release {release}", ct);
        }

        await uow.ExecuteAsync(async c =>
        {
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("onboarding.applied", "OrganisationOnboarding", release.ToString(), OrganisationId: organisationId, Reason: label,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { created, reused, ended, release })));
        }, ct);
        return new OnboardingOutcome(created, reused, ended);

        static IReadOnlySet<string> Keys(IEnumerable<StructureEntry> entries) => entries.Select(e => StructureKey.Normalise(e.Key)).ToHashSet();
    }

    private static Dictionary<string, int> Depths(IReadOnlyList<CategoryEntry> entries)
    {
        var parent = entries.ToDictionary(e => OnboardingDocument.Key(e.Code), e => string.IsNullOrWhiteSpace(e.ParentCode) ? null : OnboardingDocument.Key(e.ParentCode));
        var result = new Dictionary<string, int>();
        foreach (var code in parent.Keys)
        {
            var d = 0; var cur = code; var guard = 0;
            while (cur is not null && parent.TryGetValue(cur, out var p) && guard++ < 50) { d++; cur = p; }
            result[code] = d;
        }
        return result;
    }

    /// <summary>Two scopes grant the same thing when level, organisations and every narrowing list match as sets, and the ceiling matches.</summary>
    internal static bool SameScope(ScopeSet a, ScopeSet b)
        => a.Level == b.Level && string.Equals(a.ClassificationCeiling, b.ClassificationCeiling, StringComparison.OrdinalIgnoreCase)
           && Same(a.OrganisationIds, b.OrganisationIds) && Same(a.BusinessUnitIds, b.BusinessUnitIds) && Same(a.DivisionIds, b.DivisionIds) && Same(a.SiteIds, b.SiteIds)
           && ((a.CategoryIds is null && b.CategoryIds is null) || (a.CategoryIds is not null && b.CategoryIds is not null && Same(a.CategoryIds, b.CategoryIds)));

    private static bool Same(IReadOnlyList<Guid> x, IReadOnlyList<Guid> y) => x.Count == y.Count && x.ToHashSet().SetEquals(y);
}
