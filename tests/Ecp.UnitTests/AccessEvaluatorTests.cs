using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>Shared fixtures for access tests: one tenant, two organisations, a small structure and category tree.</summary>
internal static class World
{
    public static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OtherTenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Org1 = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    public static readonly Guid Org2 = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    public static readonly Guid Div1 = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    public static readonly Guid Bu1 = Guid.Parse("b0000000-0000-0000-0000-000000000001"); // in Div1
    public static readonly Guid Bu2 = Guid.Parse("b0000000-0000-0000-0000-000000000002"); // no division
    public static readonly Guid Site1 = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    public static readonly Guid Site2 = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    public static readonly Guid CatPackaging = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    public static readonly Guid CatFlexible = Guid.Parse("e0000000-0000-0000-0000-000000000002"); // child of Packaging
    public static readonly Guid CatEnergy = Guid.Parse("e0000000-0000-0000-0000-000000000003");
    public static readonly Guid UserA = Guid.Parse("f0000000-0000-0000-0000-00000000000a");
    public static readonly Guid UserB = Guid.Parse("f0000000-0000-0000-0000-00000000000b");

    public static readonly DateTime Now = new(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);
    public static readonly ClassificationScale Scale = ClassificationScale.Default;

    public static AssignmentView Assign(string role, ScopeSet scope, ViewerKind? kind = null, string? approverGroup = null, DateTime? from = null, DateTime? to = null)
        => new(Guid.NewGuid(), role, kind, scope, approverGroup, from ?? Now.AddDays(-30), to);

    public static AccessSubject Subject(params AssignmentView[] assignments) => new(UserA, Tenant, UserStatus.Active, assignments);

    public static ResourceDescriptor Contract(Guid? org = null, Guid? category = null, string classification = "Internal", Guid? bu = null, Guid? site = null,
        bool locked = false, IEnumerable<Guid>? owners = null, Guid? tenant = null)
    {
        var cat = category ?? CatPackaging;
        var path = cat == CatFlexible ? new[] { CatFlexible, CatPackaging } : new[] { cat };
        Guid? division = bu == Bu1 ? Div1 : null;
        return new ResourceDescriptor("Contract", tenant ?? Tenant, org ?? Org1, bu, division, site, cat, path, classification, locked, owners?.ToHashSet(), Id: Guid.NewGuid().ToString());
    }

    public static AccessDecision Decide(AccessSubject s, string permission, ResourceDescriptor r) => AccessEvaluator.Default.Evaluate(s, Scale, Now, permission, r);
}

[Trait("Category", "Unit")]
public sealed class PermissionMatrixTests
{
    [Fact]
    public void The_confirmed_matrix_and_catalogue_load_with_twelve_roles()
    {
        var m = PermissionMatrix.Instance;
        Assert.Equal(69, m.Permissions.Count);
        Assert.Equal(12, RoleCodes.All.Count);
        Assert.Equal(Grant.Scoped, m.Of(RoleCodes.Viewer, "contracts.read"));      // Viewer is scoped, never a global read role
        Assert.Equal(Grant.Yes, m.Of(RoleCodes.CategoryOwner, "contracts.read"));  // Category Owner reads all within scope
        Assert.Equal(Grant.Scoped, m.Of(RoleCodes.CategoryOwner, "contracts.edit_owned"));
        Assert.Equal(Grant.None, m.Of(RoleCodes.Legal, "contracts.edit_owned"));   // Legal never edits commercial metadata
        Assert.Equal(Grant.None, m.Of(RoleCodes.SystemAdministrator, "contracts.read")); // administrators do not read business data
        Assert.Equal(Grant.None, m.Of(RoleCodes.BusinessConfigurationAdministrator, "contracts.edit_owned"));
        Assert.Equal(Grant.Yes, m.Of(RoleCodes.Auditor, "audit.export"));
        Assert.Equal(Grant.None, m.Of(RoleCodes.Auditor, "contracts.edit_owned"));
        Assert.Equal(Grant.Scoped, m.Of(RoleCodes.Approver, "approvals.action"));
        Assert.Equal(Grant.Scoped, m.Of(RoleCodes.BackupApprover, "approvals.action"));
    }

