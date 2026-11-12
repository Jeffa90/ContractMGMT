using System.Net;
using System.Security.Claims;
using Ecp.Audit.Persistence;
using Ecp.Setup;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The real host, the real setup wizard, the real sign-in pages. The web server's Windows Authentication is simulated by a start-up filter that puts a Windows-style
/// principal (name + SID, authentication type Negotiate) on the request before the application runs, exactly where IIS in-process puts it. This does NOT prove an IIS run.
/// </summary>
[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class WindowsAuthenticationHostTests
{
    private const string AdminAccount = "DOM\\first.admin", AdminSid = "S-1-5-21-5-6-7-1201";
    private const string StrangerAccount = "DOM\\stranger", StrangerSid = "S-1-5-21-5-6-7-1299";
    private const string AdminEmail = "first.admin@windows.example.invalid";
    public const string Header = "X-Test-Windows";

    /// <summary>Simulates IIS in-process Windows Authentication: a Windows principal on HttpContext.User before anything else runs.</summary>
    private sealed class SimulatedIisAuthentication : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nxt) =>
            {
                if (context.Request.Headers.TryGetValue(Header, out var value) && value.ToString().Split('|') is [var account, var sid])
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, account), new Claim(ClaimTypes.PrimarySid, sid)], "Negotiate", ClaimTypes.Name, ClaimTypes.Role));
                await nxt();
            });
            next(app);
        };
    }

    private static Dictionary<string, string?> Uninstalled(string dir) => new()
    {
        ["Ecp:Mode"] = "Test", ["Ecp:DemoTenantResolution"] = "true", ["Ecp:DemoSeed"] = "false",
        ["Ecp:DataDirectory"] = dir, ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "ecp.db")}",
        ["Database:ConnectionStringRef"] = "", ["Database:AutoMigrate"] = "true",
    };

    private static readonly string[] ToReadiness = ["mode", "database", "storage", "tenant", "pack", "identity", "admin", "organisation", "branding", "classification", "notifications", "access"];

    private static Dictionary<string, Dictionary<string, string>> Steps(string? adminAccount) => new()
    {
        ["mode"] = new() { ["deployment"] = "Local", ["tenancyMode"] = "SingleOrganisation", ["acknowledge"] = "true" },
        ["database"] = new(), ["storage"] = new(),
        ["tenant"] = new() { ["tenantName"] = "Windows Setup Tenant" },
        ["pack"] = new() { ["packId"] = "jbs-procurement" },
        ["identity"] = new() { ["kind"] = "Windows", ["key"] = "windows", ["displayName"] = "Windows sign-in", ["windowsDomains"] = "DOM" },
        ["admin"] = new() { ["displayName"] = "First Administrator", ["email"] = AdminEmail, ["installationAdmin"] = "true", ["windowsAccount"] = adminAccount ?? "" },
        ["organisation"] = new() { ["organisationName"] = "Windows Setup Organisation", ["culture"] = "en-AU", ["timeZone"] = "Australia/Brisbane", ["currency"] = "AUD", ["referencePrefix"] = "WSO" },
        ["branding"] = new() { ["productName"] = "Windows Setup Platform", ["accentColour"] = "#14532d", ["footerText"] = "Synthetic installation" },
        ["classification"] = new() { ["levels"] = "Public\nInternal\nConfidential\nRestricted", ["defaultLevel"] = "Internal" },
        ["notifications"] = new() { ["provider"] = "Mock", ["from"] = "platform@setup.example.invalid", ["testRecipient"] = AdminEmail },
        ["access"] = new() { ["confirm"] = "true" },
    };

    private static async Task<DemoApp> InstalledAsync(string? adminAccount = AdminAccount)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ecp-win-" + Guid.NewGuid().ToString("N"))).FullName;
        var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(dir)) s[k] = v; }, services => services.AddSingleton<IStartupFilter, SimulatedIisAuthentication>());
        using var setup = app.NewClient();
        var token = await app.Factory.Services.GetRequiredService<ISetupService>().IssueTokenAsync();
        await DemoApp.PostFormAsync(setup, "/Setup", new Dictionary<string, string> { ["token"] = token });
        var steps = Steps(adminAccount);
        foreach (var id in ToReadiness)
        {
            var response = await DemoApp.PostFormAsync(setup, $"/Setup/Step/{id}", steps[id]);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(!body.Contains("This step was not completed"), $"{id} ({(int)response.StatusCode}): " + Problem(body));
        }
        // The identity step's "Test sign-in": the administrator signs in through the real Windows path, which proves they can get in (readiness R-ID-02) and activates them (R-ADM-01).
        var test = await setup.SendAsync(As("/Account/WindowsSignIn?testProvider=windows&returnUrl=%2FSetup%2FStep%2Freadiness", AdminAccount, AdminSid));
        Assert.Equal(HttpStatusCode.Redirect, test.StatusCode);
        Assert.Equal("/Setup/Step/readiness", Location(test));
        await DemoApp.PostFormAsync(setup, "/Setup/Step/demo", new Dictionary<string, string>(), "/Setup/Step/demo?handler=Skip");
        var ack = new Dictionary<string, string> { ["ack.R-NOT-01"] = "Mock mail is acceptable here.", ["ack.R-OPS-01"] = "No scanner is connected, so files stay held; acceptable here." };
        var readiness = await DemoApp.PostFormAsync(setup, "/Setup/Step/readiness", ack);
        var readinessBody = await readiness.Content.ReadAsStringAsync();
        Assert.True(!readinessBody.Contains("This step was not completed"), "readiness: " + Problem(readinessBody));
        var done = await DemoApp.PostFormAsync(setup, "/Setup/Step/complete", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        return app;
    }

    private static string Problem(string html)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, "class=\"error-summary\".*?</div>", System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(m.Success ? m.Value : html, "<[^>]+>", " ").Replace("\n", " ");
    }

    private static HttpRequestMessage As(string url, string? account = null, string? sid = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (account is not null) request.Headers.Add(Header, $"{account}|{sid}");
        return request;
    }

    private static string Location(HttpResponseMessage r) { var l = r.Headers.GetValues("Location").Single(); return l.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(l, UriKind.Absolute, out var u) ? u.PathAndQuery : l; }
    private static bool HasSessionCookie(HttpResponseMessage r) => r.Headers.TryGetValues("Set-Cookie", out var c) && c.Any(x => x.StartsWith("ecp.session="));

    [Fact]
    public async Task A_windows_identity_supplied_by_the_server_is_never_an_application_session()
    {
        using var app = await InstalledAsync();
        using var client = app.NewClient();
        // Authenticated by the web server, but not signed in to the application: every page and the API still ask for sign-in.
        var page = await client.SendAsync(As("/Contracts", AdminAccount, AdminSid));
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/SignIn", Location(page));
        var api = await client.SendAsync(As("/api/v1/contracts", AdminAccount, AdminSid));
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.False(HasSessionCookie(page));
    }

    [Fact]
    public async Task The_first_administrator_set_up_with_a_windows_account_signs_in_and_reaches_the_application()
    {
        using var app = await InstalledAsync();
        using var client = app.NewClient();
        // 1. The sign-in page sees who the web server says this is and goes straight on.
        var signIn = await client.SendAsync(As("/SignIn?returnUrl=%2F", AdminAccount, AdminSid));
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.StartsWith("/Account/WindowsSignIn", Location(signIn));
        // 2. The application decides: invited, active (set up), has a role. A session cookie is issued.
        var decide = await client.SendAsync(As("/Account/WindowsSignIn?returnUrl=%2F", AdminAccount, AdminSid));
        Assert.Equal(HttpStatusCode.Redirect, decide.StatusCode);
        Assert.Equal("/", Location(decide));
        Assert.True(HasSessionCookie(decide));
        // 3. From here the cookie, not Windows, is the session. The administrator's own access is what they get.
        var home = await client.GetStringAsync("/");
        Assert.Contains("First Administrator", home);
        Assert.Contains("Admin/Users", home);
        // Later requests need no Windows header: the session stands on its own.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Users")).StatusCode);   // the administrator's own access
    }

    [Fact]
    public async Task An_uninvited_windows_account_gets_a_friendly_refusal_that_names_the_account_and_is_audited()
    {
        using var app = await InstalledAsync();
        using var client = app.NewClient();
        var decide = await client.SendAsync(As("/Account/WindowsSignIn?returnUrl=%2F", StrangerAccount, StrangerSid));
        Assert.Equal(HttpStatusCode.Redirect, decide.StatusCode);
        Assert.StartsWith("/Account/AccessRefused?code=not_invited", Location(decide));
        Assert.False(HasSessionCookie(decide));
        var refused = await client.SendAsync(As(Location(decide), StrangerAccount, StrangerSid));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("You have not been invited", body);
        Assert.Contains(StrangerAccount, body);                         // what to tell the administrator
        Assert.DoesNotContain("Exception", body);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Contracts")).StatusCode);   // and still no session

        await using var scope = app.Factory.Services.CreateAsyncScope();
        var tenant = (await scope.ServiceProvider.GetRequiredService<Ecp.Tenancy.ITenantDirectory>().ListAsync()).Single();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant.Id);
        var events = await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Events.Where(e => e.Action == "auth.signin_refused").ToListAsync();
        var audit = Assert.Single(events);
        Assert.Equal("not_invited", audit.Reason);
        Assert.Contains("DOM\\STRANGER", audit.EntityId);
        Assert.Contains(StrangerSid, audit.NewValuesJson);
    }

    [Fact]
    public async Task Without_a_windows_identity_the_sign_in_page_explains_instead_of_looping()
    {
        using var app = await InstalledAsync();
        using var client = app.NewClient();
        var page = await client.GetAsync("/SignIn");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var body = await page.Content.ReadAsStringAsync();
        Assert.Contains("did not tell the application who you are", body);
        var direct = await client.GetAsync("/Account/WindowsSignIn");
        Assert.StartsWith("/Account/AccessRefused?code=windows_unavailable", Location(direct));
    }

    [Fact]
    public async Task Signing_out_does_not_sign_straight_back_in()
    {
        using var app = await InstalledAsync();
        using var client = app.NewClient();
        await client.SendAsync(As("/Account/WindowsSignIn?returnUrl=%2F", AdminAccount, AdminSid));
        var token = await DemoApp.TokenAsync(client, "/SignOut");
        var signOut = await client.PostAsync("/SignOut", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal("/SignIn?reason=signed_out", Location(signOut));
        var again = await client.SendAsync(As("/SignIn?reason=signed_out", AdminAccount, AdminSid));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);   // shows "You have signed out", with a button, and no automatic redirect
        Assert.Contains("You have signed out", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_setup_wizard_refuses_a_windows_administrator_without_a_windows_account()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ecp-win-" + Guid.NewGuid().ToString("N"))).FullName;
        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(dir)) s[k] = v; });
        using var setup = app.NewClient();
        await DemoApp.PostFormAsync(setup, "/Setup", new Dictionary<string, string> { ["token"] = await app.Factory.Services.GetRequiredService<ISetupService>().IssueTokenAsync() });
        var steps = Steps(adminAccount: null);
        foreach (var id in new[] { "mode", "database", "storage", "tenant", "pack", "identity" }) await DemoApp.PostFormAsync(setup, $"/Setup/Step/{id}", steps[id]);
        var admin = await DemoApp.PostFormAsync(setup, "/Setup/Step/admin", steps["admin"]);
        Assert.Contains("Without it they could not sign in with Windows", await admin.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_windows_provider_is_not_registered_as_an_OpenID_Connect_scheme()
    {
        using var app = await InstalledAsync();
        using var client = app.NewClient();
        var page = await client.GetStringAsync("/SignIn");
        Assert.DoesNotContain("/Account/Challenge?scheme=", page);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Account/Challenge?scheme=oidc-anything")).StatusCode);
    }
}
