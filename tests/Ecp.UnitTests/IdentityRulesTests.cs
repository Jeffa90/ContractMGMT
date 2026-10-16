using System.Security.Claims;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Identity.Providers;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public sealed class AssignmentRuleTests
{
    private static readonly ClassificationScale Scale = ClassificationScale.Default;
    private static ScopeSet Org(string ceiling = "Internal", IEnumerable<Guid>? bus = null, IEnumerable<Guid>? divs = null, IEnumerable<Guid>? sites = null, IEnumerable<Guid>? cats = null)
        => ScopeSet.ForOrganisations([World.Org1], ceiling, bus, divs, sites, cats);

    private static string CodeOf(Action act) => Assert.Throws<DomainException>(act).Code;

    [Fact]
    public void An_assignment_without_an_organisational_scope_is_refused()
    {
        var none = new ScopeSet(ScopeLevel.Organisation, [], [], [], [], null, "Internal");
        Assert.Equal("organisation_scope_required", CodeOf(() => AssignmentRules.Validate(RoleCodes.Legal, null, none, null, Scale)));
        Assert.Equal("organisation_scope_required", CodeOf(() => AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.AllContracts, none, null, Scale)));
    }

    [Fact]
    public void Roles_are_assignable_only_at_their_scope_levels()
    {
        Assert.Equal("invalid_scope_level", CodeOf(() => AssignmentRules.Validate(RoleCodes.TenantAdministrator, null, Org(), null, Scale)));
        Assert.Equal("invalid_scope_level", CodeOf(() => AssignmentRules.Validate(RoleCodes.Legal, null, ScopeSet.Tenant("Internal"), null, Scale)));
        Assert.Equal("invalid_scope_level", CodeOf(() => AssignmentRules.Validate(RoleCodes.SystemAdministrator, null, Org(), null, Scale)));
        AssignmentRules.Validate(RoleCodes.Auditor, null, ScopeSet.Tenant("Internal"), null, Scale); // an Auditor may be tenant-wide
        AssignmentRules.Validate(RoleCodes.SystemAdministrator, null, ScopeSet.Installation(), null, Scale);
        Assert.Equal("unknown_role", CodeOf(() => AssignmentRules.Validate("Superuser", null, Org(), null, Scale)));
    }

    [Fact]
    public void Viewer_must_be_one_of_the_five_kinds_each_with_its_own_scope()
    {
        Assert.Equal("viewer_kind_required", CodeOf(() => AssignmentRules.Validate(RoleCodes.Viewer, null, Org(), null, Scale)));
        AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.AllContracts, Org(), null, Scale);
        Assert.Equal("invalid_scope", CodeOf(() => AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.AllContracts, Org(cats: [World.CatEnergy]), null, Scale)));
        Assert.Equal("scope_required", CodeOf(() => AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Category, Org(), null, Scale)));
        AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Category, Org(cats: [World.CatEnergy]), null, Scale);
        Assert.Equal("scope_required", CodeOf(() => AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.BusinessUnit, Org(), null, Scale)));
        AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.BusinessUnit, Org(bus: [World.Bu1]), null, Scale);
        AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.BusinessUnit, Org(divs: [World.Div1]), null, Scale);
        Assert.Equal("scope_required", CodeOf(() => AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Site, Org(), null, Scale)));
        AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Site, Org(sites: [World.Site1]), null, Scale);
        Assert.Equal("scope_required", CodeOf(() => AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Restricted, Org(), null, Scale)));
        AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Restricted, Org(sites: [World.Site1]), null, Scale);
    }

    [Fact]
    public void A_restricted_viewer_ceiling_may_not_exceed_confidential_unless_explicit()
    {
        var high = Org("Restricted", sites: [World.Site1]);
        Assert.Equal("ceiling_too_high", CodeOf(() => AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Restricted, high, null, Scale)));
        AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Restricted, high, null, Scale, explicitHighCeiling: true);
        AssignmentRules.Validate(RoleCodes.Viewer, ViewerKind.Restricted, Org("Confidential", sites: [World.Site1]), null, Scale);
    }

    [Fact]
    public void A_viewer_kind_belongs_only_to_the_viewer_role_and_approver_groups_only_to_approvers()
    {
        Assert.Equal("invalid_viewer_kind", CodeOf(() => AssignmentRules.Validate(RoleCodes.Legal, ViewerKind.Site, Org(), null, Scale)));
        Assert.Equal("required", CodeOf(() => AssignmentRules.Validate(RoleCodes.Approver, null, Org(), null, Scale)));
        AssignmentRules.Validate(RoleCodes.Approver, null, Org(), "GroupA", Scale);
        AssignmentRules.Validate(RoleCodes.BackupApprover, null, Org(), "GroupA", Scale);
        Assert.Equal("invalid_approver_group", CodeOf(() => AssignmentRules.Validate(RoleCodes.Legal, null, Org(), "GroupA", Scale)));
    }

    [Fact]
    public void Division_site_administrators_must_be_limited_and_ceilings_must_exist()
    {
        Assert.Equal("scope_required", CodeOf(() => AssignmentRules.Validate(RoleCodes.DivisionSiteAdministrator, null, Org(), null, Scale)));
        AssignmentRules.Validate(RoleCodes.DivisionSiteAdministrator, null, Org(sites: [World.Site1]), null, Scale);
        Assert.Equal("invalid_classification", CodeOf(() => AssignmentRules.Validate(RoleCodes.Legal, null, Org("Ultra"), null, Scale)));
        Assert.Equal("invalid_scope", CodeOf(() => AssignmentRules.Validate(RoleCodes.Legal, null, new ScopeSet(ScopeLevel.Organisation, [World.Org1], [], [], [], [], "Internal"), null, Scale)));
    }

    [Fact]
    public void Assignments_are_effective_dated_and_validated()
    {
        var a = RoleAssignment.Create(World.Tenant, World.UserA, RoleCodes.Legal, null, Org(), null, World.Now, World.Now.AddDays(10), null, "test", null, AssignmentSource.Manual, null, Scale, World.Now);
        Assert.True(a.IsActiveAt(World.Now));
        Assert.False(a.IsActiveAt(World.Now.AddDays(10)));
        a.End(World.Now.AddDays(1));
        Assert.False(a.IsActiveAt(World.Now.AddDays(1)));
        Assert.True(a.IsActiveAt(World.Now.AddHours(1)));
        a.End(World.Now.AddDays(5)); // ending again never extends the end
        Assert.Equal(World.Now.AddDays(1), a.EffectiveToUtc);
        Assert.Equal("invalid_dates", CodeOf(() => RoleAssignment.Create(World.Tenant, World.UserA, RoleCodes.Legal, null, Org(), null, World.Now, World.Now, null, "t", null, AssignmentSource.Manual, null, Scale, World.Now)));
        Assert.Equal("not_utc", CodeOf(() => RoleAssignment.Create(World.Tenant, World.UserA, RoleCodes.Legal, null, Org(), null, DateTime.Now, null, null, "t", null, AssignmentSource.Manual, null, Scale, World.Now)));
    }

    [Fact]
    public void Group_mappings_never_grant_system_or_tenant_administrator()
    {
        Assert.Equal("mapping_not_allowed", CodeOf(() => GroupRoleMapping.Create(World.Tenant, "p", "g", RoleCodes.TenantAdministrator, null, ScopeSet.Tenant("Internal"), null, Scale, World.Now)));
        Assert.Equal("mapping_not_allowed", CodeOf(() => GroupRoleMapping.Create(World.Tenant, "p", "g", RoleCodes.SystemAdministrator, null, ScopeSet.Installation(), null, Scale, World.Now)));
        GroupRoleMapping.Create(World.Tenant, "p", "g", RoleCodes.Legal, null, Org(), null, Scale, World.Now);
    }

    [Fact]
    public void Scope_containment_prevents_granting_above_ones_own_scope()
    {
        var outer = ScopeSet.ForOrganisations([World.Org1], "Confidential", businessUnits: [World.Bu1]);
        Assert.True(outer.Contains(ScopeSet.ForOrganisations([World.Org1], "Internal", businessUnits: [World.Bu1]), Scale));
        Assert.False(outer.Contains(ScopeSet.ForOrganisations([World.Org1], "Internal"), Scale));                                   // drops the business unit narrowing
        Assert.False(outer.Contains(ScopeSet.ForOrganisations([World.Org2], "Internal", businessUnits: [World.Bu1]), Scale));      // another organisation
        Assert.False(outer.Contains(ScopeSet.ForOrganisations([World.Org1], "Restricted", businessUnits: [World.Bu1]), Scale));    // higher ceiling
        Assert.False(outer.Contains(ScopeSet.Tenant("Internal"), Scale));                                                           // tenant-wide
        Assert.True(ScopeSet.Tenant("Restricted").Contains(ScopeSet.ForOrganisations([World.Org1, World.Org2], "Restricted"), Scale));
        // a parent category covers its subcategories
        var packaging = ScopeSet.ForOrganisations([World.Org1], "Internal", categories: [World.CatPackaging]);
        var flexible = ScopeSet.ForOrganisations([World.Org1], "Internal", categories: [World.CatFlexible]);
        Assert.False(packaging.Contains(flexible, Scale));
        Assert.True(packaging.Contains(flexible, Scale, id => id == World.CatFlexible ? [World.CatFlexible, World.CatPackaging] : [id]));
    }
}

