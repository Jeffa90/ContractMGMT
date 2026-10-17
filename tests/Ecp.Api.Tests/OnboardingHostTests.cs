using System.Net;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class OnboardingHostTests
{
    private static async Task<(Guid TenantId, Guid OrgId)> TenantAOrgAsync(DemoApp app)
    {
        var (tenantId, userId) = await app.FindUserAsync("orgadmin@a.example.test");
        // the organisation the Organisation Administrator administers
        var orgId = await app.WithTenantAsync(tenantId, async sp => (await sp.GetRequiredService<IRoleAssignmentService>().ListForUserAsync(userId))
            .Single(a => a.RoleCode == RoleCodes.OrganisationAdministrator).Scope.OrganisationIds.First());
        return (tenantId, orgId);
    }

    [Fact]
    public async Task Onboarding_is_closed_to_anonymous_visitors_and_to_roles_without_the_permission()
    {
        using var app = DemoApp.Start();
        var (_, orgId) = await TenantAOrgAsync(app);
        using var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Onboarding")).StatusCode);
        foreach (var email in new[] { "viewer.category@a.example.test", "catowner.packaging@a.example.test", "legal@a.example.test", "auditor@a.example.test", "procadmin@a.example.test" })
        {
            using var client = await app.SignInAsync(email);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Onboarding?org={orgId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Onboarding/Step/categories?org={orgId}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Onboarding/Review?org={orgId}")).StatusCode);
        }
    }

    [Fact]
    public async Task The_pack_step_offers_the_packs_the_loader_finds_with_their_publisher_and_signature_and_allows_staying_on_blank()
    {
        using var app = DemoApp.Start();
        var (_, orgId) = await TenantAOrgAsync(app);
        using var admin = await app.SignInAsync("orgadmin@a.example.test");
        var page = await admin.GetStringAsync($"/Onboarding/Step/pack?org={orgId}");
        Assert.Contains("Blank (no business defaults)", page);
        Assert.Contains("Publisher", page);
        Assert.Contains("NOT signed", page);                                    // unsigned demonstration packs say so
        Assert.Contains("None (start from the Blank configuration)", page);     // staying on the Blank configuration is always possible
        Assert.Contains("Nothing takes effect until you validate and publish", page);

        // Choosing a pack that is not available here is refused with an explanation, not a stack trace.
        var bad = await DemoApp.PostFormAsync(admin, $"/Onboarding/Step/pack?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString(), ["PackId"] = "no-such-pack" }, $"/Onboarding/Step/pack?org={orgId}");
        var body = await bad.Content.ReadAsStringAsync();
        Assert.DoesNotContain("   at ", body);
        Assert.Contains("not available here", body);
    }

    [Fact]
    public async Task An_organisation_administrator_drafts_validates_previews_publishes_and_rolls_back_through_the_wizard()
    {
        using var app = DemoApp.Start();
        var (tenantId, orgId) = await TenantAOrgAsync(app);
        using var admin = await app.SignInAsync("orgadmin@a.example.test");

        var home = await admin.GetStringAsync($"/Onboarding?org={orgId}");
        Assert.Contains("Checklist", home); Assert.Contains("Nothing has been published yet", home);
        Assert.Contains("Onboarding", await admin.GetStringAsync("/")); // navigation entry

        // a bad import is rejected as a whole, with line numbers, and keeps what the person typed
        var bad = await DemoApp.PostFormAsync(admin, $"/Onboarding/Step/viewers?org={orgId}", new Dictionary<string, string>
        { ["Org"] = orgId.ToString(), ["Csv"] = "email,viewerKind\nviewer.all@a.example.test,Galactic\n" }, $"/Onboarding/Step/viewers?org={orgId}");
        var badBody = await bad.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Nothing was saved", badBody); Assert.Contains("Line 2", badBody); Assert.Contains("Galactic", badBody);

        // save two lists
        foreach (var (step, csv) in new[]
        {
            ("categories", "code,name,parentCode\nONB-DEMO,Onboarding demo,\nONB-CHILD,Onboarding child,ONB-DEMO\n"),
            ("viewers", "email,viewerKind,categories,businessUnits,sites,ceiling\nviewer.all@a.example.test,Category,ONB-DEMO,,,Internal\n"),
        })
        {
            var saved = await DemoApp.PostFormAsync(admin, $"/Onboarding/Step/{step}?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString(), ["Csv"] = csv }, $"/Onboarding/Step/{step}?org={orgId}");
            Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        }
        Assert.Contains("ONB-CHILD,Onboarding child,ONB-DEMO", await admin.GetStringAsync($"/Onboarding/Step/categories?org={orgId}"));

        // publication needs validation first
        var review = await admin.GetStringAsync($"/Onboarding/Review?org={orgId}");
        Assert.Contains("has not been validated", review);
        Assert.Contains("disabled", review);
        var tooEarly = await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString(), ["Note"] = "early" }, $"/Onboarding/Review?handler=Publish&org={orgId}");
        Assert.Equal(HttpStatusCode.OK, tooEarly.StatusCode);
        Assert.Contains("must be validated", await tooEarly.Content.ReadAsStringAsync());

        var validate = await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString() }, $"/Onboarding/Review?handler=Validate&org={orgId}");
        Assert.Equal(HttpStatusCode.Redirect, validate.StatusCode);
        var validated = await admin.GetStringAsync($"/Onboarding/Review?org={orgId}");
        Assert.Contains(">Valid<", validated);
        Assert.Contains("Who could see what", validated);
        Assert.Contains("viewer.all@a.example.test", validated);       // the simulated person
        Assert.Contains("Category", validated);

        var publish = await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString(), ["Note"] = "First release" }, $"/Onboarding/Review?handler=Publish&org={orgId}");
        Assert.Equal(HttpStatusCode.Redirect, publish.StatusCode);
        Assert.Contains("Release 1 was published", await admin.GetStringAsync($"/Onboarding/Review?org={orgId}"));

        // the release is in effect
        await app.WithTenantAsync(tenantId, async sp =>
        {
            var cats = await sp.GetRequiredService<ICategoryAdministration>().ListAllAsync();
            Assert.Contains(cats, c => c.Code == "ONB-CHILD" && c.IsActive && c.OrganisationId == orgId);
            var users = sp.GetRequiredService<IUserService>();
            var viewer = (await users.FindByEmailAsync("viewer.all@a.example.test"))!;
            Assert.Contains(await sp.GetRequiredService<IRoleAssignmentService>().ListForUserAsync(viewer.Id), a => a.Source == AssignmentSource.Onboarding && a.ViewerKind == ViewerKind.Category);
            return 0;
        });

        // a second release adds a category; rolling back to release 1 ends it
        var more = await DemoApp.PostFormAsync(admin, $"/Onboarding/Step/categories?org={orgId}", new Dictionary<string, string>
        { ["Org"] = orgId.ToString(), ["Csv"] = "code,name,parentCode\nONB-DEMO,Onboarding demo,\nONB-CHILD,Onboarding child,ONB-DEMO\nONB-LATER,Added later,\n" }, $"/Onboarding/Step/categories?org={orgId}");
        Assert.Equal(HttpStatusCode.Redirect, more.StatusCode);
        await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString() }, $"/Onboarding/Review?handler=Validate&org={orgId}");
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString(), ["Note"] = "Second" }, $"/Onboarding/Review?handler=Publish&org={orgId}")).StatusCode);

        var noReason = await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString(), ["ToRelease"] = "1", ["Reason"] = "" }, $"/Onboarding/Review?handler=Rollback&org={orgId}");
        Assert.Equal(HttpStatusCode.OK, noReason.StatusCode);                                   // a reason is required
        var rollback = await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={orgId}", new Dictionary<string, string> { ["Org"] = orgId.ToString(), ["ToRelease"] = "1", ["Reason"] = "Second release was premature" }, $"/Onboarding/Review?handler=Rollback&org={orgId}");
        Assert.Equal(HttpStatusCode.Redirect, rollback.StatusCode);
        var finalReview = await admin.GetStringAsync($"/Onboarding/Review?org={orgId}");
        Assert.Contains("rollback to 1", finalReview);
        await app.WithTenantAsync(tenantId, async sp =>
        {
            var cats = await sp.GetRequiredService<ICategoryAdministration>().ListAllAsync();
            Assert.False(cats.Single(c => c.Code == "ONB-LATER").IsActive);
            Assert.True(cats.Single(c => c.Code == "ONB-DEMO").IsActive);
            return 0;
        });
    }

    [Fact]
    public async Task A_tenant_administrator_can_open_onboarding_but_is_told_why_organisation_configuration_is_refused()
    {
        // Confirmed matrix: a Tenant Administrator holds onboarding.run, but config.draft/publish/rollback only for tenant-level definitions.
        using var app = DemoApp.Start();
        var (_, orgId) = await TenantAOrgAsync(app);
        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        var response = await tenantAdmin.GetAsync($"/Onboarding?org={orgId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("You cannot change this organisation", body);
        Assert.Contains("Organisation Administrator", body);
        Assert.DoesNotContain("Checklist", body);
    }

    [Fact]
    public async Task Onboarding_cannot_be_pointed_at_an_organisation_in_another_tenant_and_state_changes_need_the_antiforgery_token()
    {
        using var app = DemoApp.Start();
        var (_, orgA) = await TenantAOrgAsync(app);
        var (tenantB, _) = await app.FindUserAsync("tenantadmin@b.example.test");
        var orgB = await app.WithTenantAsync(tenantB, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).First().Id);
        using var adminA = await app.SignInAsync("orgadmin@a.example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/Onboarding?org={orgB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/Onboarding/Review?org={orgB}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminA.GetAsync($"/Onboarding/Step/nonsense?org={orgA}")).StatusCode);

        var noToken = await adminA.PostAsync($"/Onboarding/Step/categories?org={orgA}", new FormUrlEncodedContent(new Dictionary<string, string> { ["Csv"] = "code,name\nX,Y\n" }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        var noTokenPublish = await adminA.PostAsync($"/Onboarding/Review?handler=Publish&org={orgA}", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, noTokenPublish.StatusCode);
    }
}
