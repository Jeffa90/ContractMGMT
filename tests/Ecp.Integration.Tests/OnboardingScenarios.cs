using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Setup.Onboarding;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

internal sealed class OnboardingWorld
{
    public Guid TenantId { get; init; }
    public Guid OrganisationId { get; init; }
    public Dictionary<string, Guid> Users { get; } = [];

    public const string Owner = "owner.one@onb.example.invalid";
    public const string ViewerA = "viewer.a@onb.example.invalid";
    public const string ViewerB = "viewer.b@onb.example.invalid";
    public const string OrgAdmin = "org.admin@onb.example.invalid";
    public const string Plain = "plain.user@onb.example.invalid";

    public static async Task<OnboardingWorld> CreateAsync(TestApp app)
    {
        var (tenantId, orgId) = await app.ProvisionAsync("Onb" + Guid.NewGuid().ToString("N")[..8], "ONB");
        var world = new OnboardingWorld { TenantId = tenantId, OrganisationId = orgId };
        await using var s = app.ScopeFor(tenantId);
        var users = s.Get<IUserService>();
        foreach (var (email, name) in new[] { (Owner, "Owner One"), (ViewerA, "Viewer A"), (ViewerB, "Viewer B"), (OrgAdmin, "Org Admin"), (Plain, "Plain User") })
            world.Users[email] = await users.CreateActiveTrustedAsync(name, email, null, null, null, "test");
        await s.Get<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(world.Users[OrgAdmin], RoleCodes.OrganisationAdministrator, null, ScopeSet.ForOrganisations([orgId], "Restricted"), ExplicitHighCeiling: true),
            AssignmentSource.Manual, "test");
        return world;
    }

    public static OnboardingDocument Release1() => new(1, null,
        new StructureSection([], [new("OPS", "Operations")], [new("PKG", "Packaging Operations", "OPS")], [new("SITE-A", "Site A", null, "Australia/Melbourne")]),
        [new("PACKAGING", "Packaging"), new("PACKAGING-FLEX", "Flexible Packaging", "PACKAGING")], [new("MSA", "Master services agreement")],
        [new("PACKAGING", Owner, true)],
        [new(ViewerA, ViewerKind.Category, ["PACKAGING"], [], [], "Internal")],
        [new("corp-sso", "grp-procurement-viewers", RoleCodes.Viewer, ViewerKind.BusinessUnit, [], ["PKG"], [], "Internal")]);

    public static OnboardingDocument Release2() => Release1() with
    {
        Categories = [.. Release1().Categories, new("ENERGY", "Energy")],
        ViewerScopes = [.. Release1().ViewerScopes, new(ViewerB, ViewerKind.Site, [], [], ["SITE-A"], "Confidential")],
    };
}

/// <summary>Scenario bodies for organisation onboarding, shared by the SQLite and SQL Server runs.</summary>
internal static class OnboardingScenarios
{
    private const string Kind = OrganisationOnboardingKind.KindName;

    private static Task<ReleaseDto> PublishAsync(TenantScope s, OnboardingWorld w, OnboardingDocument d, string note = "test")
        => s.Get<IConfigurationBootstrap>().DraftValidatePublishAsync(Kind, w.OrganisationId, d.ToJson(), "onboarding-test", note);