    [Fact]
    public void Unknown_permissions_are_rejected_rather_than_silently_denied()
        => Assert.Throws<ArgumentException>(() => World.Decide(World.Subject(), "contracts.teleport", World.Contract()));
}

[Trait("Category", "Unit")]
public sealed class ViewerScopeTests
{
    private static ScopeSet Org1(string ceiling = "Confidential", IEnumerable<Guid>? bus = null, IEnumerable<Guid>? sites = null, IEnumerable<Guid>? cats = null, IEnumerable<Guid>? divs = null)
        => ScopeSet.ForOrganisations([World.Org1], ceiling, bus, divs, sites, cats);

    [Fact]
    public void All_contracts_viewer_reads_its_organisation_only()
    {
        var s = World.Subject(World.Assign(RoleCodes.Viewer, Org1(), ViewerKind.AllContracts));
        Assert.True(World.Decide(s, "contracts.read", World.Contract(World.Org1)).Allowed);
        var other = World.Decide(s, "contracts.read", World.Contract(World.Org2));
        Assert.Equal(DenyKind.NotFound, other.Deny);
        Assert.Equal(3, other.Layer);
    }

    [Fact]
    public void Viewer_is_never_a_global_read_role_and_cannot_edit()
    {
        var s = World.Subject(World.Assign(RoleCodes.Viewer, Org1(), ViewerKind.AllContracts));
        Assert.Equal(DenyKind.Forbidden, World.Decide(s, "contracts.edit_owned", World.Contract(owners: [World.UserA])).Deny);
        Assert.Equal(DenyKind.Forbidden, World.Decide(s, "contracts.create", World.Contract()).Deny);
        Assert.Equal(DenyKind.Forbidden, World.Decide(s, "audit.read", World.Contract()).Deny);
        // no assignment at all means nothing is visible
        Assert.Equal(DenyKind.NotFound, World.Decide(World.Subject(), "contracts.read", World.Contract()).Deny);
    }

    [Fact]
    public void Category_viewer_reads_its_category_and_subcategories_but_not_another_category()
    {
        var s = World.Subject(World.Assign(RoleCodes.Viewer, Org1(cats: [World.CatPackaging]), ViewerKind.Category));
        Assert.True(World.Decide(s, "contracts.read", World.Contract(category: World.CatPackaging)).Allowed);
        Assert.True(World.Decide(s, "contracts.read", World.Contract(category: World.CatFlexible)).Allowed); // parent covers subcategories
        Assert.Equal(DenyKind.NotFound, World.Decide(s, "contracts.read", World.Contract(category: World.CatEnergy)).Deny);
    }

    [Fact]
    public void Business_unit_viewer_and_division_viewer()
    {
        var bu = World.Subject(World.Assign(RoleCodes.Viewer, Org1(bus: [World.Bu1]), ViewerKind.BusinessUnit));
        Assert.True(World.Decide(bu, "contracts.read", World.Contract(bu: World.Bu1)).Allowed);
        Assert.Equal(DenyKind.NotFound, World.Decide(bu, "contracts.read", World.Contract(bu: World.Bu2)).Deny);
        Assert.Equal(DenyKind.NotFound, World.Decide(bu, "contracts.read", World.Contract(bu: null)).Deny); // a record with no business unit is outside a narrowed scope

        var division = World.Subject(World.Assign(RoleCodes.Viewer, Org1(divs: [World.Div1]), ViewerKind.BusinessUnit));
        Assert.True(World.Decide(division, "contracts.read", World.Contract(bu: World.Bu1)).Allowed);   // Bu1 is in Div1
        Assert.Equal(DenyKind.NotFound, World.Decide(division, "contracts.read", World.Contract(bu: World.Bu2)).Deny); // Bu2 is in no division
    }

