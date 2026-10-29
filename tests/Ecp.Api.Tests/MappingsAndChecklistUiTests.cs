using System.Net;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>UX-5 and UX-7 over HTTP: the set-up checklist leads the onboarding page, and identity group mappings live under Advanced for the people entitled to manage them, in words.</summary>
[Collection("Api")]
public sealed class MappingsAndChecklistUiTests
{
    private static async Task<string> PageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Identity_group_mappings_are_an_advanced_page_for_the_tenant_administrator_only_and_say_what_they_are_for()
    {
        using var app = DemoApp.Start();
        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        var page = await PageAsync(tenantAdmin, "/Admin/People/Mappings");
        Assert.Contains("Identity group mappings", page);
        Assert.Contains("Advanced", page);
        Assert.Contains("You do not need them to set up an individual person", page);
        Assert.Contains("never to administer the system or to approve", page);
        foreach (var jargon in new[] { "ProcurementAdministrator", "OrganisationAdministrator", "viewerKind", "CategoryOwner" }) Assert.DoesNotContain(jargon, page);
        Assert.Contains("Identity group mappings (advanced)", await PageAsync(tenantAdmin, "/Admin"));        // reachable from the Administration landing page

        using var orgAdmin = await app.SignInAsync("orgadmin@a.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await orgAdmin.GetAsync("/Admin/People/Mappings")).StatusCode);
        using var owner = await app.SignInAsync("catowner.packaging@a.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Admin/People/Mappings")).StatusCode);
        using var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/People/Mappings")).StatusCode);
    }

    [Fact]
    public async Task The_onboarding_front_page_leads_with_the_checklist_and_names_what_the_pack_requires()
    {
        using var app = DemoApp.Start();
        using var orgAdmin = await app.SignInAsync("orgadmin@a.example.test");
        var (tenantId, userId) = await app.FindUserAsync("orgadmin@a.example.test");
        var orgId = await app.WithTenantAsync(tenantId, async sp => (await sp.GetRequiredService<IRoleAssignmentService>().ListForUserAsync(userId))
            .Single(a => a.RoleCode == RoleCodes.OrganisationAdministrator).Scope.OrganisationIds.First());
        var page = await PageAsync(orgAdmin, $"/Onboarding?org={orgId}");
        Assert.Contains("Set-up checklist", page);
        Assert.Contains("Required", page);
        Assert.Contains("Draft sections", page);                                                  // the CSV sections remain, under an honest name
        Assert.True(page.IndexOf("Set-up checklist", StringComparison.Ordinal) < page.IndexOf("Draft sections", StringComparison.Ordinal));
        Assert.DoesNotContain("Ready to publish", page.Replace("Not ready to publish", ""));      // never claimed while a Required item is open
    }
}