    public static async Task Publishing_makes_the_document_effective_across_structure_taxonomy_owners_viewers_and_mappings(TestApp app)
    {
        var w = await OnboardingWorld.CreateAsync(app);
        await using var s = app.ScopeFor(w.TenantId);
        var release = await PublishAsync(s, w, OnboardingWorld.Release1());
        Assert.Equal(1, release.Number);

        var structure = await s.Get<IOrganisationStructureService>().GetAsync(w.OrganisationId);
        Assert.Contains(structure.Divisions, d => d.Key == "OPS" && d.IsActive);
        var bu = Assert.Single(structure.BusinessUnits, b => b.Key == "PKG");
        Assert.Equal(structure.Divisions.Single(d => d.Key == "OPS").Id, bu.DivisionId);
        Assert.Equal("Australia/Melbourne", Assert.Single(structure.Sites).TimeZoneId);

        var cats = await s.Get<ICategoryAdministration>().ListAllAsync();
        var packaging = Assert.Single(cats, c => c.Code == "PACKAGING");
        var flex = Assert.Single(cats, c => c.Code == "PACKAGING-FLEX");
        Assert.Equal(packaging.Id, flex.ParentCategoryId);
        Assert.Contains(packaging.Owners, o => o.UserId == w.Users[OnboardingWorld.Owner] && o.IsPrimary);

        var viewer = Assert.Single(await s.Get<IRoleAssignmentService>().ListForUserAsync(w.Users[OnboardingWorld.ViewerA]));
        Assert.Equal(RoleCodes.Viewer, viewer.RoleCode); Assert.Equal(ViewerKind.Category, viewer.ViewerKind); Assert.Equal(AssignmentSource.Onboarding, viewer.Source);
        Assert.Equal([packaging.Id], viewer.Scope.CategoryIds); Assert.Equal([w.OrganisationId], viewer.Scope.OrganisationIds); Assert.Equal("Internal", viewer.Scope.ClassificationCeiling);

        var mapping = Assert.Single(await s.Get<IGroupMappingService>().ListAsync());
        Assert.True(mapping.IsActive); Assert.Equal("grp-procurement-viewers", mapping.GroupKey); Assert.Equal(ViewerKind.BusinessUnit, mapping.ViewerKind);
        Assert.Equal([bu.Id], mapping.Scope.BusinessUnitIds);

        // the release is versioned and audited
        var events = (await s.Get<IAuditEventSource>().Events.ToListAsync2()).Select(e => e.Action).ToList();
        Assert.Contains("config.published", events); Assert.Contains("onboarding.applied", events);
        Assert.True((await s.Get<AuditChainVerifier>().VerifyAsync()).IsValid);
    }

    public static async Task Publishing_the_same_document_again_changes_nothing(TestApp app)
    {
        var w = await OnboardingWorld.CreateAsync(app);
        await using var s = app.ScopeFor(w.TenantId);
        await PublishAsync(s, w, OnboardingWorld.Release1());
        var again = await PublishAsync(s, w, OnboardingWorld.Release1());
        Assert.Equal(2, again.Number);
        Assert.Equal(2, (await s.Get<ICategoryAdministration>().ListAllAsync()).Count);
        Assert.Single(await s.Get<IRoleAssignmentService>().ListForUserAsync(w.Users[OnboardingWorld.ViewerA]));
        Assert.Single(await s.Get<IGroupMappingService>().ListAsync());
        Assert.Single((await s.Get<ICategoryAdministration>().ListAllAsync()).Single(c => c.Code == "PACKAGING").Owners);
    }