    [Fact]
    public void Site_viewer_reads_only_its_sites()
    {
        var s = World.Subject(World.Assign(RoleCodes.Viewer, Org1(sites: [World.Site1]), ViewerKind.Site));
        Assert.True(World.Decide(s, "contracts.read", World.Contract(site: World.Site1)).Allowed);
        Assert.Equal(DenyKind.NotFound, World.Decide(s, "contracts.read", World.Contract(site: World.Site2)).Deny);
        Assert.Equal(DenyKind.NotFound, World.Decide(s, "contracts.read", World.Contract(site: null)).Deny);
    }

    [Fact]
    public void Restricted_viewer_is_the_intersection_of_every_narrowing_and_its_ceiling()
    {
        var s = World.Subject(World.Assign(RoleCodes.Viewer, Org1("Internal", sites: [World.Site1], cats: [World.CatEnergy]), ViewerKind.Restricted));
        Assert.True(World.Decide(s, "contracts.read", World.Contract(category: World.CatEnergy, site: World.Site1, classification: "Internal")).Allowed);
        Assert.False(World.Decide(s, "contracts.read", World.Contract(category: World.CatEnergy, site: World.Site2)).Allowed);      // wrong site
        Assert.False(World.Decide(s, "contracts.read", World.Contract(category: World.CatPackaging, site: World.Site1)).Allowed);   // wrong category
        Assert.False(World.Decide(s, "contracts.read", World.Contract(category: World.CatEnergy, site: World.Site1, classification: "Confidential")).Allowed); // above ceiling
    }

    [Fact]
    public void Assignments_are_never_merged()
    {
        // One assignment covers Packaging at Site1; another covers Energy at Site2. A Packaging contract at Site2 is in neither.
        var s = World.Subject(
            World.Assign(RoleCodes.Viewer, Org1(sites: [World.Site1], cats: [World.CatPackaging]), ViewerKind.Restricted),
            World.Assign(RoleCodes.Viewer, Org1(sites: [World.Site2], cats: [World.CatEnergy]), ViewerKind.Restricted));
        Assert.True(World.Decide(s, "contracts.read", World.Contract(category: World.CatPackaging, site: World.Site1)).Allowed);
        Assert.True(World.Decide(s, "contracts.read", World.Contract(category: World.CatEnergy, site: World.Site2)).Allowed);
        Assert.False(World.Decide(s, "contracts.read", World.Contract(category: World.CatPackaging, site: World.Site2)).Allowed);
        Assert.False(World.Decide(s, "contracts.read", World.Contract(category: World.CatEnergy, site: World.Site1)).Allowed);
    }

    [Fact]
    public void Other_tenants_are_invisible_whatever_the_scope()
    {
        var s = World.Subject(World.Assign(RoleCodes.Viewer, Org1(), ViewerKind.AllContracts));
        var d = World.Decide(s, "contracts.read", World.Contract(tenant: World.OtherTenant));
        Assert.Equal(DenyKind.NotFound, d.Deny);
        Assert.Equal(2, d.Layer);
    }
}

[Trait("Category", "Unit")]
public sealed class ClassificationOverrideTests
{
    private static ScopeSet Org1(string ceiling, IEnumerable<Guid>? cats = null) => ScopeSet.ForOrganisations([World.Org1], ceiling, categories: cats);

    [Theory]
    [InlineData(RoleCodes.Viewer, ViewerKind.AllContracts)]
    [InlineData(RoleCodes.CategoryOwner, null)]
    [InlineData(RoleCodes.Legal, null)]
    [InlineData(RoleCodes.ProcurementAdministrator, null)]
    [InlineData(RoleCodes.Auditor, null)]
    public void A_record_above_the_ceiling_is_invisible_even_when_organisation_and_category_match(string role, ViewerKind? kind)
    {
        var s = World.Subject(World.Assign(role, Org1("Internal"), kind));
        Assert.True(World.Decide(s, "contracts.read", World.Contract(classification: "Internal")).Allowed);
        var above = World.Decide(s, "contracts.read", World.Contract(classification: "Confidential"));
        Assert.False(above.Allowed);
        Assert.Equal(DenyKind.NotFound, above.Deny); // invisible, not merely forbidden
        Assert.Equal(6, above.Layer);
        Assert.False(World.Decide(s, "contracts.read", World.Contract(classification: "Restricted")).Allowed);
    }