[Trait("Category", "Unit")]
public sealed class DelegationPolicyTests
{
    private static readonly ClassificationScale Scale = ClassificationScale.Default;
    private static readonly PermissionMatrix Matrix = PermissionMatrix.Instance;

    private static string? Why(AssignmentView grantor, string role, ScopeSet target) => DelegationPolicy.WhyNot(Matrix, [grantor], World.Now, role, target, Scale);
    private static ScopeSet Org(Guid org, string ceiling = "Internal", IEnumerable<Guid>? sites = null) => ScopeSet.ForOrganisations([org], ceiling, sites: sites);

    [Fact]
    public void An_organisation_administrator_grants_within_its_own_organisation_only()
    {
        var admin = World.Assign(RoleCodes.OrganisationAdministrator, Org(World.Org1));
        Assert.Null(Why(admin, RoleCodes.Legal, Org(World.Org1)));
        Assert.Null(Why(admin, RoleCodes.Viewer, ScopeSet.ForOrganisations([World.Org1], "Internal", categories: [World.CatEnergy])));
        Assert.NotNull(Why(admin, RoleCodes.Legal, Org(World.Org2)));
        Assert.NotNull(Why(admin, RoleCodes.TenantAdministrator, ScopeSet.Tenant("Internal")));
        Assert.NotNull(Why(admin, RoleCodes.SystemAdministrator, ScopeSet.Installation()));
        Assert.NotNull(Why(admin, RoleCodes.Auditor, ScopeSet.Tenant("Internal")));  // tenant-wide is above an organisation administrator
    }

