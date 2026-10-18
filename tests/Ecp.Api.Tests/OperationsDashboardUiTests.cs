using System.Net;
using System.Text.Json;
using Ecp.Host;
using Ecp.Operations;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>OPS-03 through the real host: who may open the dashboard, that it speaks in four distinct states and never calls Not Verified a pass, that its machine-readable form is clean, and what the anonymous health endpoint may say.</summary>
[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class OperationsDashboardUiTests
{
    private const string Page = "/Admin/Operations/Dashboard";

    [Fact]
    public async Task Only_an_installation_administrator_may_open_the_dashboard_or_its_json_and_there_is_no_menu_entry_for_anyone_else()
    {
        using var app = DemoApp.Start();
        using var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(Page)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(Page + "?handler=Json")).StatusCode);
        foreach (var email in new[] { "tenantadmin@a.example.test", "orgadmin@a.example.test", "procadmin@a.example.test", "legal@a.example.test", "auditor@a.example.test", "viewer.category@a.example.test" })
        {
            using var client = await app.SignInAsync(email);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Page)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Page + "?handler=Json")).StatusCode);
            Assert.DoesNotContain("/Admin/Operations", await client.GetStringAsync("/"));
        }
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        Assert.Equal(HttpStatusCode.OK, (await sysadmin.GetAsync(Page)).StatusCode);
        Assert.Contains(Page, await sysadmin.GetStringAsync("/Admin/Operations"));         // reachable from the Operations page
    }

    [Fact]
    public async Task The_page_names_four_distinct_states_marks_every_panel_and_says_plainly_what_it_could_not_check()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        var html = await sysadmin.GetStringAsync(Page);
        Assert.DoesNotContain("   at ", html);
        Assert.Contains("<h1>Operations dashboard</h1>", html);
        foreach (var panel in new[] { "Installation health", "Readiness catalogue", "Platform and schema versions", "Upgrade readiness and migrations", "Backup and restore", "Background jobs and leases", "Notification outbox", "Packs and configuration", "Document store and audit chain", "Certificates and secret expiry" })
            Assert.Contains(panel, html);
        // Not Verified is written as such, in words (not by colour alone), and the page does not describe itself as a pass or an approval.
        Assert.Contains(">Not Verified<", html);
        Assert.Contains("never a pass", html);
        Assert.Contains("not an approval to go live", html);
        Assert.Contains("TLS certificate", html); Assert.Contains("Secret expiry", html);
        // Accessibility: every table has a caption and column headers, every panel is a labelled region, the sections are reachable by id.
        Assert.Equal(System.Text.RegularExpressions.Regex.Matches(html, "<table").Count, System.Text.RegularExpressions.Regex.Matches(html, "<caption").Count);
        Assert.Contains("aria-labelledby=\"h-health\"", html); Assert.Contains("id=\"jobs\"", html); Assert.Contains("id=\"outbox\"", html);
        Assert.Contains("scope=\"col\"", html); Assert.Contains("scope=\"row\"", html);
    }

    [Fact]
    public async Task The_json_form_agrees_with_the_page_never_carries_not_verified_as_verified_and_holds_no_secret()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        var response = await sysadmin.GetAsync(Page + "?handler=Json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.False(Redaction.ContainsSecret(text));
        Assert.DoesNotContain("Data Source=", text); Assert.DoesNotContain("file:ecp_api_", text);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("correlationId").GetString()));
        Assert.Contains(root.GetProperty("overall").GetString(), new[] { "Failed", "Not Verified", "Warning" });       // never "Verified" while anything is unverified
        var allowed = new HashSet<string> { "Verified", "Warning", "Failed", "Not Verified" };
        foreach (var panel in root.GetProperty("panels").EnumerateArray())
            foreach (var item in panel.GetProperty("items").EnumerateArray())
            {
                var status = item.GetProperty("status").GetString()!;
                Assert.Contains(status, allowed);
                var explanation = item.GetProperty("explanation").GetString()!;
                if (explanation.Contains("has not been built") || explanation.Contains("not run from the dashboard") || explanation.Contains("does not inspect") || explanation.Contains("carry no expiry")) Assert.Equal("Not Verified", status);
            }
        var catalogue = root.GetProperty("coverage");
        Assert.Equal(catalogue.GetProperty("total").GetInt32(), catalogue.GetProperty("runHere").GetInt32() + catalogue.GetProperty("runElsewhere").GetInt32() + catalogue.GetProperty("notBuilt").GetInt32());
    }

    [Fact]
    public async Task During_maintenance_the_dashboard_stays_open_and_shows_why_changes_are_refused()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        var enter = await DemoApp.PostFormAsync(sysadmin, "/Admin/Operations/Maintenance", [new("Reason", "Dashboard test")], "/Admin/Operations/Maintenance?handler=Enter");
        Assert.Equal(HttpStatusCode.Redirect, enter.StatusCode);
        var html = await sysadmin.GetStringAsync(Page);
        Assert.Contains("Maintenance mode", html); Assert.Contains("Manual", html);
        Assert.Contains("Changes are refused", html);
        Assert.Equal(HttpStatusCode.OK, (await sysadmin.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public void Anonymous_readiness_in_Production_says_only_the_overall_status_and_in_demo_and_test_lists_checks_as_documented()
    {
        var report = new ReadinessReport(ReadinessStatus.Degraded, [new ReadinessResult("R-DB-01", "Database", ReadinessStatus.Degraded, "Pending migrations for: Contracts, Operations.")]);
        var production = JsonSerializer.Serialize(HealthBodies.Ready(report, EcpMode.Production));
        Assert.Equal("{\"status\":\"Degraded\"}", production);
        Assert.DoesNotContain("R-DB-01", production); Assert.DoesNotContain("Contracts", production);
        foreach (var mode in new[] { EcpMode.Demo, EcpMode.Test })
        {
            var open = JsonSerializer.Serialize(HealthBodies.Ready(report, mode));
            Assert.Contains("R-DB-01", open);                                                // deliberate and documented: Demo and Test are not internet-facing
        }
    }
}