    [Fact]
    public void An_unknown_classification_never_satisfies_a_ceiling()
    {
        var s = World.Subject(World.Assign(RoleCodes.Legal, Org1("Restricted")));
        Assert.False(World.Decide(s, "contracts.read", World.Contract(classification: "TopSecretInvented")).Allowed);
        Assert.False(World.Decide(s, "contracts.read", World.Contract(classification: "")).Allowed);
    }

    [Fact]
    public void Classification_overrides_otherwise_valid_ownership()
    {
        var s = World.Subject(World.Assign(RoleCodes.CategoryOwner, Org1("Internal")));
        var own = World.Contract(classification: "Confidential", owners: [World.UserA]);
        Assert.False(World.Decide(s, "contracts.read", own).Allowed);
        Assert.False(World.Decide(s, "contracts.edit_owned", own).Allowed);
    }

    [Fact]
    public void The_scale_is_configurable_and_ordered()
    {
        var scale = new ClassificationScale(["Open", "Staff", "Board"], "Staff");
        var s = World.Subject(World.Assign(RoleCodes.Legal, Org1("Staff")));
        var board = World.Contract(classification: "Board");
        Assert.False(AccessEvaluator.Default.Evaluate(s, scale, World.Now, "contracts.read", board).Allowed);
        Assert.True(AccessEvaluator.Default.Evaluate(s, scale, World.Now, "contracts.read", World.Contract(classification: "Open")).Allowed);
        Assert.Equal(["Open", "Staff"], scale.AtOrBelow("Staff"));
        Assert.Empty(scale.AtOrBelow("Nonsense"));
    }
}

[Trait("Category", "Unit")]
public sealed class CategoryOwnerTests
{
    private static ScopeSet Org1(string ceiling = "Confidential") => ScopeSet.ForOrganisations([World.Org1], ceiling);
    private static AccessSubject Owner(ScopeSet? scope = null) => World.Subject(World.Assign(RoleCodes.CategoryOwner, scope ?? Org1()));

    [Fact]
    public void Reads_everything_in_the_organisation_regardless_of_category()
    {
        var s = Owner();
        Assert.True(World.Decide(s, "contracts.read", World.Contract(category: World.CatPackaging)).Allowed);
        Assert.True(World.Decide(s, "contracts.read", World.Contract(category: World.CatEnergy)).Allowed);
    }

    [Fact]
    public void Edits_only_where_the_user_is_the_current_owner_of_the_category()
    {
        var s = Owner();
        Assert.True(World.Decide(s, "contracts.edit_owned", World.Contract(category: World.CatPackaging, owners: [World.UserA])).Allowed);
        var notOwner = World.Decide(s, "contracts.edit_owned", World.Contract(category: World.CatEnergy, owners: [World.UserB]));
        Assert.Equal(DenyKind.Forbidden, notOwner.Deny);
        Assert.Equal(5, notOwner.Layer);
        Assert.False(World.Decide(s, "contracts.edit_owned", World.Contract(owners: [])).Allowed); // an ended assignment means no owners
        Assert.False(World.Decide(s, "contracts.edit_owned", World.Contract(owners: null)).Allowed);
    }

    [Fact]
    public void Cannot_edit_a_locked_record_even_when_the_owner()
    {
        var d = World.Decide(Owner(), "contracts.edit_owned", World.Contract(owners: [World.UserA], locked: true));
        Assert.Equal(DenyKind.Forbidden, d.Deny);
        Assert.Equal(7, d.Layer);
        Assert.False(World.Decide(Owner(), "documents.upload", World.Contract(owners: [World.UserA], locked: true)).Allowed);
    }

    [Fact]
    public void Read_all_remains_bounded_by_tenant_organisation_and_classification()
    {
        var s = Owner(Org1("Internal"));
        Assert.False(World.Decide(s, "contracts.read", World.Contract(org: World.Org2)).Allowed);
        Assert.False(World.Decide(s, "contracts.read", World.Contract(tenant: World.OtherTenant)).Allowed);
        Assert.False(World.Decide(s, "contracts.read", World.Contract(classification: "Confidential")).Allowed);
    }

