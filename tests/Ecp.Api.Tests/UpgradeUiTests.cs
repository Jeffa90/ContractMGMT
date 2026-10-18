using System.IO.Compression;
using System.Net;
using System.Text;
using Ecp.Host;
using Ecp.Operations;
using Ecp.Recovery;
using Microsoft.AspNetCore.Http;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>Administration, Operations, upgrade pages through the real host (OPS-02): who may open them, what they say on a database that cannot be upgraded, and that nothing on them downgrades.</summary>
[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class UpgradeUiTests
{
    private static readonly string[] Pages =
    [
        "/Admin/Operations/UpgradeReadiness", "/Admin/Operations/UpgradePreview", "/Admin/Operations/MigrationReport", "/Admin/Operations/UpgradeHistory", "/Admin/Operations/PostUpgradeValidation", "/Admin/Operations/RollbackEvidence"
    ];

    [Theory]
    [InlineData("/Admin/Operations/UpgradeReadiness", true)]
    [InlineData("/Admin/Operations/UpgradePreview", true)]
    [InlineData("/Admin/Operations/RollbackEvidence", true)]
    public void The_upgrade_pages_stay_open_while_the_installation_is_in_maintenance_because_an_upgrade_is_done_in_maintenance(string path, bool allowed)
        => Assert.Equal(allowed, MaintenanceMiddleware.IsAllowedDuringMaintenance(new PathString(path)));

    [Fact]
    public async Task Only_an_installation_administrator_may_open_the_upgrade_pages_and_everyone_else_is_refused_before_anything_is_shown()
    {
        using var app = DemoApp.Start();
        using var anonymous = app.NewClient();
        foreach (var page in Pages) Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(page)).StatusCode);

        foreach (var email in new[] { "tenantadmin@a.example.test", "orgadmin@a.example.test", "legal@a.example.test", "auditor@a.example.test", "viewer.category@a.example.test" })
        {
            using var client = await app.SignInAsync(email);
            foreach (var page in Pages) Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(page)).StatusCode);
            foreach (var url in new[] { "/Admin/Operations/UpgradeReadiness?handler=Json", "/Admin/Operations/UpgradeReadiness?handler=Csv", "/Admin/Operations/MigrationReport?handler=Script&module=Contracts", "/Admin/Operations/UpgradeHistory?handler=Evidence&name=x.json" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        }
        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        var token = DemoApp.ExtractToken(await tenantAdmin.GetStringAsync("/")) ?? "";
        foreach (var url in new[] { "/Admin/Operations/UpgradePreview?handler=Apply", "/Admin/Operations/UpgradePreview?handler=Rehearse", "/Admin/Operations/RollbackEvidence?handler=Prove", "/Admin/Operations/UpgradeReadiness?handler=Run" })
        {
            var response = await tenantAdmin.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["Reason"] = "x", ["__RequestVerificationToken"] = token }));
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, url + " " + response.StatusCode);
        }
        await using var scope = app.Factory.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().GetAsync()).Enabled);
    }

    [Fact]
    public async Task On_a_database_that_cannot_be_upgraded_the_pages_say_blocked_or_not_verified_plainly_offer_no_downgrade_and_show_no_stack_trace()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        foreach (var page in Pages)
        {
            var response = await sysadmin.GetAsync(page);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("   at ", html);
            Assert.DoesNotContain("Data Source=", html);
            Assert.DoesNotMatch("(?i)downgrade\\s*(button|now)|>\\s*Downgrade\\s*<|>\\s*Revert\\s*<", html);
        }
        var readiness = await sysadmin.GetStringAsync("/Admin/Operations/UpgradeReadiness");
        Assert.Contains("Upgrade readiness", readiness);
        Assert.Contains("is not an approval to upgrade or to go live", readiness);
        var run = await DemoApp.PostFormAsync(sysadmin, "/Admin/Operations/UpgradeReadiness", [], "/Admin/Operations/UpgradeReadiness?handler=Run");
        var body = await run.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.Contains("Blocked", body);
        Assert.DoesNotContain("   at ", body);
        // The only things offered on rollback are evidence of a restore; rolling back is described as restoring a backup.
        var rollback = await sysadmin.GetStringAsync("/Admin/Operations/RollbackEvidence");
        Assert.Contains("restor", rollback.ToLowerInvariant());
        Assert.Contains("data", rollback.ToLowerInvariant());
        // The evidence file is fetched by exact name only.
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.GetAsync("/Admin/Operations/UpgradeHistory?handler=Evidence&name=..%2F..%2Fetc%2Fpasswd")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.GetAsync("/Admin/Operations/UpgradeHistory?handler=Evidence&name=missing.json")).StatusCode);

        // Applying is refused in plain words: no backup exists and the provider cannot be upgraded here.
        var apply = await DemoApp.PostFormAsync(sysadmin, "/Admin/Operations/UpgradePreview", [new("BackupId", "bk-20261017T000000Z-00000000"), new("Reason", "rehearsal"), new("Confirmation", "UPGRADE 1.0.0 TO 1.1.0")], "/Admin/Operations/UpgradePreview?handler=Apply");
        var applyBody = await apply.Content.ReadAsStringAsync();
        Assert.True(apply.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity, apply.StatusCode.ToString());
        Assert.DoesNotContain("   at ", applyBody);
    }

    [Fact]
    public async Task The_pre_flight_and_migration_downloads_work_for_those_who_may_hold_no_secret_and_a_spreadsheet_cannot_run_them()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        var json = await sysadmin.GetAsync("/Admin/Operations/UpgradeReadiness?handler=Json");
        Assert.Equal(HttpStatusCode.OK, json.StatusCode); Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
        var jsonText = await json.Content.ReadAsStringAsync();
        Assert.False(Redaction.ContainsSecret(jsonText));
        Assert.DoesNotContain("Data Source=", jsonText);
        Assert.Contains("Blocked", jsonText);
        var csv = await sysadmin.GetAsync("/Admin/Operations/UpgradeReadiness?handler=Csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var csvText = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        foreach (var line in csvText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)) foreach (var cell in line.Split(',')) Assert.False(cell.Length > 0 && cell.TrimStart('"')[0] is '=' or '+' or '@' or '\t');
        // A pending-migration script is generated for SQL Server only: here the page says so in words, not a file and not an error page.
        var script = await sysadmin.GetAsync("/Admin/Operations/MigrationReport?handler=Script&module=Operations");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/html", script.Content.Headers.ContentType!.MediaType);
        var scriptBody = await script.Content.ReadAsStringAsync();
        Assert.Contains("SQL Server", scriptBody); Assert.DoesNotContain("   at ", scriptBody);
        var hostile = await sysadmin.GetAsync("/Admin/Operations/MigrationReport?handler=Script&module=%27%3B%20DROP%20TABLE%20x%3B--");
        Assert.NotEqual("text/plain", hostile.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("DROP TABLE x;--", await hostile.Content.ReadAsStringAsync());
    }
}

