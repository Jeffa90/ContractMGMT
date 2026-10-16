using System.Net;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>Support access through the real pages: the request, the persistent banner, the tenant's own view of it, and ending it.</summary>
[Collection("Api")]
[Trait("Category", "Elevation")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ElevationUiTests
{
    [Fact]
    public async Task An_installation_administrator_works_under_a_visible_banner_and_the_tenant_can_see_and_end_it()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        var (tenantId, _) = await app.FindUserAsync("procadmin@a.example.test");

        var page = await sysadmin.GetStringAsync("/Admin/Support");
        Assert.Contains("Request access", page);
        var open = await DemoApp.PostFormAsync(sysadmin, "/Admin/Support", [new("tenantId", tenantId.ToString()), new("kind", "Support"), new("reason", "Investigating a missing contract report"), new("minutes", "30"), new("ceiling", "")],
            "/Admin/Support?handler=Open");
        Assert.Equal(HttpStatusCode.Redirect, open.StatusCode);

        // Every page now shows the banner, and the administrator reads the tenant's contracts within the access they were given.
        var home = await sysadmin.GetStringAsync("/");
        Assert.Contains("Support access is active", home);
        Assert.Contains("Investigating a missing contract report", home);
        var contracts = await sysadmin.GetStringAsync("/Contracts");
        Assert.Contains("Internal", contracts);                                                                   // within the tenant's default level; Confidential and above are not shown
        Assert.Equal(HttpStatusCode.Forbidden, (await sysadmin.GetAsync("/Migration")).StatusCode);                  // read-only access: no migration pages

        // The tenant's own administrator sees the access and what was done under it.
        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        var seen = await tenantAdmin.GetStringAsync("/Admin/Elevations");
        Assert.Contains("Investigating a missing contract report", seen);
        Assert.Contains("Active", seen);

        // Ending it from the banner returns the administrator to their own tenant: the banner is gone and tenant data is no longer reachable.
        var end = await DemoApp.PostFormAsync(sysadmin, "/", [], "/Account/EndAccess");
        Assert.Equal(HttpStatusCode.Redirect, end.StatusCode);
        Assert.DoesNotContain("Support access is active", await sysadmin.GetStringAsync("/Admin/Support"));
        var after = await sysadmin.GetAsync("/Contracts");
        Assert.NotEqual(HttpStatusCode.OK, after.StatusCode);
        Assert.Contains("Ended", await tenantAdmin.GetStringAsync("/Admin/Elevations"));
    }

    [Fact]
    public async Task People_without_the_permission_cannot_open_or_even_see_the_support_pages()
    {
        using var app = DemoApp.Start();
        using var procurement = await app.SignInAsync("procadmin@a.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await procurement.GetAsync("/Admin/Support")).StatusCode);
        Assert.Contains("__RequestVerificationToken", await procurement.GetStringAsync("/"));                         // the sign-out form carries its antiforgery token
        using var viewer = await app.SignInAsync("viewer.all@a.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Elevations")).StatusCode);
    }
}
