using System.Net;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>UX-2: My access explains in plain English what the signed-in person can do, where, and why, computed by the evaluator, with the role detail underneath.</summary>
[Collection("Api")]
public sealed class MyAccessUiTests
{
    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_category_owner_sees_what_they_can_do_where_and_why_without_role_codes()
    {
        using var app = DemoApp.Start();
        using var owner = await app.SignInAsync("catowner.packaging@a.example.test", followRedirects: true);
        var html = await BodyAsync(owner, "/Access");
        Assert.Contains("Create contracts", html);
        Assert.Contains("Packaging", html);                         // the categories they own, by name
        Assert.Contains("Why", html);
        Assert.DoesNotContain("CategoryOwner", html);               // a role code is never the explanation
        Assert.DoesNotContain("contracts.create", html);            // nor a permission code
        Assert.DoesNotContain("viewerKind", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("What you cannot do", html);
    }

    [Fact]
    public async Task A_read_only_person_is_told_what_they_cannot_do_and_what_kind_of_access_would_give_it()
    {
        using var app = DemoApp.Start();
        using var viewer = await app.SignInAsync("viewer.all@a.example.test", followRedirects: true);
        var html = await BodyAsync(viewer, "/Access");
        Assert.Contains("View contracts", html);
        Assert.Contains("Create contracts", html);                  // listed under what they cannot do
        Assert.Contains("Contract Manager", html);                  // ... with the kind of access that would give it
    }

    [Fact]
    public async Task An_administrator_sees_the_people_and_set_up_abilities_and_the_role_detail_is_still_available()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync("tenantadmin@a.example.test", followRedirects: true);
        var html = await BodyAsync(admin, "/Access");
        Assert.Contains("Add people and give them access", html);
        Assert.Contains("Roles, where they apply, and where each came from", html);
    }
}