    public static async Task Rolling_back_ends_only_what_a_later_release_introduced_and_never_what_existed_before(TestApp app)
    {
        var w = await OnboardingWorld.CreateAsync(app);
        await using var s = app.ScopeFor(w.TenantId);
        // "ENERGY" already exists (created by hand, not by onboarding) and so does a manual viewer assignment for B with the same scope as release 2.
        await s.Get<ICategoryAdministration>().UpsertCategoryAsync(w.OrganisationId, "ENERGY", "Energy (manual)", null);

        await PublishAsync(s, w, OnboardingWorld.Release1());
        await PublishAsync(s, w, OnboardingWorld.Release2());
        var energy = (await s.Get<ICategoryAdministration>().ListAllAsync()).Single(c => c.Code == "ENERGY");
        Assert.True(energy.IsActive);
        Assert.Single(await s.Get<IRoleAssignmentService>().ListForUserAsync(w.Users[OnboardingWorld.ViewerB]));

        var rolled = await s.Get<IConfigurationService>().LatestReleaseAsync(Kind, w.OrganisationId);
        Assert.Equal(2, rolled!.Number);
        // roll back to release 1 as a trusted caller would (the interactive path is covered by the authorised-user scenario)
        await using (var admin = app.ScopeFor(w.TenantId))
        {
            admin.Get<CurrentUser>().Set(w.Users[OnboardingWorld.OrgAdmin]);
            var release3 = await admin.Get<IOnboardingService>().RollbackAsync(w.OrganisationId, 1, "Back to the first agreed set");
            Assert.Equal(3, release3.Number); Assert.Equal(1, release3.RollbackOfNumber);
        }

        // the viewer scope that release 2 introduced is ended; the manual category stays active
        var viewerB = await s.Get<IRoleAssignmentService>().ListForUserAsync(w.Users[OnboardingWorld.ViewerB], includeEnded: true);
        Assert.All(viewerB, a => Assert.False(a.IsActive));
        Assert.Single(await s.Get<IRoleAssignmentService>().ListForUserAsync(w.Users[OnboardingWorld.ViewerA]));      // release 1 content remains
        Assert.True((await s.Get<ICategoryAdministration>().ListAllAsync()).Single(c => c.Code == "ENERGY").IsActive);   // pre-existing: never touched
        Assert.True((await s.Get<ICategoryAdministration>().ListAllAsync()).Single(c => c.Code == "PACKAGING").IsActive);

        // history is never rewritten: three releases, the third marked as a rollback
        var history = await s.Get<IConfigurationService>().LatestReleaseAsync(Kind, w.OrganisationId);
        Assert.Equal(3, history!.Number); Assert.Equal(1, history.RollbackOfNumber);
    }

    public static async Task Items_created_by_a_release_are_ended_when_a_later_release_drops_them_and_come_back_when_it_lists_them_again(TestApp app)
    {
        var w = await OnboardingWorld.CreateAsync(app);
        await using var s = app.ScopeFor(w.TenantId);
        await PublishAsync(s, w, OnboardingWorld.Release2());                       // creates ENERGY, viewer B, and the rest
        var without = OnboardingWorld.Release2() with { Categories = [.. OnboardingWorld.Release1().Categories], ViewerScopes = [.. OnboardingWorld.Release1().ViewerScopes] };
        await PublishAsync(s, w, without);
        Assert.False((await s.Get<ICategoryAdministration>().ListAllAsync()).Single(c => c.Code == "ENERGY").IsActive);
        Assert.DoesNotContain(await s.Get<IRoleAssignmentService>().ListForUserAsync(w.Users[OnboardingWorld.ViewerB]), a => a.IsActive);

        await PublishAsync(s, w, OnboardingWorld.Release2());                       // listed again
        Assert.True((await s.Get<ICategoryAdministration>().ListAllAsync()).Single(c => c.Code == "ENERGY").IsActive);
        Assert.Single(await s.Get<IRoleAssignmentService>().ListForUserAsync(w.Users[OnboardingWorld.ViewerB]), a => a.IsActive);
    }

    public static async Task An_invalid_document_is_refused_before_any_effect(TestApp app)
    {
        var w = await OnboardingWorld.CreateAsync(app);
        await using var s = app.ScopeFor(w.TenantId);
        var bad = OnboardingWorld.Release1() with { CategoryOwners = [new("PACKAGING", "nobody@onb.example.invalid", true)] };
        var ex = await Assert.ThrowsAsync<DomainException>(() => PublishAsync(s, w, bad));
        Assert.Equal("validation_failed", ex.Code);
        Assert.Contains("nobody@onb.example.invalid", ex.Message);
        Assert.Empty(await s.Get<ICategoryAdministration>().ListAllAsync());                                  // nothing was created
        Assert.Null(await s.Get<IConfigurationService>().LatestReleaseAsync(Kind, w.OrganisationId));         // and no release exists
    }

