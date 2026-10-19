using System.Net;
using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// G-31 over real HTTP: Test mode with <c>Ecp:Governance:SecondPerson=Enforced</c> behaves like Production (the author cannot publish, a second person can, the screens say so, the audit events carry the mode);
/// Test with the default and Demo keep the simplified rule. The same scenarios run on SQLite and on real SQL Server.
/// </summary>
internal static class GovernanceScenarios
{
    private const string Author = "bizconfig@a.example.test";
    private const string Reviewer = "orgadmin@a.example.test";
    private const string SysAdmin = "sysadmin@installation.example.test";

    public static Action<Dictionary<string, string?>> Settings(string mode, string? governance) => s =>
    {
        s["Ecp:Mode"] = mode;
        if (governance is not null) s[GovernanceMode.SettingKey] = governance;
        s["Documents:Scanner:Provider"] = "mock";
    };

    private static async Task<Guid> OrgAsync(DemoApp app)
    {
        var (tenant, _) = await app.FindUserAsync(Reviewer);
        return await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single(o => o.Name == "Demo Procurement Group").Id);
    }

    private static Dictionary<string, string> Form(Guid org, string singular, string plural)
    {
        var form = new Dictionary<string, string> { ["OrganisationId"] = org.ToString(), ["Locale"] = "en", ["Note"] = "governance rehearsal" };
        for (var i = 0; i < TermCatalogue.All.Count; i++)
        {
            var key = TermCatalogue.All[i].Key;
            form[$"Rows[{i}].Key"] = key;
            if (key == "supplier") { form[$"Rows[{i}].Override"] = "true"; form[$"Rows[{i}].Singular"] = singular; form[$"Rows[{i}].Plural"] = plural; }
        }
        return form;
    }

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<List<(string Action, string? NewValues)>> AuditAsync(DemoApp app, Guid tenant)
        => await app.WithTenantAsync(tenant, async sp =>
        {
            var rows = await sp.GetRequiredService<IAuditEventSource>().Events.Where(e => e.Action.StartsWith("config.")).OrderBy(e => e.Sequence).Select(e => new { e.Action, e.NewValuesJson }).ToListAsync();
            return rows.Select(r => (r.Action, r.NewValuesJson)).ToList();
        });

    /// <summary>The author presses the publish button on the terminology form. Returns what the page says and whether the wording is in force.</summary>
    private static async Task<(string Flash, bool InForce, Guid Tenant)> AuthorPublishesAsync(DemoApp app, string wording)
    {
        var org = await OrgAsync(app);
        var (tenant, _) = await app.FindUserAsync(Reviewer);
        using var author = await app.SignInAsync(Author);
        using var viewer = await app.SignInAsync("viewer.all@a.example.test", followRedirects: true);
        var form = Form(org, wording, wording + "s");
        var edit = $"/Admin/Terminology/Edit?organisationId={org}&locale=en";
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(author, edit, form, $"/Admin/Terminology/Edit?handler=Validate&organisationId={org}&locale=en")).StatusCode);
        var post = await DemoApp.PostFormAsync(author, edit, form, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var flash = await BodyAsync(author, post.Headers.Location!.OriginalString);
        var inForce = (await BodyAsync(viewer, "/Suppliers")).Contains($"<h1>{wording}s</h1>");
        return (flash, inForce, tenant);
    }

    public static async Task Enforced_test_behaves_like_production(DemoApp app)
    {
        var (flash, inForce, tenant) = await AuthorPublishesAsync(app, "Partner");
        Assert.Contains("Submitted for review", flash);
        Assert.False(inForce, "Nothing may be in force until a second person publishes.");

        var org = await OrgAsync(app);
        using var author = await app.SignInAsync(Author);
        using var reviewer = await app.SignInAsync(Reviewer);
        using var viewer = await app.SignInAsync("viewer.all@a.example.test", followRedirects: true);
        var queue = await BodyAsync(author, "/Admin/Reviews?show=mine");
        var id = Regex.Match(queue, "/Admin/Reviews/Details\\?id=([0-9a-f-]{36})").Groups[1].Value;
        Assert.NotEmpty(id);
        Assert.DoesNotContain("Approve and publish", await BodyAsync(author, $"/Admin/Reviews/Details?id={id}"));
        var refused = await DemoApp.PostFormAsync(author, $"/Admin/Reviews/Details?id={id}", new Dictionary<string, string> { ["Id"] = id, ["Note"] = "mine" }, $"/Admin/Reviews/Details?handler=Publish&id={id}");
        Assert.Contains("second person", await refused.Content.ReadAsStringAsync());
        var publish = await DemoApp.PostFormAsync(reviewer, $"/Admin/Reviews/Details?id={id}", new Dictionary<string, string> { ["Id"] = id, ["Note"] = "Agreed" }, $"/Admin/Reviews/Details?handler=Publish&id={id}");
        Assert.Equal(HttpStatusCode.Redirect, publish.StatusCode);
        Assert.Contains("<h1>Partners</h1>", await BodyAsync(viewer, "/Suppliers"));

        var audit = await AuditAsync(app, tenant);
        Assert.Contains(audit, a => a.Action == "config.draft.submitted" && a.NewValues!.Contains("\"governanceMode\":\"TestEnforced\""));
        Assert.Contains(audit, a => a.Action == "config.published" && a.NewValues!.Contains("\"governanceMode\":\"TestEnforced\""));
        Assert.Contains(audit, a => a.Action == "config.publish.refused" && a.NewValues!.Contains("TestEnforced"));
        await ShowsGovernanceAsync(app, "TestEnforced", "second-person rule is in force");
    }

    public static async Task Default_test_keeps_the_simplified_rule(DemoApp app)
    {
        var (flash, inForce, tenant) = await AuthorPublishesAsync(app, "Partner");
        Assert.DoesNotContain("Submitted for review", flash);
        Assert.True(inForce, "Under simplified governance the author's publish takes effect at once.");
        var audit = await AuditAsync(app, tenant);
        Assert.Contains(audit, a => a.Action == "config.published" && a.NewValues!.Contains("\"governanceMode\":\"TestSimplified\""));
        await ShowsGovernanceAsync(app, "TestSimplified", "NOT in force");
    }

    public static async Task Demo_ignores_the_setting(DemoApp app)
    {
        var (flash, inForce, _) = await AuthorPublishesAsync(app, "Partner");
        Assert.DoesNotContain("Submitted for review", flash);
        Assert.True(inForce);
        await ShowsGovernanceAsync(app, "DemoSimplified", "NOT in force");
    }

    /// <summary>Health, the operations dashboard and the setup report all state the governance mode.</summary>
    private static async Task ShowsGovernanceAsync(DemoApp app, string name, string phrase)
    {
        using var sys = await app.SignInAsync(SysAdmin);
        var health = await BodyAsync(sys, "/Admin/Health");
        Assert.Contains("R-GOV-01", health);
        Assert.Contains(name, health);
        Assert.Contains(phrase, health);
        var dashboard = await BodyAsync(sys, "/Admin/Operations/Dashboard");
        Assert.Contains("Governance", dashboard);
        Assert.Contains(name, dashboard);
        Assert.Equal(name, app.Factory.Services.GetRequiredService<GovernanceMode>().Name);
    }
}