/// <summary>The upgrade commands (docs/UPGRADE_RUNBOOK.md) over the real host composition.</summary>
[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class UpgradeCommandLineTests
{
    private static async Task<(int Code, string Output, string Error)> RunAsync(DemoApp app, string command, params string[] arguments)
    {
        var originalOut = Console.Out; var originalError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        Console.SetOut(output); Console.SetError(error);
        try { return (await UpgradeCli.RunAsync(command, [command, .. arguments], app.Factory.Services), output.ToString(), error.ToString()); }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    [Fact]
    public async Task The_upgrade_verbs_verify_the_manifest_refuse_what_cannot_be_done_here_and_never_print_a_secret()
    {
        using var app = DemoApp.Start();
        var verify = await RunAsync(app, "upgrade-manifest-verify");
        Assert.Equal(0, verify.Code);
        Assert.Contains("valid for this build", verify.Output);

        // Pre-flight on a database that cannot be upgraded exits non-zero and says why.
        var preflight = await RunAsync(app, "upgrade-preflight");
        Assert.NotEqual(0, preflight.Code);
        Assert.Contains("Blocked", preflight.Output + preflight.Error);

        // Applying needs the exact words, a reason and a backup; none of it is guessed.
        var apply = await RunAsync(app, "upgrade-apply", "bk-20261017T000000Z-00000000", "--reason", "rehearsal", "--confirm", "wrong words");
        Assert.NotEqual(0, apply.Code);
        var rehearse = await RunAsync(app, "upgrade-rehearse", "bk-20261017T000000Z-00000000", "ecp_target", "--reason", "rehearsal", "--confirm", "wrong words");
        Assert.NotEqual(0, rehearse.Code);
        Assert.Equal(64, (await RunAsync(app, "upgrade-apply")).Code);                      // not enough arguments: usage

        var report = await RunAsync(app, "migration-report");
        Assert.Equal(0, report.Code);
        Assert.Contains("Operations", report.Output);
        Assert.Equal(0, (await RunAsync(app, "upgrade-history")).Code);

        Assert.DoesNotContain("file:ecp_api_", verify.Output + verify.Error + preflight.Output + preflight.Error + apply.Output + apply.Error + report.Output);
        Assert.False(Redaction.ContainsSecret(preflight.Output + report.Output));
    }
}