    [Fact]
    public void Only_a_tenant_administrator_grants_the_highest_classification_ceiling()
    {
        var orgAdmin = World.Assign(RoleCodes.OrganisationAdministrator, Org(World.Org1, "Internal"));
        Assert.Null(Why(orgAdmin, RoleCodes.Legal, Org(World.Org1, "Confidential")));  // administrators do not read data, so their own ceiling is not a bound
        Assert.NotNull(Why(orgAdmin, RoleCodes.Legal, Org(World.Org1, "Restricted")));
        var tenantAdmin = World.Assign(RoleCodes.TenantAdministrator, ScopeSet.Tenant("Internal"));
        Assert.Null(Why(tenantAdmin, RoleCodes.Legal, Org(World.Org1, "Restricted")));
        Assert.Null(Why(tenantAdmin, RoleCodes.OrganisationAdministrator, Org(World.Org2)));
        Assert.Null(Why(tenantAdmin, RoleCodes.Auditor, ScopeSet.Tenant("Confidential")));
        Assert.NotNull(Why(tenantAdmin, RoleCodes.SystemAdministrator, ScopeSet.Installation()));
    }

    [Fact]
    public void A_division_site_administrator_grants_only_within_its_sites_and_only_limited_roles()
    {
        var admin = World.Assign(RoleCodes.DivisionSiteAdministrator, Org(World.Org1, sites: [World.Site1]));
        Assert.Null(Why(admin, RoleCodes.Viewer, Org(World.Org1, sites: [World.Site1])));
        Assert.NotNull(Why(admin, RoleCodes.Viewer, Org(World.Org1)));                                  // wider than its sites
        Assert.NotNull(Why(admin, RoleCodes.Viewer, Org(World.Org1, sites: [World.Site2])));            // different site
        Assert.NotNull(Why(admin, RoleCodes.ProcurementAdministrator, Org(World.Org1, sites: [World.Site1])));
        Assert.NotNull(Why(admin, RoleCodes.Approver, Org(World.Org1, sites: [World.Site1])));          // no implicit approval rights
        Assert.NotNull(Why(admin, RoleCodes.DivisionSiteAdministrator, Org(World.Org1, sites: [World.Site1])));
    }

    [Fact]
    public void Roles_that_cannot_delegate_cannot_grant_anything()
    {
        foreach (var role in new[] { RoleCodes.Legal, RoleCodes.CategoryOwner, RoleCodes.Auditor, RoleCodes.Viewer, RoleCodes.ProcurementAdministrator, RoleCodes.BusinessConfigurationAdministrator })
        {
            var g = World.Assign(role, Org(World.Org1), role == RoleCodes.Viewer ? ViewerKind.AllContracts : null);
            Assert.Equal("The caller cannot delegate roles.", Why(g, RoleCodes.Viewer, Org(World.Org1)));
        }
    }

