using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.Contracts.Persistence;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Persistence;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Integration.Tests;

/// <summary>
/// A realistic tenant for access scenarios: two organisations, a division, business units, sites, a category tree, suppliers, contracts across
/// every combination that matters, and one user per role/scope shape. Built through the real services.
/// </summary>
internal sealed class AccessWorld
{
    private readonly TestApp _app;
    public AccessWorld(TestApp app) { _app = app; }

    public Guid TenantId { get; private set; }
    public Guid Org1 { get; private set; }
    public Guid Org2 { get; private set; }
    public Guid Div1 { get; private set; }
    public Guid Bu1 { get; private set; }   // in Div1
    public Guid Bu2 { get; private set; }   // no division
    public Guid Site1 { get; private set; }
    public Guid Site2 { get; private set; }
    public Guid CatPackaging { get; private set; }
    public Guid CatFlexible { get; private set; }  // child of Packaging
    public Guid CatEnergy { get; private set; }
    public Guid CatOrg2 { get; private set; }
    public Guid Sup1 { get; private set; }  // used by packaging contracts
    public Guid Sup2 { get; private set; }  // used by energy contracts
    public Guid Sup3 { get; private set; }  // org 2
    public Guid SupUnused { get; private set; }
    public Dictionary<string, Guid> Contracts { get; } = [];
    public Dictionary<string, Guid> Users { get; } = [];
    public Guid EndedPackagingAssignment { get; private set; }

    public static async Task<AccessWorld> CreateAsync(TestApp app, string name)
    {
        var w = new AccessWorld(app);
        await w.BuildAsync(name);
        return w;
    }

    private async Task BuildAsync(string name)
    {
        await using var bootstrap = _app.Services.CreateAsyncScope();
        var p = await bootstrap.ServiceProvider.GetRequiredService<ITenantProvisioningService>()
            .ProvisionAsync(new ProvisionTenantRequest(name, TenancyMode.MultiOrganisation, EcpMode.Test, name + " One", "Australia/Brisbane", "en-AU", "AUD", "WLD" + name[^1]));
        TenantId = p.TenantId; Org1 = p.OrganisationId;
        await using var s = _app.ScopeFor(TenantId);
        var tenancy = s.Get<ITenantProvisioningService>();
        Org2 = await tenancy.AddOrganisationAsync(new AddOrganisationRequest(name + " Two", "Australia/Sydney", "en-AU", "AUD", "WLX" + name[^1]));

        var structure = s.Get<IOrganisationStructureService>();
        var div = await structure.UpsertDivisionAsync(Org1, "OPS", "Operations");
        Bu1 = await structure.UpsertBusinessUnitAsync(Org1, "PKG", "Packaging Operations", "OPS");
        Bu2 = await structure.UpsertBusinessUnitAsync(Org1, "ENG", "Energy", null);
        Site1 = await structure.UpsertSiteAsync(Org1, "SITE-A", "Site A", "Australia/Melbourne");
        Site2 = await structure.UpsertSiteAsync(Org1, "SITE-B", "Site B", "Australia/Perth");
        var snapshot = await structure.GetAsync(Org1);
        Div1 = snapshot.Divisions.Single().Id;
        _ = div;

        var cats = s.Get<ICategoryAdministration>();
        CatPackaging = await cats.UpsertCategoryAsync(Org1, "PACKAGING", "Packaging", null);
        CatFlexible = await cats.UpsertCategoryAsync(Org1, "PACKAGING-FLEX", "Flexible Packaging", "PACKAGING");
        CatEnergy = await cats.UpsertCategoryAsync(Org1, "ENERGY", "Energy", null);
        CatOrg2 = await cats.UpsertCategoryAsync(Org2, "FACILITIES", "Facilities", null);

        var sup = s.Get<ISupplierService>();
        Sup1 = await sup.CreateAsync(new CreateSupplierRequest(Org1, "Alpha Packaging Supplies Pty Ltd"));
        Sup2 = await sup.CreateAsync(new CreateSupplierRequest(Org1, "Beta Energy Retail Pty Ltd"));
        SupUnused = await sup.CreateAsync(new CreateSupplierRequest(Org1, "Unused Supplier Pty Ltd"));
        Sup3 = await sup.CreateAsync(new CreateSupplierRequest(Org2, "Gamma Facilities Pty Ltd"));

        var contracts = s.Get<IContractService>();
        async Task<Guid> Add(string key, Guid org, string reference, Guid supplier, Guid category, string classification, Guid? bu, Guid? site)
            => Contracts[key] = await contracts.CreateDraftAsync(new CreateContractRequest(org, reference, "Synthetic " + key, supplier, category, Classification: classification, BusinessUnitId: bu, SiteId: site));
        await Add("C1", Org1, name + "-001", Sup1, CatPackaging, "Internal", Bu1, Site1);
        await Add("C2", Org1, name + "-002", Sup1, CatFlexible, "Confidential", Bu1, Site2);
        await Add("C3", Org1, name + "-003", Sup2, CatEnergy, "Internal", Bu2, Site1);
        await Add("C4", Org1, name + "-004", Sup2, CatEnergy, "Restricted", Bu2, Site2);
        await Add("C5", Org1, name + "-005", Sup1, CatPackaging, "Public", null, null);
        await Add("C6", Org2, name + "-006", Sup3, CatOrg2, "Internal", null, null);
        await Add("C7", Org1, name + "-007", Sup1, CatPackaging, "Internal", Bu1, Site1);
        // C7 is an Effective, locked record (the workflow engine arrives later; the lock is a domain operation).
        await s.Get<IUnitOfWork>().ExecuteAsync(async ct =>
        {
            var db = s.Get<ContractsDbContext>();
            var c7 = await db.Contracts.FirstAsync(x => x.Id == Contracts["C7"], ct);
            c7.ApplyWorkflowState("Effective", locks: true, "test", _app.Clock.UtcNow);
            await db.SaveChangesAsync(ct);
        });

        // Users: one per shape. Roles are granted through the trusted path, as setup and onboarding do.
        var scale = ClassificationScale.Default;
        _ = scale;
        await User("tenantAdmin", "Tenant Admin", RoleCodes.TenantAdministrator, null, ScopeSet.Tenant("Internal"));
        await User("orgAdmin", "Org Admin", RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([Org1], "Internal"));
        await User("siteAdmin", "Site Admin", RoleCodes.DivisionSiteAdministrator, null, ScopeSet.ForOrganisations([Org1], "Internal", sites: [Site1]));
        await User("viewerAll", "Viewer All", RoleCodes.Viewer, ViewerKind.AllContracts, ScopeSet.ForOrganisations([Org1], "Confidential"));
        await User("viewerCat", "Viewer Category", RoleCodes.Viewer, ViewerKind.Category, ScopeSet.ForOrganisations([Org1], "Confidential", categories: [CatPackaging]));
        await User("viewerBu", "Viewer BU", RoleCodes.Viewer, ViewerKind.BusinessUnit, ScopeSet.ForOrganisations([Org1], "Confidential", businessUnits: [Bu1]));
        await User("viewerSite", "Viewer Site", RoleCodes.Viewer, ViewerKind.Site, ScopeSet.ForOrganisations([Org1], "Confidential", sites: [Site2]));
        await User("viewerRestricted", "Viewer Restricted", RoleCodes.Viewer, ViewerKind.Restricted, ScopeSet.ForOrganisations([Org1], "Internal", sites: [Site1], categories: [CatEnergy]));
        await User("ownerPackaging", "Owner Packaging", RoleCodes.CategoryOwner, null, ScopeSet.ForOrganisations([Org1], "Confidential"));
        await User("ownerEnergy", "Owner Energy", RoleCodes.CategoryOwner, null, ScopeSet.ForOrganisations([Org1], "Confidential"));
        await User("legal", "Legal", RoleCodes.Legal, null, ScopeSet.ForOrganisations([Org1], "Restricted"));
        await User("auditor", "Auditor", RoleCodes.Auditor, null, ScopeSet.Tenant("Restricted"));
        await User("procAdmin", "Procurement Admin", RoleCodes.ProcurementAdministrator, null, ScopeSet.ForOrganisations([Org1], "Confidential"));
        await User("approver", "Approver", RoleCodes.Approver, null, ScopeSet.ForOrganisations([Org1], "Confidential"), "GroupA");

        await cats.AssignOwnerTrustedAsync(CatPackaging, Users["ownerPackaging"], true, _app.Clock.UtcNow.AddDays(-30), null, "world", null);
        await cats.AssignOwnerTrustedAsync(CatEnergy, Users["ownerEnergy"], true, _app.Clock.UtcNow.AddDays(-30), null, "world", null);
        // An assignment that has ended: the Energy owner used to own Packaging and must hold no edit rights there today.
        EndedPackagingAssignment = await cats.AssignOwnerTrustedAsync(CatPackaging, Users["ownerEnergy"], false, _app.Clock.UtcNow.AddDays(-400), _app.Clock.UtcNow.AddDays(-35), "world", "ended");
    }

