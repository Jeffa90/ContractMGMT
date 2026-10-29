using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>ACC-001/ACC-003: the new guided-administration pages pass the project's accessibility basics (one h1, language, labelled controls, captioned tables, named buttons and links). A person with a screen reader walking them through is still Not Verified.</summary>
[Collection("Api")]
public sealed class GuidedAdministrationAccessibilityTests
{
    private static async Task<string> PageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Every_new_page_passes_the_accessibility_basics_and_the_wizard_steps_name_their_progress()
    {
        using var app = DemoApp.Start();
        using var orgAdmin = await app.SignInAsync("orgadmin@a.example.test");
        var (tenantId, userId) = await app.FindUserAsync("orgadmin@a.example.test");
        var (_, viewerId) = await app.FindUserAsync("viewer.category@a.example.test");
        foreach (var url in new[] { "/", "/Access", "/Access/Setup", "/Admin", "/Admin/People", "/Admin/People/Access", $"/Admin/People/Access?Input.UserId={viewerId}&Step=1", $"/Admin/People/Person/{viewerId}", "/Admin/Categories" })
        {
            var html = await PageAsync(orgAdmin, url);
            Accessibility.AssertBasics(html, url);
        }
        var wizard = await PageAsync(orgAdmin, "/Admin/People/Access");
        Assert.Contains("Step 1 of", wizard);                                                    // progress is stated in words, not only drawn
        Assert.Contains("aria-current=\"step\"", wizard);
        _ = tenantId; _ = userId;

        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        Accessibility.AssertBasics(await PageAsync(tenantAdmin, "/Admin/People/Mappings"), "/Admin/People/Mappings");
    }
}