[Collection("Api")]
[Trait("Category", "Governance")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class GovernanceModeUiTests
{
    [Fact] public async Task Test_with_enforcement_behaves_like_Production_end_to_end() { using var app = DemoApp.Start(GovernanceScenarios.Settings("Test", "Enforced")); await GovernanceScenarios.Enforced_test_behaves_like_production(app); }
    [Fact] public async Task Test_with_an_unrecognised_value_fails_safe_to_enforcement() { using var app = DemoApp.Start(GovernanceScenarios.Settings("Test", "maybe")); await GovernanceScenarios.Enforced_test_behaves_like_production(app); }
    [Fact] public async Task Test_by_default_keeps_simplified_governance_and_says_so() { using var app = DemoApp.Start(GovernanceScenarios.Settings("Test", null)); await GovernanceScenarios.Default_test_keeps_the_simplified_rule(app); }
    [Fact] public async Task Demo_ignores_the_enforcement_setting() { using var app = DemoApp.Start(GovernanceScenarios.Settings("Demo", "Enforced")); await GovernanceScenarios.Demo_ignores_the_setting(app); }
}

[Collection("Api")]
[Trait("Category", "SqlServer")]
public sealed class GovernanceModeSqlServerTests
{
    [SqlServerFact] public async Task Test_with_enforcement_behaves_like_Production_on_real_SQL_Server() { using var app = DemoApp.StartSqlServer(GovernanceScenarios.Settings("Test", "Enforced")); await GovernanceScenarios.Enforced_test_behaves_like_production(app); }
    [SqlServerFact] public async Task Test_by_default_keeps_simplified_governance_on_real_SQL_Server() { using var app = DemoApp.StartSqlServer(GovernanceScenarios.Settings("Test", null)); await GovernanceScenarios.Default_test_keeps_the_simplified_rule(app); }
}
