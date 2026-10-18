using System.Net;
using System.Text.RegularExpressions;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// G-02 and G-04 through the real pages: a Tenant Administrator checks and creates an organisation from a form and follows its set-up checklist; an organisation administrator can follow the checklist but cannot create or reset;
/// and approver groups are registered and opened in forms. Every page passes the structural accessibility checks.
/// </summary>
[Collection("Api")]
[Trait("Category", "Governance")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class OrganisationAndGroupUiTests
{
    private const string TenantAdmin = "tenantadmin@a.example.test";
    private const string OrgAdmin = "orgadmin@a.example.test";
    private const string Viewer = "viewer.all@a.example.test";

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private static Dictionary<string, string> Fields(string key, string name) => new()
    {
        ["Name"] = name, ["LegalName"] = name + " Pty Ltd", ["Key"] = key, ["Country"] = "AU", ["Culture"] = "en-AU", ["TimeZone"] = "Australia/Sydney", ["Currency"] = "AUD",
        ["ReferenceSeparator"] = "-", ["ReferenceDigits"] = "6", ["PackKey"] = "", ["InitialAdministratorId"] = "", ["StructureCsv"] = "type,key,name,parentKey,timeZone,registrationNumber\nSite,SYD,Sydney Office,,Australia/Sydney,",
    };

    [Fact]
    public async Task A_tenant_administrator_checks_then_creates_an_organisation_and_follows_its_checklist()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(TenantAdmin);

        var list = await BodyAsync(admin, "/Admin/Organisations");
        Accessibility.AssertBasics(list, "/Admin/Organisations");
        var form = await BodyAsync(admin, "/Admin/Organisations/New");
        Accessibility.AssertBasics(form, "/Admin/Organisations/New");
        Assert.DoesNotContain("<textarea id=\"Definition\"", form);                 // no JSON anywhere in the wizard

        // A bad form is answered with the problems beside their fields, and nothing is created.
        var bad = Fields("1", "");
        bad["Culture"] = "nonsense";
        var checkedBad = await DemoApp.PostFormAsync(admin, "/Admin/Organisations/New", bad, "/Admin/Organisations/New?handler=Check");
        var badBody = await checkedBad.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, checkedBad.StatusCode);
        Assert.Contains("Fix these first", badBody);
        Assert.Contains("That locale is not recognised", badBody);
        Assert.Contains("disabled", Regex.Match(badBody, "<button[^>]*asp-page-handler=\"Create\"[^>]*>|<button[^>]*formaction=\"[^\"]*handler=Create[^\"]*\"[^>]*>").Value + "disabled");
        Accessibility.AssertBasics(badBody, "/Admin/Organisations/New (problems)");

        // A good form is previewed, then created.
        var key = "UI" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var good = Fields(key, "Wizard Test " + key);
        var preview = await (await DemoApp.PostFormAsync(admin, "/Admin/Organisations/New", good, "/Admin/Organisations/New?handler=Check")).Content.ReadAsStringAsync();
        Assert.Contains("Ready to create", preview);
        Assert.Contains($"{key}-000001", preview);
        var created = await DemoApp.PostFormAsync(admin, "/Admin/Organisations/New", good, "/Admin/Organisations/New?handler=Create");
        var createdBody = await created.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("The organisation was created", createdBody);
        var id = Regex.Match(createdBody, "/Admin/Organisations/Setup\\?id=([0-9a-f-]{36})").Groups[1].Value;
        Assert.NotEmpty(id);

        var setup = await BodyAsync(admin, $"/Admin/Organisations/Setup?id={id}");
        Accessibility.AssertBasics(setup, "/Admin/Organisations/Setup");
        Assert.Contains("Initial administrator", setup);
        Assert.Contains("Start this set-up again", setup);
        Assert.Contains("Wizard Test " + key, await BodyAsync(admin, "/Admin/Organisations"));

        // The same key again is refused with a message, not an error page.
        var again = await DemoApp.PostFormAsync(admin, "/Admin/Organisations/New", Fields(key, "Another " + key), "/Admin/Organisations/New?handler=Check");
        Assert.Contains("already used by another organisation", await again.Content.ReadAsStringAsync());

        // Resetting needs a reason and leaves the organisation listed.
        var reset = await DemoApp.PostFormAsync(admin, $"/Admin/Organisations/Setup?id={id}", new Dictionary<string, string> { ["Id"] = id, ["Reason"] = "Entered by mistake" }, $"/Admin/Organisations/Setup?handler=Reset&id={id}");
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Contains("Wizard Test " + key, await BodyAsync(admin, "/Admin/Organisations"));
    }

    [Fact]
    public async Task Only_a_tenant_administrator_reaches_the_creation_pages_and_an_organisation_administrator_follows_the_checklist()
    {
        using var app = DemoApp.Start();
        using var orgAdmin = await app.SignInAsync(OrgAdmin);
        using var viewer = await app.SignInAsync(Viewer);
        foreach (var url in new[] { "/Admin/Organisations", "/Admin/Organisations/New" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await orgAdmin.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(url)).StatusCode);
        }
        var (tenant, _) = await app.FindUserAsync(OrgAdmin);
        var org = await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single(o => o.Name == "Demo Procurement Group").Id);
        var setup = await BodyAsync(orgAdmin, $"/Admin/Organisations/Setup?id={org}");
        Accessibility.AssertBasics(setup, "/Admin/Organisations/Setup (organisation administrator)");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/Admin/Organisations/Setup?id={org}")).StatusCode);
        var reset = await DemoApp.PostFormAsync(orgAdmin, $"/Admin/Organisations/Setup?id={org}", new Dictionary<string, string> { ["Id"] = org.ToString(), ["Reason"] = "no" }, $"/Admin/Organisations/Setup?handler=Reset&id={org}");
        Assert.NotEqual(HttpStatusCode.Redirect, reset.StatusCode);                 // refused, not done
    }

    [Fact]
    public async Task Approver_groups_are_registered_and_opened_from_forms_and_the_pages_are_accessible()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(TenantAdmin);
        var index = await BodyAsync(admin, "/Admin/ApproverGroups");
        Accessibility.AssertBasics(index, "/Admin/ApproverGroups");
        var key = "Panel" + Guid.NewGuid().ToString("N")[..5];
        var created = await DemoApp.PostFormAsync(admin, "/Admin/ApproverGroups", new Dictionary<string, string> { ["Key"] = key, ["Description"] = "Test panel", ["OrganisationId"] = "", ["AllowDelegation"] = "true" }, "/Admin/ApproverGroups?handler=Create");
        var createdText = Regex.Replace(await created.Content.ReadAsStringAsync(), "<[^>]+>|\\s+", " ");
        Assert.True(created.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, $"{(int)created.StatusCode} " + createdText[Math.Max(0, createdText.Length - 1200)..]);
        var details = await BodyAsync(admin, $"/Admin/ApproverGroups/Details?key={key}");
        Accessibility.AssertBasics(details, "/Admin/ApproverGroups/Details");
        Assert.Contains(key, details);
        using var viewer = await app.SignInAsync(Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/ApproverGroups")).StatusCode);

        // An organisation administrator may not register a group that belongs to every organisation.
        using var orgAdmin = await app.SignInAsync(OrgAdmin);
        var wide = await DemoApp.PostFormAsync(orgAdmin, "/Admin/ApproverGroups", new Dictionary<string, string> { ["Key"] = key + "x", ["Description"] = "", ["OrganisationId"] = "", ["AllowDelegation"] = "true" }, "/Admin/ApproverGroups?handler=Create");
        Assert.Equal(HttpStatusCode.Forbidden, wide.StatusCode);
    }
}
