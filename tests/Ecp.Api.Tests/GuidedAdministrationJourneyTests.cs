using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The first acceptance scenario of the guided-administration branch (docs/UX_ACCEPTANCE_CRITERIA.md, E2E-001): an administrator who cannot yet create anything gets operational access, adds a colleague as a
/// Contract Manager for one category, allows demonstration sign-in, previews as that person, checks what they see, and leaves the preview. No CSV, JSON, role code, viewer kind or group claim key is needed
/// anywhere along the way, and nothing on the pages names one.
/// </summary>
[Collection("Api")]
public sealed class GuidedAdministrationJourneyTests
{
    private static readonly string[] Jargon = ["CategoryOwner", "ProcurementAdministrator", "OrganisationAdministrator", "viewerKind", "ViewerKind", "groups claim", "contracts.create", "users.manage", "roles.delegate"];

    private static async Task<string> PageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        foreach (var word in Jargon) Assert.DoesNotContain(word, html);                           // no code, key or permission name on any page of the journey
        return html;
    }

    private static async Task<string> StepAsync(HttpClient client, string handler, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var response = await DemoApp.PostFormAsync(client, "/Admin/People/Access", fields, $"/Admin/People/Access?handler={handler}");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, $"{handler} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_first_administrator_gets_to_work_adds_a_colleague_and_checks_what_the_colleague_sees_without_any_code()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync("orgadmin@a.example.test", followRedirects: false);

        // 1. Home: the administrator can administer but not yet create. The page says what to do.
        var home = await PageAsync(admin, "/");
        Assert.DoesNotContain("href=\"/Contracts/Create\"", home);
        Assert.Contains("Set up my access", home);

        // 2. Set up my access: said first, then applied; no code needed.
        var setup = await PageAsync(admin, "/Access/Setup");
        Assert.Contains("This will give you", setup);
        var org = Regex.Match(setup, "name=\"OrganisationId\" value=\"([0-9a-f-]{36})\"").Groups[1].Value;
        var give = await DemoApp.PostFormAsync(admin, "/Access/Setup", new Dictionary<string, string> { ["OrganisationId"] = org, ["Reason"] = "Journey" }, "/Access/Setup?handler=Give");
        Assert.Equal(HttpStatusCode.Redirect, give.StatusCode);

        // 3. Effective access in plain English; + New contract is now on Home.
        Assert.Contains("Create contracts", await PageAsync(admin, "/Access"));
        Assert.Contains("href=\"/Contracts/Create\"", await PageAsync(admin, "/"));

        // 4. Add a colleague: the Contract Manager profile for one category, with demonstration sign-in.
        var first = await PageAsync(admin, "/Admin/People/Access");
        var email = $"journey.{Guid.NewGuid():N}"[..20] + "@a.example.test";
        var carried = new Dictionary<string, string> { ["Input.DisplayName"] = "Journey Jo", ["Input.Email"] = email, ["Input.AllowDemo"] = "true", ["Input.OrganisationId"] = org };
        Assert.Contains("What will this person do?", await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "1" }));
        Assert.Contains("Where can they work?", await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "2", ["Input.Profiles"] = "contract-manager" }));
        var ownership = await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "3", ["Input.Profiles"] = "contract-manager", ["Input.Scope"] = "WholeOrganisation" });
        var packaging = Regex.Match(ownership, "id=\"oid-([0-9a-f-]{36})\"[^>]*/><label for=\"oid-[0-9a-f-]{36}\">Packaging</label>").Groups[1].Value;
        Assert.NotEmpty(packaging);                                                              // chosen by name from a list, not typed
        var review = await StepAsync(admin, "Next", new Dictionary<string, string>(carried)
            { ["Step"] = "4", ["Input.Profiles"] = "contract-manager", ["Input.Scope"] = "WholeOrganisation", ["Input.Ownership"] = "Selected", ["Input.OwnedIds"] = packaging });
        review = await StepAsync(admin, "Next", new Dictionary<string, string>(carried)
            { ["Step"] = "5", ["Input.Profiles"] = "contract-manager", ["Input.Scope"] = "WholeOrganisation", ["Input.Ownership"] = "Selected", ["Input.OwnedIds"] = packaging, ["Input.Ceiling"] = "Internal" });
        Assert.Contains("Check before you save", review);
        Assert.Contains("Journey Jo", review);
        var saved = await DemoApp.PostFormAsync(admin, "/Admin/People/Access", new Dictionary<string, string>(carried)
            { ["Step"] = "7", ["Input.Profiles"] = "contract-manager", ["Input.Scope"] = "WholeOrganisation", ["Input.Ownership"] = "Selected", ["Input.OwnedIds"] = packaging, ["Input.Ceiling"] = "Internal" }, "/Admin/People/Access?handler=Save");
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var personUrl = saved.Headers.Location!.ToString();
        var person = await PageAsync(admin, personUrl);
        Assert.Contains("Journey Jo", person);
        Assert.Contains("Preview as this user", person);

        // 5. Preview as that person: their navigation, their records, read-only, with a banner.
        var preview = await DemoApp.PostFormAsync(admin, personUrl, new Dictionary<string, string> { ["reason"] = "Check what Jo sees" }, personUrl + "?handler=Preview");
        Assert.Equal(HttpStatusCode.Redirect, preview.StatusCode);
        var previewHome = await PageAsync(admin, "/");
        Assert.Contains("TEST MODE", previewHome); Assert.Contains("Previewing as", previewHome); Assert.Contains("Exit user preview", previewHome);
        Assert.Contains("href=\"/Contracts/Create\"", previewHome);                              // Jo owns Packaging, so Jo can start a contract
        Assert.DoesNotContain("admin-menu", previewHome);                                        // Jo administers nothing
        var register = await PageAsync(admin, "/Contracts");
        Assert.Contains("Synthetic Packaging Supply Agreement", register);
        Assert.DoesNotContain("Synthetic Energy Agreement", register);                           // above the Internal ceiling chosen for Jo, so hidden rather than refused
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/Admin/People")).StatusCode);
        Assert.Contains("Create contracts", await PageAsync(admin, "/Access"));

        // 6. Exit: back to the administrator, who is themselves again.
        // The Exit button must carry its own antiforgery token (a plain action attribute would not), so it is used exactly as a browser would use it.
        var bannerForm = Regex.Match(await PageAsync(admin, "/"), "<form[^>]*action=\"/Account/ExitPreview\"[^>]*>.*?</form>", RegexOptions.Singleline).Value;
        var bannerToken = DemoApp.ExtractToken(bannerForm);
        Assert.False(string.IsNullOrEmpty(bannerToken), "The Exit user preview form carries an antiforgery token.");
        var exit = await admin.PostAsync("/Account/ExitPreview", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = bannerToken! }));
        Assert.Equal(HttpStatusCode.Redirect, exit.StatusCode);
        var after = await PageAsync(admin, "/");
        Assert.DoesNotContain("Previewing as", after);
        Assert.Contains("admin-menu", after);

        // 7. Jo can really sign in through demonstration sign-in, and sees the same.
        using var jo = await app.SignInAsync(email, followRedirects: false);
        Assert.Contains("href=\"/Contracts/Create\"", await PageAsync(jo, "/"));
    }
}