    [Fact]
    public void Category_scope_narrows_edit_but_not_read()
    {
        var narrowed = ScopeSet.ForOrganisations([World.Org1], "Confidential", categories: [World.CatPackaging]);
        var s = Owner(narrowed);
        Assert.True(World.Decide(s, "contracts.read", World.Contract(category: World.CatEnergy)).Allowed);  // read-all ignores category scope
        Assert.False(World.Decide(s, "contracts.edit_owned", World.Contract(category: World.CatEnergy, owners: [World.UserA])).Allowed); // edit respects it
        Assert.True(World.Decide(s, "contracts.edit_owned", World.Contract(category: World.CatPackaging, owners: [World.UserA])).Allowed);
    }

    [Fact]
    public void Cannot_finally_approve_a_disposition_or_unlock_or_delete()
    {
        var s = Owner();
        var r = World.Contract(owners: [World.UserA]);
        Assert.True(World.Decide(s, "disposition.request", r).Allowed);
        Assert.False(World.Decide(s, "disposition.approve", r).Allowed);
        Assert.False(World.Decide(s, "contracts.unlock_emergency", r).Allowed);
        Assert.False(World.Decide(s, "approvals.action", r).Allowed);
    }
}

[Trait("Category", "Unit")]
public sealed class RoleBoundaryTests
{
    private static ScopeSet Org1(string ceiling = "Restricted") => ScopeSet.ForOrganisations([World.Org1], ceiling);

    [Fact]
    public void Legal_reads_and_reviews_but_cannot_register_authorise_or_edit_commercial_metadata()
    {
        var s = World.Subject(World.Assign(RoleCodes.Legal, Org1()));
        var r = World.Contract();
        Assert.True(World.Decide(s, "contracts.read", r).Allowed);
        Assert.True(World.Decide(s, "legal.review", r).Allowed);
        Assert.True(World.Decide(s, "legal.clear", r).Allowed);
        Assert.False(World.Decide(s, "approvals.action", r).Allowed);
        Assert.False(World.Decide(s, "contracts.edit_owned", r).Allowed);
    }

    [Fact]
    public void Approver_sees_a_record_only_while_an_approval_for_its_group_is_pending()
    {
        var s = World.Subject(World.Assign(RoleCodes.Approver, Org1("Confidential"), approverGroup: "GroupA"));
        var none = World.Contract();
        Assert.False(World.Decide(s, "contracts.read", none).Allowed);
        var pending = none with { PendingApprovalGroupKeys = new HashSet<string> { "GroupA" } };
        Assert.True(World.Decide(s, "contracts.read", pending).Allowed);
        Assert.True(World.Decide(s, "approvals.action", pending).Allowed);
        var otherGroup = none with { PendingApprovalGroupKeys = new HashSet<string> { "GroupB" } };
        Assert.False(World.Decide(s, "approvals.action", otherGroup).Allowed);
    }

    [Fact]
    public void A_backup_approver_has_the_same_authority_for_its_own_group_and_the_submitter_cannot_approve()
    {
        var s = World.Subject(World.Assign(RoleCodes.BackupApprover, Org1("Confidential"), approverGroup: "GroupA"));
        var pending = World.Contract() with { PendingApprovalGroupKeys = new HashSet<string> { "GroupA" } };
        Assert.True(World.Decide(s, "approvals.action", pending).Allowed);
        var own = pending with { SubmittedByUserId = World.UserA };
        var d = World.Decide(s, "approvals.action", own);
        Assert.False(d.Allowed);
        Assert.Equal(8, d.Layer);
    }

    [Fact]
    public void Auditor_reads_and_exports_evidence_but_cannot_operate()
    {
        var s = World.Subject(World.Assign(RoleCodes.Auditor, Org1()));
        var r = World.Contract();
        Assert.True(World.Decide(s, "audit.read", r).Allowed);
        Assert.True(World.Decide(s, "audit.export", r).Allowed);
        Assert.True(World.Decide(s, "contracts.read", r).Allowed);
        Assert.False(World.Decide(s, "contracts.edit_owned", r).Allowed);
        Assert.False(World.Decide(s, "contracts.create", r).Allowed);
        Assert.False(World.Decide(s, "users.manage", r).Allowed);
    }

