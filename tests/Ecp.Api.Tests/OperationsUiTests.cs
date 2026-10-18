using System.IO.Compression;
using System.Net;
using System.Text;
using Ecp.Host;
using Ecp.Notifications.Persistence;
using Ecp.Operations;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>Administration, Operations through the real host: who may open each page, maintenance mode as the host enforces it, and the downloads.</summary>
[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class OperationsUiTests
{
    private static readonly string[] Pages =
    [
        "/Admin/Operations", "/Admin/Operations/Backups", "/Admin/Operations/Restore", "/Admin/Operations/Validation", "/Admin/Operations/Maintenance", "/Admin/Operations/Leases", "/Admin/Operations/Evidence",
        "/Admin/Operations/Backups/bk-20261017T000000Z-00000000"
    ];

    [Theory]
    [InlineData("/health/ready", true)]
    [InlineData("/health/live", true)]
    [InlineData("/Admin/Operations", true)]
    [InlineData("/admin/operations/Maintenance", true)]
    [InlineData("/SignIn", true)]
    [InlineData("/Reauth", true)]
    [InlineData("/Contracts", false)]
    [InlineData("/api/v1/contracts", false)]
    [InlineData("/Admin/OperationsExtra", false)]
    [InlineData("//Admin/Operations", false)]
    [InlineData("/Admin/Users", false)]
    [InlineData("/Setup", false)]
    public void Only_health_sign_in_and_the_operations_pages_are_open_while_the_installation_is_in_maintenance(string path, bool allowed)
        => Assert.Equal(allowed, MaintenanceMiddleware.IsAllowedDuringMaintenance(new PathString(path)));

    [Fact]
    public async Task Only_an_installation_administrator_may_open_the_operations_pages_and_everyone_else_is_refused_before_anything_is_shown()
    {
        using var app = DemoApp.Start();
        using var anonymous = app.NewClient();
        foreach (var page in Pages) Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(page)).StatusCode);

        foreach (var email in new[] { "tenantadmin@a.example.test", "orgadmin@a.example.test", "procadmin@a.example.test", "legal@a.example.test", "auditor@a.example.test", "viewer.category@a.example.test", "catowner.packaging@a.example.test" })
        {
            using var client = await app.SignInAsync(email);
            foreach (var page in Pages) Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(page)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Operations/Evidence?handler=Json")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Operations/Evidence?handler=Csv")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Operations/Evidence?handler=Bundle")).StatusCode);
            var page0 = await client.GetStringAsync("/");
            Assert.DoesNotContain("/Admin/Operations", page0);                                              // not even a menu entry
        }
        // Posting to them does not work either: a business administrator cannot restore or switch maintenance mode on.
        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        var token = DemoApp.ExtractToken(await tenantAdmin.GetStringAsync("/")) ?? "";
        foreach (var url in new[] { "/Admin/Operations/Maintenance?handler=Enter", "/Admin/Operations/Restore?handler=Start", "/Admin/Operations/Backups?handler=Start", "/Admin/Operations/Leases?handler=TakeOver" })
        {
            var response = await tenantAdmin.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["Reason"] = "x", ["__RequestVerificationToken"] = token }));
            Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, url + " " + response.StatusCode);
        }
        await using var scope = app.Factory.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        Assert.False((await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().GetAsync()).Enabled);
    }

    [Fact]
    public async Task An_installation_administrator_sees_every_operations_page_and_a_database_without_a_supported_backup_says_so_plainly()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        Assert.Contains("Operations", await sysadmin.GetStringAsync("/"));                                    // a menu entry for those who may use it
        var overview = await sysadmin.GetStringAsync("/Admin/Operations");
        Assert.Contains("Recovery readiness", overview); Assert.Contains("not the same as having a recovery plan", overview);
        Assert.Contains("No supported backup", overview);
        Assert.DoesNotContain("   at ", overview);
        foreach (var page in Pages.Where(p => !p.Contains("/Backups/bk-"))) Assert.Equal(HttpStatusCode.OK, (await sysadmin.GetAsync(page)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.GetAsync("/Admin/Operations/Backups/bk-20261017T000000Z-00000000")).StatusCode);       // an unknown backup is not found, never an error page
        Assert.Equal(HttpStatusCode.NotFound, (await sysadmin.GetAsync("/Admin/Operations/Backups/..%2F..%2Fetc")).StatusCode);
        var backups = await sysadmin.GetStringAsync("/Admin/Operations/Backups");
        Assert.Contains("Backup and restore need SQL Server", backups);
        Assert.Contains("deployment models", (await sysadmin.GetStringAsync("/Admin/Operations/Evidence")).ToLowerInvariant());
        Assert.Contains("Not Verified", await sysadmin.GetStringAsync("/Admin/Operations/Evidence"));

        // Starting a backup here is refused in plain words, with an error summary and no stack trace.
        var started = await DemoApp.PostFormAsync(sysadmin, "/Admin/Operations/Backups", [new("Reason", "rehearsal")], "/Admin/Operations/Backups?handler=Start");
        var body = await started.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.Contains("There is a problem", body); Assert.Contains("Backup and restore need SQL Server", body);
        Assert.DoesNotContain("   at ", body);
    }

    [Fact]
    public async Task Maintenance_mode_refuses_changes_with_a_clear_answer_keeps_reading_health_and_the_operations_pages_open_and_shows_a_banner()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        using var tenantAdmin = await app.SignInAsync("tenantadmin@a.example.test");
        var token = DemoApp.ExtractToken(await tenantAdmin.GetStringAsync("/")) ?? "";
        async Task<HttpResponseMessage> TryChange(HttpClient client, string url)
            => await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        // Before: nothing special.
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, (await TryChange(tenantAdmin, "/Admin/Categories")).StatusCode);
        Assert.DoesNotContain("The system is in maintenance", await tenantAdmin.GetStringAsync("/"));

        // The administrator enters maintenance mode with a reason (their sign-in is recent).
        var enter = await DemoApp.PostFormAsync(sysadmin, "/Admin/Operations/Maintenance", [new("Reason", "Planned restore rehearsal")], "/Admin/Operations/Maintenance?handler=Enter");
        Assert.Equal(HttpStatusCode.Redirect, enter.StatusCode);

        // Now: a change is refused with 503 and a Retry-After, whatever the route, and the answer does not reveal why or how.
        foreach (var url in new[] { "/Admin/Categories", "/Contracts", "/Onboarding", "/Admin/Users", "/api/v1/anything", "/Admin/OperationsExtra", "//Admin/Operations/Maintenance" })
        {
            var refused = await TryChange(tenantAdmin, url);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.True(refused.Headers.RetryAfter is not null, url);
            var text = await refused.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Planned restore rehearsal", text); Assert.DoesNotContain("   at ", text); Assert.DoesNotContain("Operations", text);
            Assert.Contains("maintenance", text.ToLowerInvariant());
        }
        var api = await TryChange(tenantAdmin, "/api/v1/contracts");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
        Assert.Contains("maintenance_mode", await api.Content.ReadAsStringAsync());
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await tenantAdmin.SendAsync(new HttpRequestMessage(method, "/api/v1/contracts/" + Guid.NewGuid()))).StatusCode);

        // Reading, health and the operations pages still work, and every page carries the banner.
        Assert.Equal(HttpStatusCode.OK, (await tenantAdmin.GetAsync("/Notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenantAdmin.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tenantAdmin.GetAsync("/health/live")).StatusCode);
        var home = await tenantAdmin.GetStringAsync("/");
        Assert.Contains("The system is in maintenance.", home); Assert.Contains("Changes are paused", home);
        Assert.DoesNotContain("Planned restore rehearsal", home);          // the reason is for operators, not for everyone
        var maintenancePage = await sysadmin.GetStringAsync("/Admin/Operations/Maintenance");
        Assert.Contains("Planned restore rehearsal", maintenancePage); Assert.Contains("Leave maintenance mode", maintenancePage);

        // Background jobs take no new work while it is on (and resume afterwards).
        var jobs = app.Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<BackgroundJobService>().Single();
        var before = await JobRunsAsync(app);
        await jobs.RunOnceAsync(default);
        Assert.Equal(before, await JobRunsAsync(app));

        // The operator leaves maintenance mode; changes work again and the banner is gone.
        var leave = await DemoApp.PostFormAsync(sysadmin, "/Admin/Operations/Maintenance", [new("Reason", "Rehearsal finished")], "/Admin/Operations/Maintenance?handler=Leave");
        Assert.Equal(HttpStatusCode.Redirect, leave.StatusCode);
        await Task.Delay(MaintenanceSnapshot.Ttl + TimeSpan.FromMilliseconds(200));                      // another instance would see it within the snapshot's lifetime; this process saw it at once
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, (await TryChange(tenantAdmin, "/Admin/Categories")).StatusCode);
        Assert.DoesNotContain("The system is in maintenance.", await tenantAdmin.GetStringAsync("/"));
        await jobs.RunOnceAsync(default);
        Assert.True(await JobRunsAsync(app) > before);
    }

    private static async Task<int> JobRunsAsync(DemoApp app)
    {
        var (tenantId, _) = await app.FindUserAsync("tenantadmin@a.example.test");
        return await app.WithTenantAsync(tenantId, sp => sp.GetRequiredService<NotificationsDbContext>().JobRuns.CountAsync());
    }

    [Fact]
    public async Task Evidence_and_the_support_bundle_download_for_those_who_may_and_hold_no_secret_and_a_spreadsheet_cannot_run_them()
    {
        using var app = DemoApp.Start();
        using var sysadmin = await app.SignInAsync("sysadmin@installation.example.test");
        var json = await sysadmin.GetAsync("/Admin/Operations/Evidence?handler=Json");
        Assert.Equal(HttpStatusCode.OK, json.StatusCode); Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
        var jsonText = await json.Content.ReadAsStringAsync();
        Assert.Contains("not an approval for production use", jsonText); Assert.Contains("Not Verified", jsonText);
        var csv = await sysadmin.GetAsync("/Admin/Operations/Evidence?handler=Csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var csvText = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        foreach (var line in csvText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)) foreach (var cell in line.Split(',')) Assert.False(cell.Length > 0 && cell.TrimStart('"')[0] is '=' or '+' or '@' or '\t');
        var bundle = await sysadmin.GetAsync("/Admin/Operations/Evidence?handler=Bundle");
        Assert.Equal("application/zip", bundle.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(new MemoryStream(await bundle.Content.ReadAsByteArrayAsync()));
        using var reader = new StreamReader(zip.GetEntry("bundle.json")!.Open());
        var text = await reader.ReadToEndAsync();
        Assert.DoesNotContain("Data Source=", text); Assert.DoesNotContain("file:ecp_api_", text); Assert.DoesNotContain("Password", text.Replace("passwords", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("deploymentModels", text);
    }
}

/// <summary>The command-line verbs (docs/OPERATIONS_RUNBOOK.md) over the real host composition.</summary>
[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class OperationsCommandLineTests
{
    private static async Task<(int Code, string Output, string Error)> RunAsync(DemoApp app, string command, params string[] arguments)
    {
        var originalOut = Console.Out; var originalError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter();
        Console.SetOut(output); Console.SetError(error);
        try { return (await OperationsCli.RunAsync(command, [command, .. arguments], app.Factory.Services), output.ToString(), error.ToString()); }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    [Fact]
    public async Task The_verbs_report_plainly_refuse_what_cannot_be_done_here_and_never_print_a_secret()
    {
        using var app = DemoApp.Start();
        // A provider with no supported backup says so and exits non-zero; nothing is created.
        var backup = await RunAsync(app, "backup", "--reason", "rehearsal");
        Assert.Equal(2, backup.Code);
        Assert.Contains("SQL Server", backup.Error);
        var restore = await RunAsync(app, "restore", "bk-20261017T000000Z-00000000", "ecp_target", "--reason", "rehearsal", "--confirm", "wrong words");
        Assert.Equal(2, restore.Code);
        Assert.Contains("Type exactly", restore.Error);
        Assert.Equal(2, (await RunAsync(app, "verify-backup", "../etc")).Code == 2 ? 2 : 0);
        Assert.Equal(0, (await RunAsync(app, "list-backups")).Code);

        // Maintenance mode: needs a reason, switches, and is visible to the next command.
        Assert.Equal(2, (await RunAsync(app, "maintenance", "on")).Code);
        Assert.Equal(0, (await RunAsync(app, "maintenance", "on", "--reason", "rehearsal")).Code);
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
            Assert.True((await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().GetAsync()).Enabled);
        }
        Assert.Equal(0, (await RunAsync(app, "maintenance", "off", "--reason", "finished")).Code);

        // Validation and the support bundle run, and the bundle holds neither the connection string nor a password.
        var validate = await RunAsync(app, "validate");
        Assert.Contains("Validation:", validate.Output);
        var path = Path.Combine(Path.GetTempPath(), "ecp-cli-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Assert.Equal(0, (await RunAsync(app, "support-bundle", path)).Code);
            using var zip = ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("bundle.json")!.Open());
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain("Data Source=", text); Assert.DoesNotContain("file:ecp_api_", text);
        }
        finally { File.Delete(path); }
        Assert.Equal(64, (await RunAsync(app, "restore")).Code);          // not enough arguments: usage
        Assert.DoesNotContain("file:ecp_api_", backup.Output + backup.Error + restore.Output + restore.Error + validate.Output + validate.Error);
    }
}
