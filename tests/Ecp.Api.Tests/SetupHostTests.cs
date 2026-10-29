using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Ecp.Host.Setup;
using Ecp.Identity;
using Ecp.Setup;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>The first-run wizard driven through the real host: gate, token, session cookie, antiforgery, every step, completion, closure and amendment.</summary>
[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class SetupHostTests
{
    private const string AdminEmail = "first.admin@setup.example.invalid";

    /// <summary>A Test-mode host with an empty file database and no demonstration data: an installation that has not been set up.</summary>
    private static Dictionary<string, string?> Uninstalled(string dir) => new()
    {
        ["Ecp:Mode"] = "Test", ["Ecp:DemoTenantResolution"] = "true", ["Ecp:DemoSeed"] = "false",
        ["Ecp:DataDirectory"] = dir, ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "ecp.db")}",
        ["Database:ConnectionStringRef"] = "", ["Database:AutoMigrate"] = "true",
    };

    private static string NewDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ecp-setup-host-" + Guid.NewGuid().ToString("N"))).FullName;

    private static async Task<HttpResponseMessage> OpenSessionAsync(HttpClient client, string token)
        => await DemoApp.PostFormAsync(client, "/Setup", new Dictionary<string, string> { ["token"] = token });

    private static async Task<string> NewTokenAsync(DemoApp app) => await app.Factory.Services.GetRequiredService<ISetupService>().IssueTokenAsync();

    private static readonly Dictionary<string, Dictionary<string, string>> Steps = new()
    {
        ["mode"] = new() { ["deployment"] = "Local", ["tenancyMode"] = "SingleOrganisation", ["acknowledge"] = "true" },
        ["database"] = new(), ["storage"] = new(),
        ["tenant"] = new() { ["tenantName"] = "Host Setup Tenant" },
        ["pack"] = new() { ["packId"] = "jbs-procurement" },
        ["identity"] = new() { ["kind"] = "Demo" },
        ["admin"] = new() { ["displayName"] = "First Administrator", ["email"] = AdminEmail, ["installationAdmin"] = "true" },
        ["organisation"] = new() { ["organisationName"] = "Host Setup Organisation", ["culture"] = "en-AU", ["timeZone"] = "Australia/Brisbane", ["currency"] = "AUD", ["referencePrefix"] = "HSO" },
        ["branding"] = new() { ["productName"] = "Host Setup Platform", ["accentColour"] = "#14532d", ["footerText"] = "Synthetic installation" },
        ["classification"] = new() { ["levels"] = "Public\nInternal\nConfidential\nRestricted", ["defaultLevel"] = "Internal" },
        ["notifications"] = new() { ["provider"] = "Mock", ["from"] = "platform@setup.example.invalid", ["testRecipient"] = AdminEmail },
        ["access"] = new() { ["confirm"] = "true" },
    };

    private static async Task PostStepAsync(HttpClient client, string id, Dictionary<string, string>? fields = null)
    {
        var response = await DemoApp.PostFormAsync(client, $"/Setup/Step/{id}", fields ?? Steps[id]);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, $"{id}: {(int)response.StatusCode}");
        Assert.True(!body.Contains("This step was not completed"), $"{id} was not completed: " + System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Match(body, "This step was not completed.*?</div>", System.Text.RegularExpressions.RegexOptions.Singleline).Value, "<[^>]+>", " "));
        Assert.True(!RenderedPageSweepTests.RawTokens(body).Any(), $"{id}: raw term token in the page after the step: {string.Join(", ", RenderedPageSweepTests.RawTokens(body))}");   // G-32: neutral wording before any organisation exists
    }

    private static async Task AssertWizardPagesShowNoRawTokensAsync(HttpClient client, IEnumerable<string> ids)
    {
        foreach (var url in new[] { "/Setup", "/Setup/Report" }.Concat(ids.Select(id => $"/Setup/Step/{id}")))
        {
            var response = await client.GetAsync(url);
            if (response.StatusCode != HttpStatusCode.OK) continue;
            var leaks = RenderedPageSweepTests.RawTokens(await response.Content.ReadAsStringAsync()).ToList();
            Assert.True(leaks.Count == 0, $"{url}: raw term token(s) rendered: {string.Join(", ", leaks)}");
        }
    }

    private static async Task WalkAsync(HttpClient client, IEnumerable<string> ids) { foreach (var id in ids) await PostStepAsync(client, id); }

    private static async Task FinishAsync(HttpClient client)
    {
        var skip = await DemoApp.PostFormAsync(client, "/Setup/Step/demo", new Dictionary<string, string>(), "/Setup/Step/demo?handler=Skip");
        Assert.Equal(HttpStatusCode.Redirect, skip.StatusCode);
        var ack = new Dictionary<string, string> { ["ack.R-NOT-01"] = "Mock mail is acceptable for this synthetic installation.", ["ack.R-OPS-01"] = "No scanner is connected, so files stay held; acceptable for this synthetic installation." };
        await PostStepAsync(client, "readiness", ack);
        var done = await DemoApp.PostFormAsync(client, "/Setup/Step/complete", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal("/SignIn", PathOf(done));
    }

    private static string PathOf(HttpResponseMessage r)
    {
        var l = r.Headers.Location!.OriginalString;
        return Uri.TryCreate(l, UriKind.Absolute, out var u) ? u.AbsolutePath : l.Split('?')[0];
    }

    private static readonly string[] ToReadiness = ["mode", "database", "storage", "tenant", "pack", "identity", "admin", "organisation", "branding", "classification", "notifications", "access"];

    [Fact]
    public async Task An_incomplete_installation_sends_browsers_to_setup_and_closes_the_api_but_keeps_health_open()
    {
        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(NewDir())) s[k] = v; });
        using var client = app.NewClient();
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/Setup", PathOf(home));
        Assert.Equal("/Setup", PathOf(await client.GetAsync("/Contracts")));
        var api = await client.GetAsync("/api/v1/contracts");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
        Assert.Contains("setup_required", await api.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/info")).StatusCode);
        var setupResponse = await client.GetAsync("/Setup");
        Assert.Equal("no-store", setupResponse.Headers.CacheControl?.ToString());
        Assert.Contains("frame-ancestors 'none'", string.Join(' ', setupResponse.Headers.GetValues("Content-Security-Policy")));
        var page = await setupResponse.Content.ReadAsStringAsync();
        Assert.Contains("Setup token", page);
        Assert.DoesNotContain("First Administrator", page);
    }

    [Fact]
    public async Task Step_pages_need_the_session_and_the_token_form_rejects_forgery_wrong_tokens_and_reuse()
    {
        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(NewDir())) s[k] = v; });
        using var client = app.NewClient();
        Assert.Equal("/Setup", PathOf(await client.GetAsync("/Setup/Step/mode")));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Setup/Report")).StatusCode);

        // no antiforgery token: refused before the handler runs
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Setup", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "x" }))).StatusCode);

        var wrong = await OpenSessionAsync(client, "not-the-token");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("not valid", await wrong.Content.ReadAsStringAsync());
        Assert.False(wrong.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("ecp.setup=")));

        var token = await NewTokenAsync(app);
        var ok = await OpenSessionAsync(client, token);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var setCookie = ok.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ecp.setup="));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/Setup", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(token, setCookie);
        Assert.Contains("Work through the steps", await client.GetStringAsync("/Setup"));

        // the token cannot be used again, and a second browser cannot open a second session
        using var second = app.NewClient();
        var reuse = await OpenSessionAsync(second, token);
        Assert.Contains("already open", await reuse.Content.ReadAsStringAsync());
        var other = await OpenSessionAsync(second, await NewTokenAsync(app)); // a new token ends the first session
        Assert.Equal(HttpStatusCode.Redirect, other.StatusCode);
        Assert.Contains("Setup token", await client.GetStringAsync("/Setup")); // the first browser's session is gone
    }

    [Fact]
    public async Task The_wizard_runs_end_to_end_then_closes_and_signs_the_administrator_in_to_a_branded_application()
    {
        var dir = NewDir();
        var clock = new FakeClock(DateTime.UtcNow);
        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(dir)) s[k] = v; }, services => services.AddSingleton<IClock>(clock));
        using var client = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await OpenSessionAsync(client, await NewTokenAsync(app))).StatusCode);

        // a step that depends on an earlier one is refused until the earlier one is done
        var early = await DemoApp.PostFormAsync(client, "/Setup", Steps["organisation"], "/Setup/Step/organisation");
        Assert.Contains("Complete first", await early.Content.ReadAsStringAsync());

        await WalkAsync(client, ToReadiness);
        await AssertWizardPagesShowNoRawTokensAsync(client, ToReadiness.Concat(["demo", "readiness", "complete"]));
        var blocked = await DemoApp.PostFormAsync(client, "/Setup/Step/readiness", new Dictionary<string, string>());
        Assert.Contains("Acknowledge each warning", await blocked.Content.ReadAsStringAsync());
        await FinishAsync(client);

        // the setup pages no longer exist
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Setup")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Setup/Step/mode")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/Setup", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = "x" }))).StatusCode);
        Assert.Equal("/SignIn", PathOf(await client.GetAsync("/Contracts")));
        Assert.True(File.Exists(Path.Combine(dir, "bootstrap", "setup.complete")));

        // the named administrator can sign in (demonstration identity) and sees the organisation's branding
        using var admin = await app.SignInAsync(AdminEmail);
        var home = await admin.GetStringAsync("/");
        Assert.Contains("Host Setup Platform", home);
        Assert.Contains("Synthetic installation", home);
        var css = await admin.GetStringAsync("/css/branding.css");
        Assert.Contains("--accent: #14532d", css);
        Assert.Contains("Admin/Users", home);          // tenant administrators manage users
        Assert.DoesNotContain("/Admin/Setup", home);   // but only installation administrators can reopen setup
        // the initial administrator is also Organisation Administrator of the first organisation, so onboarding can begin straight away
        Assert.Contains("Organisation onboarding", home);
        var onboarding = await admin.GetStringAsync("/Onboarding");
        Assert.Contains("Set-up checklist", onboarding); Assert.Contains("Host Setup Organisation", onboarding);
    }

    [Fact]
    public async Task Reopening_setup_needs_the_installation_administrator_and_a_recent_authentication_and_is_audited()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(NewDir())) s[k] = v; }, services => services.AddSingleton<IClock>(clock));
        using var setup = app.NewClient();
        await OpenSessionAsync(setup, await NewTokenAsync(app));
        await WalkAsync(setup, ToReadiness);
        await FinishAsync(setup);

        // a tenant administrator is not an installation administrator
        using var tenantAdmin = await app.SignInAsync(AdminEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/Admin/Setup")).StatusCode);

        // the installation administrator signs in through the installation scope
        using var sys = await SignInInstallationAdminAsync(app);
        var overview = await sys.GetStringAsync("/Admin/Setup");
        Assert.Contains("Setup is complete", overview);
        Assert.Contains("R-AUD-01", overview);
        Assert.DoesNotContain("ECP_", overview);
        Assert.Equal(HttpStatusCode.OK, (await sys.GetAsync("/Admin/Setup/Step/branding")).StatusCode); // signed in moments ago: recent

        // ten minutes later the session is no longer "recent": the step asks the person to confirm it is them
        clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await sys.GetAsync("/Admin/Setup/Step/branding");
        Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.StartsWith("/Reauth", stale.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await sys.GetAsync("/Admin/Setup")).StatusCode); // reading the report does not need step-up

        var confirm = await DemoApp.PostFormAsync(sys, "/Reauth?returnUrl=%2FAdmin%2FSetup%2FStep%2Fbranding", new Dictionary<string, string> { ["returnUrl"] = "/Admin/Setup/Step/branding" });
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        var step = await sys.GetStringAsync("/Admin/Setup/Step/branding");
        Assert.Contains("You are amending a configuration", step);

        // amend the branding: a new release, visible at once, and the closed steps stay closed
        var amended = await DemoApp.PostFormAsync(sys, "/Admin/Setup/Step/branding", new Dictionary<string, string>
        { ["productName"] = "Amended Platform", ["accentColour"] = "#14532d", ["footerText"] = "" });
        Assert.Contains("Branding published", await amended.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await sys.GetAsync("/Admin/Setup/Step/tenant")).StatusCode);
        var closed = await sys.GetStringAsync("/Admin/Setup/Step/tenant");
        Assert.Contains("Setup is complete", closed);
        var denied = await DemoApp.PostFormAsync(sys, "/Admin/Setup/Step/branding", Steps["tenant"], "/Admin/Setup/Step/tenant");
        Assert.DoesNotContain("Tenant 'Host Setup Tenant' already exists", await denied.Content.ReadAsStringAsync());

        using var tenantUser = await app.SignInAsync(AdminEmail);
        Assert.Contains("Amended Platform", await tenantUser.GetStringAsync("/"));
        var audit = await tenantUser.GetStringAsync("/Admin/Audit?action=config.");
        Assert.Contains("config.", audit);
    }

    private static async Task<HttpClient> SignInInstallationAdminAsync(DemoApp app)
    {
        var client = app.NewClient();
        await using var scope = app.Factory.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(SystemTenant.Id);
        var user = await scope.ServiceProvider.GetRequiredService<IUserService>().FindByEmailAsync(AdminEmail);
        Assert.NotNull(user);
        var token = await DemoApp.TokenAsync(client, "/SignIn");
        var response = await client.PostAsync("/SignIn", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["pick"] = $"{SystemTenant.Id}:{user!.Id}", ["returnUrl"] = "/", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task A_restart_midway_resumes_setup_with_the_same_state_and_no_duplicates()
    {
        var dir = NewDir();
        Dictionary<string, string?> Settings(Dictionary<string, string?> s) { foreach (var (k, v) in Uninstalled(dir)) s[k] = v; return s; }
        string tenantId;
        using (var first = DemoApp.Start(s => Settings(s)))
        {
            using var client = first.NewClient();
            await OpenSessionAsync(client, await NewTokenAsync(first));
            await WalkAsync(client, ["mode", "database", "storage", "tenant", "pack", "identity"]);
            tenantId = (await first.Factory.Services.GetRequiredService<ISetupService>().SnapshotAsync()).TenantId!.Value.ToString();
        }

        // new process: same file database, same data directory (and key ring), nothing in memory
        using var second = DemoApp.Start(s => Settings(s));
        using var next = second.NewClient();
        var svc = second.Factory.Services.GetRequiredService<ISetupService>();
        var resumed = await svc.SnapshotAsync();
        Assert.Equal(tenantId, resumed.TenantId!.Value.ToString());
        Assert.Equal("admin", resumed.NextStageId);
        Assert.False(resumed.IsComplete);
        Assert.Equal(HttpStatusCode.Redirect, (await next.GetAsync("/")).StatusCode);              // still gated after the restart
        // the session cookie from before the restart is not accepted by a client that never had one; a fresh token reopens setup
        Assert.Equal(HttpStatusCode.Redirect, (await OpenSessionAsync(next, await NewTokenAsync(second))).StatusCode);
        await WalkAsync(next, ["tenant", "identity", "admin", "organisation", "branding", "classification", "notifications", "access"]);   // repeating finished steps is harmless
        await FinishAsync(next);
        Assert.True(await svc.IsCompleteAsync());
        await using var scope = second.Factory.Services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(), t => t.Name == "Host Setup Tenant");
    }

    [Fact]
    public async Task A_completed_installation_stays_closed_after_a_restart_and_does_not_reissue_tokens()
    {
        var dir = NewDir();
        Dictionary<string, string?> Settings(Dictionary<string, string?> s) { foreach (var (k, v) in Uninstalled(dir)) s[k] = v; return s; }
        using (var first = DemoApp.Start(s => Settings(s)))
        {
            using var client = first.NewClient();
            await OpenSessionAsync(client, await NewTokenAsync(first));
            await WalkAsync(client, ToReadiness);
            await FinishAsync(client);
        }
        using var second = DemoApp.Start(s => Settings(s));
        using var next = second.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await next.GetAsync("/Setup")).StatusCode);
        Assert.Equal("/SignIn", PathOf(await next.GetAsync("/")));
        await Assert.ThrowsAsync<DomainException>(() => second.Factory.Services.GetRequiredService<ISetupService>().IssueTokenAsync());
    }

    [Fact]
    public async Task The_setup_token_command_issues_a_token_that_a_running_host_accepts()
    {
        var dir = NewDir();
        Directory.CreateDirectory(dir);
        var host = typeof(Ecp.Host.Setup.SetupGateMiddleware).Assembly.Location;
        var psi = new ProcessStartInfo("dotnet", $"\"{host}\" setup-token") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var (k, v) in Uninstalled(dir)) psi.Environment[k.Replace(":", "__")] = v;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        // the token is the only line shaped like one (host log lines may also reach standard output)
        var token = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Single(l => System.Text.RegularExpressions.Regex.IsMatch(l, "^[A-Za-z0-9_-]{43}$"));
        Assert.True(token.Length >= 43);

        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(dir)) s[k] = v; });
        using var client = app.NewClient();
        var opened = await OpenSessionAsync(client, token);
        Assert.True(opened.StatusCode == HttpStatusCode.Redirect, "Opening setup with the command's token failed: " + System.Text.RegularExpressions.Regex.Match(await opened.Content.ReadAsStringAsync(), "<p>([^<]{10,200})</p>").Value);
        // and the token is not found in the state file in the clear
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(Path.Combine(dir, "bootstrap", "setup.state")));
    }

    [Fact]
    public async Task Demo_seeded_hosts_are_complete_from_the_start_so_existing_demonstrations_are_unaffected()
    {
        using var app = DemoApp.Start();
        using var client = app.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Setup")).StatusCode);
        Assert.True(await app.Factory.Services.GetRequiredService<ISetupService>().IsCompleteAsync());
    }

    private sealed class FromAddress(string address) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            Microsoft.AspNetCore.Builder.UseExtensions.Use(app, (Microsoft.AspNetCore.Http.HttpContext context, Func<Task> n) => { context.Connection.RemoteIpAddress = IPAddress.Parse(address); return n(); });
            next(app);
        };
    }

    [Fact]
    public async Task Setup_pages_do_not_exist_for_callers_off_the_server_unless_remote_setup_is_allowed()
    {
        using var closed = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(NewDir())) s[k] = v; },
            services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new FromAddress("203.0.113.9")));
        using var remote = closed.NewClient();
        Assert.Equal(HttpStatusCode.NotFound, (await remote.GetAsync("/Setup")).StatusCode);                      // not even the token form is shown
        Assert.Equal(HttpStatusCode.NotFound, (await remote.GetAsync("/Setup/Report")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await remote.GetAsync("/")).StatusCode);                           // the application itself is still gated
        Assert.Equal(HttpStatusCode.OK, (await remote.GetAsync("/health/live")).StatusCode);

        using var allowed = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(NewDir())) s[k] = v; s["Ecp:Setup:AllowRemote"] = "true"; },
            services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new FromAddress("203.0.113.9")));
        using var remoteAllowed = allowed.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await remoteAllowed.GetAsync("/Setup")).StatusCode);
    }

    [Fact]
    public async Task Remote_setup_is_refused_unless_explicitly_allowed()
    {
        var factory = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(NewDir())) s[k] = v; });
        using var app = factory;
        var svc = app.Factory.Services.GetRequiredService<ISetupService>();
        var refused = await svc.BeginSessionAsync(await svc.IssueTokenAsync(), fromLoopback: false);
        Assert.False(refused.Granted);
        Assert.Contains("only be opened from the server itself", refused.Refusal);
    }

    [Fact]
    public async Task The_email_step_passes_only_when_the_provider_really_accepts_a_test_message_and_readiness_then_needs_no_acknowledgement()
    {
        using var smtp = new Ecp.Tests.Common.FakeSmtpServer();
        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(NewDir())) s[k] = v; });
        using var client = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await OpenSessionAsync(client, await NewTokenAsync(app))).StatusCode);
        await WalkAsync(client, ToReadiness.Where(id => id != "notifications"));

        Dictionary<string, string> Smtp(int port) => new() { ["provider"] = "Smtp", ["from"] = "platform@setup.example.invalid", ["host"] = "127.0.0.1", ["port"] = port.ToString(), ["useTls"] = "false", ["testRecipient"] = AdminEmail };

        // nothing listens on the port: the step does not pass, says why in plain words with a diagnostic code, and readiness does not count email as ready
        var down = await DemoApp.PostFormAsync(client, "/Setup/Step/notifications", Smtp(Ecp.Tests.Common.FakeSmtpServer.ClosedPort()));
        var downBody = await down.Content.ReadAsStringAsync();
        Assert.Contains("This step was not completed", downBody);
        Assert.Contains("EML-CONN-001", downBody);
        Assert.Contains("email is not ready", downBody);
        Assert.Empty(smtp.Mail);

        // a real mail server accepts the synthetic message: the step passes and R-NOT-01 is a pass, not a warning to acknowledge
        await PostStepAsync(client, "notifications", Smtp(smtp.Port));
        Assert.Single(smtp.Mail);
        var readiness = await client.GetStringAsync("/Setup/Step/readiness");
        Assert.DoesNotContain("ack.R-NOT-01", readiness);
    }
}