    [Fact]
    public void Administrators_do_not_read_business_data_and_the_installation_administrator_never_reaches_tenant_data()
    {
        foreach (var role in new[] { RoleCodes.OrganisationAdministrator, RoleCodes.BusinessConfigurationAdministrator })
        {
            var s = World.Subject(World.Assign(role, Org1()));
            var d = World.Decide(s, "contracts.read", World.Contract());
            Assert.Equal(DenyKind.Forbidden, d.Deny); // in scope, but the role holds no such permission
            Assert.Equal(4, d.Layer);
        }
        var tenantAdmin = World.Subject(World.Assign(RoleCodes.TenantAdministrator, ScopeSet.Tenant("Public")));
        Assert.False(World.Decide(tenantAdmin, "contracts.read", World.Contract()).Allowed);
        var sysAdminSubject = new AccessSubject(World.UserA, SystemTenant.Id, UserStatus.Active, [World.Assign(RoleCodes.SystemAdministrator, ScopeSet.Installation())]);
        var tenantRecord = AccessEvaluator.Default.Evaluate(sysAdminSubject, World.Scale, World.Now, "contracts.read", World.Contract());
        Assert.Equal(DenyKind.NotFound, tenantRecord.Deny);
        var installationRecord = new ResourceDescriptor("Setup", SystemTenant.Id, Dimensions: ScopeDimensions.None);
        Assert.True(AccessEvaluator.Default.Evaluate(sysAdminSubject, World.Scale, World.Now, "setup.run", installationRecord).Allowed);
    }

    [Fact]
    public void Tenant_administrator_configures_only_tenant_level_definitions_and_cannot_unlock_or_approve()
    {
        var s = World.Subject(World.Assign(RoleCodes.TenantAdministrator, ScopeSet.Tenant("Public")));
        var tenantLevel = new ResourceDescriptor("Configuration", World.Tenant, IsTenantLevelDefinition: true, Dimensions: ScopeDimensions.None);
        var orgLevel = new ResourceDescriptor("Configuration", World.Tenant, World.Org1, Dimensions: ScopeDimensions.OrganisationOnly);
        Assert.True(World.Decide(s, "config.draft", tenantLevel).Allowed);
        Assert.False(World.Decide(s, "config.draft", orgLevel).Allowed);
        Assert.False(World.Decide(s, "contracts.unlock_emergency", World.Contract()).Allowed);
        Assert.False(World.Decide(s, "approvals.action", World.Contract()).Allowed);
    }

    [Fact]
    public void Procurement_administrator_force_unlock_needs_organisation_authorisation_and_cannot_approve_own_submission()
    {
        var s = World.Subject(World.Assign(RoleCodes.ProcurementAdministrator, Org1("Confidential")));
        var r = World.Contract(classification: "Internal");
        Assert.True(World.Decide(s, "contracts.read", r).Allowed);
        Assert.False(World.Decide(s, "contracts.unlock_emergency", r).Allowed);
        Assert.True(World.Decide(s, "contracts.unlock_emergency", r with { EmergencyUnlockAuthorised = true }).Allowed);
        Assert.False(World.Decide(s, "approvals.action", r).Allowed); // holds no approval role
    }

    [Fact]
    public void Division_site_administrator_is_limited_to_assigned_divisions_or_sites_and_has_no_implicit_rights()
    {
        var scope = ScopeSet.ForOrganisations([World.Org1], "Public", sites: [World.Site1]);
        var s = World.Subject(World.Assign(RoleCodes.DivisionSiteAdministrator, scope));
        var inSite = new ResourceDescriptor("User", World.Tenant, World.Org1, SiteId: World.Site1, Dimensions: ScopeDimensions.Organisation | ScopeDimensions.Site);
        var otherSite = inSite with { SiteId = World.Site2 };
        var noSiteDimension = new ResourceDescriptor("Category", World.Tenant, World.Org1, Dimensions: ScopeDimensions.OrganisationOnly);
        Assert.True(World.Decide(s, "users.manage", inSite).Allowed);
        Assert.False(World.Decide(s, "users.manage", otherSite).Allowed);
        Assert.False(World.Decide(s, "categories.manage", noSiteDimension).Allowed); // no division or site on the record: outside a limited administrator's scope
        Assert.False(World.Decide(s, "contracts.read", World.Contract(site: World.Site1)).Allowed);
        Assert.False(World.Decide(s, "contracts.unlock_emergency", World.Contract(site: World.Site1)).Allowed);
        Assert.False(World.Decide(s, "approvals.action", World.Contract(site: World.Site1)).Allowed);
    }
}

