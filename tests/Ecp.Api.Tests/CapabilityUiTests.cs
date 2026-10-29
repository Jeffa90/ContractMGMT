using System.Net;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// S6 through the real host: the Capabilities administration pages, and the pages an organisation sees once a capability is switched on or off. The demo tenant has an organisation on the reference (JBS) pack, where every
/// capability is on, and one on the Blank pack, where Legal Review, Renewals and Signature Requests are off by default. Switching a capability changes the process people see, never what anyone may do.
/// </summary>
[Collection("Api")]
[Trait("Category", "Capabilities")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class CapabilityUiTests
{
    private const string TenantAdmin = "tenantadmin@a.example.test";
    private const string OrgAdmin = "orgadmin@a.example.test";
    private const string SiteAdmin = "siteadmin@a.example.test";
    private const string BizConfig = "bizconfig@a.example.test";
    private const string Viewer = "viewer.all@a.example.test";
    private const string Auditor = "auditor@a.example.test";
    private const string CatOwner = "catowner.packaging@a.example.test";

    private static readonly string[] Pages = ["/Admin/Capabilities", "/Admin/Capabilities/Edit", "/Admin/Capabilities/Preview", "/Admin/Capabilities/History", "/Admin/Capabilities/Audit"];

    private static async Task<(Guid Tenant, Guid Org)> TenantAsync(DemoApp app, string orgName)
    {
        var (tenant, _) = await app.FindUserAsync(TenantAdmin);
        var org = await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single(o => o.Name == orgName).Id);
        return (tenant, org);
    }

    private static async Task<string> UserInAsync(DemoApp app, Guid tenant, Guid org, string role, ViewerKind? kind = null, string ceiling = "Internal")
    {
        var email = $"cap.{Guid.NewGuid():N}@a.example.test"[..40] + ".test";
        await app.WithTenantAsync(tenant, async sp =>
        {
            var id = await sp.GetRequiredService<IUserService>().CreateActiveTrustedAsync("Capability " + role, email, Ecp.Host.Security.DemoIdentityService.ProviderKey, Ecp.Host.Security.DemoIdentityService.Issuer, email, "test");
            await sp.GetRequiredService<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, kind, ScopeSet.ForOrganisations([org], ceiling), null,
                EffectiveFromUtc: DateTime.UtcNow.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
            return 0;
        });
        return email;
    }

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private static string Edit(Guid org) => $"/Admin/Capabilities/Edit?organisationId={org}";

    /// <summary>The fields a browser would post for the edit form: every capability, inherit unless chosen.</summary>
    private static Dictionary<string, string> Form(Guid org, params (string Key, string Choice)[] choices)
    {
        var form = new Dictionary<string, string> { ["OrganisationId"] = org.ToString(), ["Note"] = "" };
        for (var i = 0; i < CapabilityCatalogue.All.Count; i++)
        {
            var key = CapabilityCatalogue.All[i].Key;
            form[$"Rows[{i}].Key"] = key;
            form[$"Rows[{i}].Choice"] = choices.FirstOrDefault(c => c.Key == key).Choice ?? "inherit";
        }
        return form;
    }

    private static string Post(string handler, Guid org) => $"/Admin/Capabilities/Edit?handler={handler}&organisationId={org}";

    // ---- access ----

    [Fact]
    public async Task Each_capability_page_opens_only_for_people_who_hold_its_permission()
    {
        using var app = DemoApp.Start();
        using var anonymous = app.NewClient();
        foreach (var page in Pages) Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync(page)).StatusCode);

        using var viewer = await app.SignInAsync(Viewer);
        using var site = await app.SignInAsync(SiteAdmin);
        using var owner = await app.SignInAsync(CatOwner);
        foreach (var client in new[] { viewer, site, owner })
            foreach (var page in Pages) Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(page)).StatusCode);

        foreach (var email in new[] { OrgAdmin, BizConfig, TenantAdmin })
        {
            using var admin = await app.SignInAsync(email, followRedirects: true);
            foreach (var page in Pages) { var r = await admin.GetAsync(page); Assert.True(r.StatusCode == HttpStatusCode.OK, $"{email} {page} -> {(int)r.StatusCode}"); }
        }

        // The auditor reads the overview, preview, history and audit trail, and cannot draft.
        using var auditor = await app.SignInAsync(Auditor, followRedirects: true);
        foreach (var page in new[] { "/Admin/Capabilities", "/Admin/Capabilities/Preview", "/Admin/Capabilities/History", "/Admin/Capabilities/Audit" }) Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(page)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/Admin/Capabilities/Edit")).StatusCode);
        Assert.DoesNotContain("Start a draft", await BodyAsync(auditor, "/Admin/Capabilities"));
    }

    [Fact]
    public async Task A_person_cannot_name_an_organisation_they_hold_no_capability_right_in()
    {
        using var app = DemoApp.Start();
        var (_, blank) = await TenantAsync(app, "Demo Facilities Organisation");
        using var org1Admin = await app.SignInAsync(OrgAdmin, followRedirects: true);                    // an administrator of the procurement organisation only
        Assert.Equal(HttpStatusCode.NotFound, (await org1Admin.GetAsync($"/Admin/Capabilities?organisationId={blank}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org1Admin.GetAsync(Edit(blank))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org1Admin.GetAsync($"/Admin/Capabilities/Audit?organisationId={blank}")).StatusCode);
    }

    // ---- overview ----

    [Fact]
    public async Task The_overview_says_what_is_in_force_and_where_each_answer_comes_from()
    {
        using var app = DemoApp.Start();
        var (tenant, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var admin = await UserInAsync(app, tenant, blankOrg, RoleCodes.OrganisationAdministrator);
        using var blank = await app.SignInAsync(admin, followRedirects: true);
        var html = await BodyAsync(blank, $"/Admin/Capabilities?organisationId={blankOrg}");
        foreach (var key in CapabilityKeys.All) Assert.Contains($"<code class=\"id\">{key}</code>", html);
        Assert.Contains("Pack default", html);
        Assert.Contains("The neutral lifecycle has no legal review step.", html);                // the pack's own reason, shown beside the default
        Assert.Contains("never changes who may do anything", html);

        using var jbs = await app.SignInAsync(OrgAdmin, followRedirects: true);
        var reference = await BodyAsync(jbs, "/Admin/Capabilities");
        Assert.DoesNotContain("badge warn\">Off", reference);                                    // every capability is on for the reference organisation
    }

    // ---- the form: publish, see it in the pages, refuse an incoherent choice, roll back ----

    [Fact]
    public async Task Switching_a_capability_on_and_off_changes_the_navigation_the_organisation_sees_and_nothing_a_person_may_do()
    {
        using var app = DemoApp.Start();
        var (tenant, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var adminEmail = await UserInAsync(app, tenant, blankOrg, RoleCodes.OrganisationAdministrator);
        var ownerEmail = await UserInAsync(app, tenant, blankOrg, RoleCodes.CategoryOwner);
        using var admin = await app.SignInAsync(adminEmail, followRedirects: false);
        using var owner = await app.SignInAsync(ownerEmail, followRedirects: true);

        // The Blank pack leaves Legal Review and Renewals off, so the facilities organisation's navigation has neither, and keeps the capabilities that are on.
        var before = await BodyAsync(owner, "/Actions");
        Assert.DoesNotContain("href=\"/Legal\"", before);
        Assert.DoesNotContain("href=\"/Renewals\"", before);
        Assert.Contains("href=\"/Variations\"", before);

        // The reference organisation keeps everything.
        using var refOwner = await app.SignInAsync(CatOwner, followRedirects: true);
        var reference = await BodyAsync(refOwner, "/Actions");
        Assert.Contains("href=\"/Legal\"", reference);
        Assert.Contains("href=\"/Renewals\"", reference);

        // Saving does not publish; validating and previewing show the effect; publishing makes it so.
        var form = Form(blankOrg, (CapabilityKeys.LegalReview, "on"), (CapabilityKeys.Renewals, "on"));
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(admin, Edit(blankOrg), form, Post("Save", blankOrg))).StatusCode);
        Assert.DoesNotContain("href=\"/Legal\"", await BodyAsync(owner, "/Actions"));
        var preview = await BodyAsync(admin, $"/Admin/Capabilities/Preview?organisationId={blankOrg}");
        Assert.Contains("is available again", preview);
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(admin, Edit(blankOrg), form, Post("Validate", blankOrg))).StatusCode);
        var publish = await DemoApp.PostFormAsync(admin, Edit(blankOrg), form, Post("Publish", blankOrg));
        Assert.Equal(HttpStatusCode.Redirect, publish.StatusCode);
        Assert.StartsWith("/Admin/Capabilities", publish.Headers.Location!.OriginalString);

        var after = await BodyAsync(owner, "/Actions");
        Assert.Contains("href=\"/Legal\"", after);
        Assert.Contains("href=\"/Renewals\"", after);

        // Switching them off again (an explicit choice) hides them again; History and Audit record all of it.
        var off = Form(blankOrg, (CapabilityKeys.LegalReview, "off"), (CapabilityKeys.Renewals, "off"));
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(admin, Edit(blankOrg), off, Post("Publish", blankOrg))).StatusCode);
        var hidden = await BodyAsync(owner, "/Actions");
        Assert.DoesNotContain("href=\"/Legal\"", hidden);
        Assert.DoesNotContain("href=\"/Renewals\"", hidden);
        var history = await BodyAsync(admin, $"/Admin/Capabilities/History?organisationId={blankOrg}");
        Assert.Contains("legal.review: on", history);
        Assert.Contains("legal.review: off", history);
        var audit = await BodyAsync(admin, $"/Admin/Capabilities/Audit?organisationId={blankOrg}");
        Assert.Contains("config.published", audit);

        // Rolling back restores release 1 (both on) as a new release.
        var rollback = await DemoApp.PostFormAsync(admin, $"/Admin/Capabilities/History?organisationId={blankOrg}", new Dictionary<string, string> { ["OrganisationId"] = blankOrg.ToString(), ["ToRelease"] = "1", ["Reason"] = "We do review" },
            $"/Admin/Capabilities/History?handler=Rollback&organisationId={blankOrg}");
        Assert.Equal(HttpStatusCode.Redirect, rollback.StatusCode);
        Assert.Contains("href=\"/Legal\"", await BodyAsync(owner, "/Actions"));

        // The organisation administrator's own access is unchanged by any of it; the owner still cannot open the capability pages.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Admin/Capabilities")).StatusCode);
    }

    [Fact]
    public async Task An_incoherent_choice_is_shown_beside_its_row_and_publishes_nothing()
    {
        using var app = DemoApp.Start();
        var (tenant, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var adminEmail = await UserInAsync(app, tenant, blankOrg, RoleCodes.OrganisationAdministrator);
        var ownerEmail = await UserInAsync(app, tenant, blankOrg, RoleCodes.CategoryOwner);
        using var admin = await app.SignInAsync(adminEmail, followRedirects: false);
        using var owner = await app.SignInAsync(ownerEmail, followRedirects: true);

        // Variations need Register Authorisation, and the neutral lifecycle has amendments: both choices below are refused, with the reason.
        var needs = Form(blankOrg, (CapabilityKeys.RegisterAuthorisation, "off"));
        var response = await DemoApp.PostFormAsync(admin, Edit(blankOrg), needs, Post("Publish", blankOrg));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);                                 // the page again, not a redirect
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("There is a problem", html);
        Assert.Contains("needs", html);
        Assert.Contains("href=\"#cap-", html);                                                // the error summary links to the row

        var uses = Form(blankOrg, (CapabilityKeys.Variations, "off"), (CapabilityKeys.RegisterAuthorisation, "off"));
        var usesResponse = await DemoApp.PostFormAsync(admin, Edit(blankOrg), uses, Post("Publish", blankOrg));
        Assert.Equal(HttpStatusCode.OK, usesResponse.StatusCode);
        Assert.Contains("uses", await usesResponse.Content.ReadAsStringAsync());

        // Nothing was published: the navigation is as it was.
        Assert.Contains("href=\"/Variations\"", await BodyAsync(owner, "/Actions"));
        Assert.Contains("published no choice of its own", await BodyAsync(admin, $"/Admin/Capabilities?organisationId={blankOrg}"));
    }

    // ---- onboarding shows what a pack switches off ----

    [Fact]
    public async Task The_onboarding_pack_step_shows_which_optional_steps_each_pack_switches_off()
    {
        using var app = DemoApp.Start();
        var (_, orgId) = await TenantAsync(app, "Demo Procurement Group");
        using var admin = await app.SignInAsync(OrgAdmin);
        var page = await admin.GetStringAsync($"/Onboarding/Step/pack?org={orgId}");
        Assert.Contains("Optional steps", page);
        Assert.Contains("The neutral lifecycle has no legal review step.", page);               // the Blank pack's reason for switching Legal Review off
        Assert.Contains("<strong>off</strong>", page);
        Assert.Contains("<strong>on</strong>", page);
    }
}