    [Fact]
    public void An_ended_delegation_grants_nothing()
    {
        var ended = World.Assign(RoleCodes.OrganisationAdministrator, Org(World.Org1), to: World.Now.AddDays(-1));
        Assert.NotNull(Why(ended, RoleCodes.Legal, Org(World.Org1)));
    }
}

[Trait("Category", "Unit")]
public sealed class IdentityProviderConfigurationTests
{
    private static IdentityProviderSettings Oidc(string? authority = "https://login.example.test/realm", string? secret = "env:ECP_OIDC_SECRET", string scopes = "openid profile email")
        => new("corp-oidc", IdentityProviderKind.Oidc, "Corporate sign-in", authority, "client-1", secret, null, scopes);

    private static IdentityProviderSettings Entra(string? tenant = "11111111-2222-3333-4444-555555555555", string? authority = null)
        => new("entra", IdentityProviderKind.Entra, "Microsoft Entra ID", authority, "client-2", "env:ECP_ENTRA_SECRET", tenant);

    private static string[] Codes(IReadOnlyList<ConfigurationIssue> issues) => issues.Where(i => i.Blocking).Select(i => i.Code).ToArray();

    [Fact]
    public void A_complete_oidc_configuration_is_valid()
        => Assert.True(IdentityProviderValidator.IsValid(IdentityProviderValidator.Validate(Oidc(), EcpMode.Production)));

    [Fact]
    public void Authority_must_be_https_outside_loopback_and_never_carry_credentials()
    {
        Assert.Contains("authority_https", Codes(IdentityProviderValidator.Validate(Oidc("http://idp.example.test"), EcpMode.Production)));
        Assert.Contains("authority_https", Codes(IdentityProviderValidator.Validate(Oidc("http://idp.example.test"), EcpMode.Demo)));
        Assert.DoesNotContain("authority_https", Codes(IdentityProviderValidator.Validate(Oidc("http://localhost:8080/realm"), EcpMode.Test)));
        Assert.Contains("authority_https", Codes(IdentityProviderValidator.Validate(Oidc("http://localhost:8080/realm"), EcpMode.Production)));
        Assert.Contains("authority_format", Codes(IdentityProviderValidator.Validate(Oidc("https://user:pw@idp.example.test"), EcpMode.Test)));
        Assert.Contains("authority", Codes(IdentityProviderValidator.Validate(Oidc("not a url"), EcpMode.Test)));
        Assert.Contains("authority", Codes(IdentityProviderValidator.Validate(Oidc(null), EcpMode.Test)));
    }

    [Fact]
    public void The_client_secret_must_be_a_reference_and_never_the_secret_itself()
    {
        Assert.Contains("client_secret_ref", Codes(IdentityProviderValidator.Validate(Oidc(secret: "s3cr3t-value-in-config"), EcpMode.Production)));
        Assert.Contains("client_secret_ref", Codes(IdentityProviderValidator.Validate(Oidc(secret: "https://vault.example.test/secret"), EcpMode.Production)));
        Assert.DoesNotContain("client_secret_ref", Codes(IdentityProviderValidator.Validate(Oidc(secret: "file:/run/secrets/oidc"), EcpMode.Production)));
        var missing = IdentityProviderValidator.Validate(Oidc(secret: null), EcpMode.Production);
        Assert.Contains(missing, i => i is { Code: "client_secret_missing", Blocking: false }); // public client with PKCE: a warning, not an error
    }

    [Fact]
    public void An_unresolvable_secret_blocks_production_only()
    {
        var none = new NoSecrets();
        Assert.Contains("client_secret_unresolved", Codes(IdentityProviderValidator.Validate(Oidc(), EcpMode.Production, none)));
        Assert.DoesNotContain("client_secret_unresolved", Codes(IdentityProviderValidator.Validate(Oidc(), EcpMode.Demo, none)));
    }

    [Fact]
    public void Entra_requires_a_directory_guid_and_derives_its_authority()
    {
        Assert.True(IdentityProviderValidator.IsValid(IdentityProviderValidator.Validate(Entra(), EcpMode.Production)));
        Assert.Equal("https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0", Entra().EffectiveAuthority);
        foreach (var bad in new[] { "common", "organizations", "consumers", "", null, "not-a-guid", "00000000-0000-0000-0000-000000000000" })
            Assert.Contains("entra_tenant", Codes(IdentityProviderValidator.Validate(Entra(bad), EcpMode.Production)));
        Assert.Contains("entra_authority", Codes(IdentityProviderValidator.Validate(Entra(authority: "https://login.microsoftonline.com/common/v2.0"), EcpMode.Production)));
        Assert.DoesNotContain("entra_authority", Codes(IdentityProviderValidator.Validate(Entra(authority: "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0/"), EcpMode.Production)));
    }