[Trait("Category", "Unit")]
public sealed class EffectiveDatingAndAccountTests
{
    private static ScopeSet Org1() => ScopeSet.ForOrganisations([World.Org1], "Restricted");

    [Fact]
    public void Assignments_apply_only_inside_their_effective_period()
    {
        var future = World.Subject(World.Assign(RoleCodes.Legal, Org1(), from: World.Now.AddDays(1)));
        var ended = World.Subject(World.Assign(RoleCodes.Legal, Org1(), from: World.Now.AddDays(-10), to: World.Now.AddDays(-1)));
        var endingNow = World.Subject(World.Assign(RoleCodes.Legal, Org1(), from: World.Now.AddDays(-10), to: World.Now));
        var current = World.Subject(World.Assign(RoleCodes.Legal, Org1(), from: World.Now, to: World.Now.AddDays(1)));
        Assert.False(World.Decide(future, "contracts.read", World.Contract()).Allowed);
        Assert.False(World.Decide(ended, "contracts.read", World.Contract()).Allowed);
        Assert.False(World.Decide(endingNow, "contracts.read", World.Contract()).Allowed);
        Assert.True(World.Decide(current, "contracts.read", World.Contract()).Allowed);
    }

    [Theory]
    [InlineData(UserStatus.Disabled)]
    [InlineData(UserStatus.Invited)]
    public void An_account_that_is_not_active_has_no_access(UserStatus status)
    {
        var s = new AccessSubject(World.UserA, World.Tenant, status, [World.Assign(RoleCodes.Legal, Org1())]);
        var d = AccessEvaluator.Default.Evaluate(s, World.Scale, World.Now, "contracts.read", World.Contract());
        Assert.False(d.Allowed);
        Assert.Equal(1, d.Layer);
        Assert.Empty(AccessEvaluator.Default.GrantsFor(s, World.Scale, World.Now, "contracts.read"));
        Assert.False(AccessEvaluator.Default.HoldsAnywhere(s, World.Now, "contracts.read"));
    }
}

[Trait("Category", "Unit")]
public sealed class GrantProjectionTests
{
    [Fact]
    public void Grants_carry_every_scope_element_for_list_queries()
    {
        var scope = ScopeSet.ForOrganisations([World.Org1], "Confidential", businessUnits: null, divisions: [World.Div1], sites: [World.Site1], categories: [World.CatPackaging]);
        var s = World.Subject(World.Assign(RoleCodes.Viewer, scope, ViewerKind.Restricted));
        var grant = Assert.Single(AccessEvaluator.Default.GrantsFor(s, World.Scale, World.Now, "contracts.read"));
        Assert.Equal([World.Org1], grant.OrganisationIds);
        Assert.Equal([World.Div1], grant.DivisionIds);
        Assert.Equal([World.Site1], grant.SiteIds);
        Assert.Equal([World.CatPackaging], grant.CategoryIds);
        Assert.Equal(["Public", "Internal", "Confidential"], grant.AllowedClassifications);
        Assert.Equal(GrantRelationship.None, grant.Relationship);
    }