    public static async Task Only_authorised_users_can_run_onboarding_and_publication_needs_a_validated_unchanged_draft(TestApp app)
    {
        var w = await OnboardingWorld.CreateAsync(app);

        await using (var plain = app.ScopeFor(w.TenantId))
        {
            plain.Get<CurrentUser>().Set(w.Users[OnboardingWorld.Plain]);                                     // no role at all
            await Assert.ThrowsAnyAsync<Exception>(() => plain.Get<IOnboardingService>().GetAsync(w.OrganisationId));
        }
        await using (var anon = app.ScopeFor(w.TenantId))
            await Assert.ThrowsAnyAsync<Exception>(() => anon.Get<IOnboardingService>().GetAsync(w.OrganisationId));   // no signed-in user

        await using var admin = app.ScopeFor(w.TenantId);
        admin.Get<CurrentUser>().Set(w.Users[OnboardingWorld.OrgAdmin]);
        var svc = admin.Get<IOnboardingService>();
        var state = await svc.GetAsync(w.OrganisationId);
        Assert.Equal(OnboardingDocument.Empty.ToJson(), state.Document.ToJson());

        // edit through the wizard's CSV import, validate, publish
        var saved = await svc.SaveSectionAsync(w.OrganisationId, "categories", "code,name,parentCode\nPACKAGING,Packaging,\nPACKAGING-FLEX,Flexible Packaging,PACKAGING\n");
        Assert.Empty(saved.Issues); Assert.Equal(2, saved.State.Document.Categories.Count);
        await Assert.ThrowsAsync<DomainException>(() => svc.PublishAsync(w.OrganisationId, "too early"));       // not validated yet
        var validated = await svc.ValidateAsync(w.OrganisationId);
        Assert.True(validated.IsPublishable);
        await svc.SaveSectionAsync(w.OrganisationId, "types", "code,name\nMSA,Master services agreement\n");   // changing the draft invalidates the validation
        await Assert.ThrowsAsync<DomainException>(() => svc.PublishAsync(w.OrganisationId, "stale validation"));
        await svc.ValidateAsync(w.OrganisationId);
        var release = await svc.PublishAsync(w.OrganisationId, "First onboarding");
        Assert.Equal(1, release.Number);
        Assert.Equal(2, (await admin.Get<ICategoryAdministration>().ListAllAsync()).Count);
        var history = await svc.ReleasesAsync(w.OrganisationId);
        Assert.Equal(release.Number, Assert.Single(history).Number);

        // a CSV with a bad line is rejected as a whole and the draft is unchanged
        var rejected = await svc.SaveSectionAsync(w.OrganisationId, "viewers", "email,viewerKind\nviewer.a@onb.example.invalid,Galactic\n");
        Assert.NotEmpty(rejected.Issues);
        Assert.Empty((await svc.GetAsync(w.OrganisationId)).Document.ViewerScopes);
    }

    public static async Task Preview_and_plan_describe_the_draft_without_changing_anything(TestApp app)
    {
        var w = await OnboardingWorld.CreateAsync(app);
        await using var admin = app.ScopeFor(w.TenantId);
        admin.Get<CurrentUser>().Set(w.Users[OnboardingWorld.OrgAdmin]);
        var svc = admin.Get<IOnboardingService>();
        await svc.SaveSectionAsync(w.OrganisationId, "structure", OnboardingCsv.WriteStructure(OnboardingWorld.Release1().Structure));
        await svc.SaveSectionAsync(w.OrganisationId, "categories", OnboardingCsv.WriteCategories(OnboardingWorld.Release1().Categories));
        await svc.SaveSectionAsync(w.OrganisationId, "viewers", OnboardingCsv.WriteViewers(OnboardingWorld.Release1().ViewerScopes));

        var plan = await svc.PlanAsync(w.OrganisationId);
        Assert.Equal(2, plan.Single(p => p.Section == "Categories").ToCreate);
        Assert.Equal(1, plan.Single(p => p.Section == "Viewer scopes").ToCreate);
        var preview = await svc.PreviewAsync(w.OrganisationId);
        var row = Assert.Single(preview.Rows);
        Assert.True(row.Visible > 0 && row.Visible < row.Total);                                              // in scope sees some, not all
        Assert.Empty(await admin.Get<ICategoryAdministration>().ListAllAsync());                              // nothing was created
        Assert.Empty(await admin.Get<IRoleAssignmentService>().ListForUserAsync(w.Users[OnboardingWorld.ViewerA]));
    }
}
