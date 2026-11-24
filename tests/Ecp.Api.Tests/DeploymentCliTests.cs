using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Ecp.Host;
using Ecp.Setup;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// ADR-0045: the pipeline's unattended setup. The real host and setup stages, driven by <see cref="DeploymentCli"/> with an answers file and
/// Ecp:Security:FailsafeAdmins, exactly as the deploy workflow runs them. IIS Windows Authentication is simulated as in WindowsAuthenticationHostTests.
/// </summary>
[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class DeploymentCliTests
{
    private const string Header = "X-Test-Windows";
    private const string FirstAccount = "DOM\\first.admin", FirstSid = "S-1-5-21-5-6-7-1401";
    private const string SecondAccount = "DOM\\second.admin", SecondSid = "S-1-5-21-5-6-7-1402";

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

    private static HttpRequestMessage As(string url, string account, string sid)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(Header, $"{account}|{sid}");
        return request;
    }

    private static IConfiguration Failsafe(params (string Account, string Name, string Email)[] admins)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < admins.Length; i++)
        {
            values[$"{DeploymentCli.FailsafeSection}:{i}:Account"] = admins[i].Account;
            values[$"{DeploymentCli.FailsafeSection}:{i}:DisplayName"] = admins[i].Name;
            values[$"{DeploymentCli.FailsafeSection}:{i}:Email"] = admins[i].Email;
        }
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string Answers(string dir)
    {
        var path = Path.Combine(dir, "answers.json");
        File.WriteAllText(path, """
        {
          "stages": {
            "mode": { "deployment": "IIS", "tenancyMode": "SingleOrganisation", "acknowledge": true },
            "tenant": { "tenantName": "Pipeline Tenant" },
            "pack": { "packId": "jbs-procurement" },
            "identity": { "kind": "Windows", "key": "windows", "displayName": "Windows sign-in" },
            "admin": { "installationAdmin": true, "doManage": true, "doOwn": true },
            "organisation": { "organisationName": "Pipeline Organisation", "culture": "en-AU", "timeZone": "Australia/Brisbane", "currency": "AUD", "referencePrefix": "PIP" },
            "notifications": { "provider": "Mock", "from": "platform@setup.example.invalid" },
            "access": { "confirm": true },
            "demo": "skip",
            "readiness": { "ack.R-NOT-01": "Mock mail is acceptable here.", "ack.R-OPS-01": "No scanner here; acceptable." }
          }
        }
        """);
        return path;
    }

    private static DemoApp Uninstalled(string dir) => DemoApp.Start(s =>
    {
        s["Ecp:Mode"] = "Test"; s["Ecp:DemoTenantResolution"] = "true"; s["Ecp:DemoSeed"] = "false"; s["Ecp:DataDirectory"] = dir;
        s["Database:Provider"] = "Sqlite"; s["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "ecp.db")}"; s["Database:ConnectionStringRef"] = ""; s["Database:AutoMigrate"] = "true";
    }, services => services.AddSingleton<IStartupFilter, SimulatedIisAuthentication>());

    [Fact]
    public async Task Setup_apply_installs_without_the_wizard_and_every_failsafe_administrator_can_sign_in()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ecp-cli-" + Guid.NewGuid().ToString("N"))).FullName;
        using var app = Uninstalled(dir);
        using var client = app.NewClient();
        var services = app.Factory.Services;
        var config = Failsafe((FirstAccount, "First Administrator", "first.admin@windows.example.invalid"), (SecondAccount, "Second Administrator", "second.admin@windows.example.invalid"));
        string[] args = ["setup-apply", "--answers", Answers(dir)];

        // First run: every step that a machine can do is done; the administrators exist but have not signed in, so readiness waits for a person.
        Assert.Equal(DeploymentCli.NeedsAttention, await DeploymentCli.RunAsync("setup-apply", args, services, config, EcpMode.Test));
        Assert.False(await services.GetRequiredService<ISetupService>().IsCompleteAsync());

        // The first administrator signs in once from their own PC (not the server): this is the test sign-in and activates them. The setup gate lets this path through.
        var signIn = await client.SendAsync(As("/Account/WindowsSignIn?testProvider=windows", FirstAccount, FirstSid));
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.DoesNotContain("AccessRefused", signIn.Headers.Location!.ToString());

        // Second run finishes setup.
        Assert.Equal(0, await DeploymentCli.RunAsync("setup-apply", args, services, config, EcpMode.Test));
        Assert.True(await services.GetRequiredService<ISetupService>().IsCompleteAsync());

        // The second failsafe administrator was never named in the wizard, yet is invited with the administrator role and can sign in.
        using var second = app.NewClient();
        var secondSignIn = await second.SendAsync(As("/Account/WindowsSignIn", SecondAccount, SecondSid));
        Assert.Equal(HttpStatusCode.Redirect, secondSignIn.StatusCode);
        Assert.DoesNotContain("AccessRefused", secondSignIn.Headers.Location!.ToString());

        // Re-running on a complete installation changes nothing and succeeds (every deploy runs it).
        Assert.Equal(0, await DeploymentCli.RunAsync("setup-apply", args, services, config, EcpMode.Test));
    }

    [Fact]
    public void Failsafe_administrators_must_be_windows_accounts_with_an_email_address()
    {
        Assert.Throws<DomainException>(() => DeploymentCli.ReadFailsafeAdmins(Failsafe(("first.admin@example.invalid", "A", "a@example.invalid"))));
        Assert.Throws<DomainException>(() => DeploymentCli.ReadFailsafeAdmins(Failsafe((FirstAccount, "A", "not-an-email"))));
        var admins = DeploymentCli.ReadFailsafeAdmins(Failsafe((FirstAccount, "A", "a@example.invalid")));
        Assert.Equal("DOM\\FIRST.ADMIN", Assert.Single(admins).Account);
    }

    [Fact]
    public void Answers_override_stage_defaults_and_the_administrator_comes_only_from_configuration()
    {
        var admin = new StageDescriptor("admin", "t", "s", StageStatus.NotStarted,
            [new StageField("displayName", "n", FieldKind.Text), new StageField("installationAdmin", "i", FieldKind.Checkbox, Value: "true")], [], "a", null, true, null);
        using var doc = JsonDocument.Parse("""{ "admin": { "displayName": "Someone Else", "installationAdmin": false } }""");
        var values = DeploymentCli.ValuesFor(admin, doc.RootElement, new FailsafeAdmin("DOM\\X", "Configured Admin", "x@example.invalid"));
        Assert.Equal("Configured Admin", values["displayName"]);
        Assert.Equal("DOM\\X", values["windowsAccount"]);
        Assert.Equal("false", values["installationAdmin"]);
    }
}