    [Fact]
    public void Category_owner_read_grant_ignores_category_scope_and_edit_grant_requires_ownership()
    {
        var scope = ScopeSet.ForOrganisations([World.Org1], "Confidential", categories: [World.CatPackaging]);
        var s = World.Subject(World.Assign(RoleCodes.CategoryOwner, scope));
        Assert.Null(Assert.Single(AccessEvaluator.Default.GrantsFor(s, World.Scale, World.Now, "contracts.read")).CategoryIds);
        var edit = Assert.Single(AccessEvaluator.Default.GrantsFor(s, World.Scale, World.Now, "contracts.edit_owned"));
        Assert.Equal(GrantRelationship.ActiveCategoryOwnership, edit.Relationship);
        Assert.Equal([World.CatPackaging], edit.CategoryIds);
    }

    [Fact]
    public void Tenant_level_assignments_have_no_organisation_restriction_and_installation_ones_yield_nothing()
    {
        var auditor = World.Subject(World.Assign(RoleCodes.Auditor, ScopeSet.Tenant("Internal")));
        Assert.Null(Assert.Single(AccessEvaluator.Default.GrantsFor(auditor, World.Scale, World.Now, "audit.read")).OrganisationIds);
        var sys = new AccessSubject(World.UserA, SystemTenant.Id, UserStatus.Active, [World.Assign(RoleCodes.SystemAdministrator, ScopeSet.Installation())]);
        Assert.Empty(AccessEvaluator.Default.GrantsFor(sys, World.Scale, World.Now, "setup.run"));
    }

    [Fact]
    public void Roles_without_the_permission_yield_no_grant()
    {
        var s = World.Subject(World.Assign(RoleCodes.Legal, ScopeSet.ForOrganisations([World.Org1], "Public")));
        Assert.Empty(AccessEvaluator.Default.GrantsFor(s, World.Scale, World.Now, "contracts.edit_owned"));
    }
}

[Trait("Category", "Unit")]
public sealed class MatrixDrivenAuthorisationTests
{
    /// <summary>AUT-001: every cell of the confirmed matrix is enforced. A fully covering assignment that also satisfies the relationship rule is allowed exactly where the cell says Y or S.</summary>
    [Fact]
    public void Every_role_and_permission_follows_the_confirmed_matrix()
    {
        var matrix = PermissionMatrix.Instance;
        var checkedCells = 0;
        foreach (var role in RoleCodes.All)
            foreach (var permission in matrix.Permissions)
            {
                var scope = role switch
                {
                    RoleCodes.SystemAdministrator => ScopeSet.Installation(),
                    RoleCodes.TenantAdministrator => ScopeSet.Tenant("Restricted"),
                    RoleCodes.DivisionSiteAdministrator => ScopeSet.ForOrganisations([World.Org1], "Restricted", divisions: [World.Div1], sites: [World.Site1]),
                    _ => ScopeSet.ForOrganisations([World.Org1], "Restricted")
                };
                var kind = role == RoleCodes.Viewer ? ViewerKind.AllContracts : (ViewerKind?)null;
                var group = role is RoleCodes.Approver or RoleCodes.BackupApprover ? "G" : null;
                var subject = role == RoleCodes.SystemAdministrator
                    ? new AccessSubject(World.UserA, SystemTenant.Id, UserStatus.Active, [World.Assign(role, scope)])
                    : World.Subject(World.Assign(role, scope, kind, group));
                var tenantId = role == RoleCodes.SystemAdministrator ? SystemTenant.Id : World.Tenant;
                // A record that satisfies every scope and relationship, so only the matrix decides.
                var resource = new ResourceDescriptor("Any", tenantId, World.Org1, World.Bu1, World.Div1, World.Site1, World.CatPackaging, [World.CatPackaging], "Internal",
                    IsLocked: false, ActiveCategoryOwnerIds: new HashSet<Guid> { World.UserA }, PendingApprovalGroupKeys: new HashSet<string> { "G" },
                    SubmittedByUserId: World.UserB, IsTenantLevelDefinition: true, EmergencyUnlockAuthorised: true);
                var decision = World.Decide(subject, permission, resource);
                var cell = matrix.Of(role, permission);
                Assert.True(decision.Allowed == (cell != Grant.None), $"{role} / {permission}: matrix says {cell}, evaluator said {(decision.Allowed ? "allowed" : decision.Reason)}");
                checkedCells++;
            }
        Assert.Equal(12 * 69, checkedCells);
    }
}