    private async Task User(string key, string display, string role, ViewerKind? kind, ScopeSet scope, string? approverGroup = null)
    {
        await using var s = _app.ScopeFor(TenantId);
        var id = await s.Get<IUserService>().CreateActiveTrustedAsync(display, key + "@" + TenantId.ToString("N")[..8] + ".example.test", "demo", "https://demo.example.test", key + "|" + TenantId, "world");
        Users[key] = id;
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, kind, scope, approverGroup,
            EffectiveFromUtc: _app.Clock.UtcNow.AddDays(-100), ExplicitHighCeiling: true), AssignmentSource.Demo, "world");
    }

    public DateTime Now => _app.Clock.UtcNow;

    /// <summary>A scope in this tenant with no signed-in user (trusted setup-style operations and verification reads).</summary>
    public TenantScope AsScopeless() => _app.ScopeFor(TenantId);

    public Task<HashSet<string>> VisibleContractKeysForIdAsync(Guid userId) => AsIdAsync(userId, async s =>
    {
        var page = await s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200));
        return Contracts.Where(kv => page.Items.Any(i => i.Id == kv.Value)).Select(kv => kv.Key).ToHashSet();
    });

    /// <summary>Runs work as a signed-in user in this tenant.</summary>
    public async Task<T> AsAsync<T>(string userKey, Func<TenantScope, Task<T>> work) => await AsIdAsync(Users[userKey], work);

    public async Task<T> AsIdAsync<T>(Guid userId, Func<TenantScope, Task<T>> work)
    {
        await using var s = _app.ScopeFor(TenantId);
        s.Get<CurrentUser>().Set(userId);
        var actor = s.Get<ActorContext>();
        actor.Type = ActorType.User; actor.ActorId = userId.ToString(); actor.Source = "Test";
        return await work(s);
    }

    public Task AsAsync(string userKey, Func<TenantScope, Task> work) => AsAsync<bool>(userKey, async s => { await work(s); return true; });

    public Task<HashSet<string>> VisibleContractKeysAsync(string userKey) => AsAsync(userKey, async s =>
    {
        var page = await s.Get<ISecuredContractService>().ListAsync(new ContractFilter(PageSize: 200));
        return Contracts.Where(kv => page.Items.Any(i => i.Id == kv.Value)).Select(kv => kv.Key).ToHashSet();
    });
}