    [Fact]
    public void Scopes_keys_and_demo_provider_rules()
    {
        Assert.Contains("scope_openid", Codes(IdentityProviderValidator.Validate(Oidc(scopes: "profile email"), EcpMode.Test)));
        Assert.Contains("key", Codes(IdentityProviderValidator.Validate(Oidc() with { Key = "Bad Key!" }, EcpMode.Test)));
        var demo = new IdentityProviderSettings("demo", IdentityProviderKind.Demo, "Demonstration sign-in", null, null, null, null);
        Assert.True(IdentityProviderValidator.IsValid(IdentityProviderValidator.Validate(demo, EcpMode.Demo)));
        Assert.True(IdentityProviderValidator.IsValid(IdentityProviderValidator.Validate(demo, EcpMode.Test)));
        Assert.Contains("demo_in_production", Codes(IdentityProviderValidator.Validate(demo, EcpMode.Production)));
    }

    private sealed class NoSecrets : ISecretProvider { public string? Resolve(SecretReference reference) => null; }
}

[Trait("Category", "Unit")]
public sealed class ClaimsMappingTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
        => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    private static IdentityProviderSettings Oidc() => new("corp", IdentityProviderKind.Oidc, "Corp", "https://idp.example.test", "c", null, null);
    private static IdentityProviderSettings Entra() => new("entra", IdentityProviderKind.Entra, "Entra", null, "c", null, "11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Generic_oidc_maps_subject_email_name_and_groups()
    {
        var login = ClaimsMapper.Map(Oidc(), Principal(("iss", "https://idp.example.test"), ("sub", "abc"), ("email", "Pat@Example.test"), ("email_verified", "true"),
            ("name", "Pat Example"), ("groups", "g1"), ("groups", "g2"), ("groups", "g1")))!;
        Assert.Equal("corp", login.ProviderKey);
        Assert.Equal("https://idp.example.test", login.Issuer);
        Assert.Equal("abc", login.Subject);
        Assert.True(login.EmailTrusted);
        Assert.Equal("Pat Example", login.DisplayName);
        Assert.Equal(["g1", "g2"], login.Groups);
    }

    [Fact]
    public void An_unverified_email_is_not_trusted_for_linking()
    {
        var unverified = ClaimsMapper.Map(Oidc(), Principal(("iss", "https://idp.example.test"), ("sub", "abc"), ("email", "pat@example.test")))!;
        Assert.False(unverified.EmailTrusted);
        var falsy = ClaimsMapper.Map(Oidc(), Principal(("iss", "https://idp.example.test"), ("sub", "abc"), ("email", "pat@example.test"), ("email_verified", "false")))!;
        Assert.False(falsy.EmailTrusted);
    }

    [Fact]
    public void A_token_without_subject_or_issuer_is_refused()
    {
        Assert.Null(ClaimsMapper.Map(Oidc(), Principal(("iss", "https://idp.example.test"), ("email", "x@example.test"))));
        Assert.Null(ClaimsMapper.Map(Oidc(), Principal(("sub", "abc"))));
    }

    [Fact]
    public void Entra_uses_the_object_id_checks_the_directory_and_flags_group_overage()
    {
        var dir = "11111111-2222-3333-4444-555555555555";
        var login = ClaimsMapper.Map(Entra(), Principal(("iss", $"https://login.microsoftonline.com/{dir}/v2.0"), ("tid", dir), ("oid", "object-1"), ("sub", "pairwise-1"),
            ("preferred_username", "pat@corp.example.test"), ("name", "Pat"), ("groups", "11111111-aaaa-bbbb-cccc-111111111111")))!;
        Assert.Equal("object-1", login.Subject);                 // stable across applications, unlike the pairwise sub
        Assert.True(login.EmailTrusted);                          // administered by the configured directory
        Assert.Equal("pat@corp.example.test", login.Email);
        Assert.False(login.GroupsOverage);

        Assert.Null(ClaimsMapper.Map(Entra(), Principal(("iss", "https://login.microsoftonline.com/other/v2.0"), ("tid", "99999999-2222-3333-4444-555555555555"), ("oid", "o")))); // another directory
        var overage = ClaimsMapper.Map(Entra(), Principal(("iss", $"https://login.microsoftonline.com/{dir}/v2.0"), ("tid", dir), ("oid", "object-1"), ("_claim_names", "{\"groups\":\"src1\"}")))!;
        Assert.True(overage.GroupsOverage);
    }
}
