using System.Net;
using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Operations;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// G-12 over real HTTP and a real loopback SMTP socket: email is Healthy only after the provider was reached and a synthetic message accepted with the current settings; unreachable, rejecting and untested providers are
/// never Healthy; a changed setting or the passage of time withdraws a pass; simulated providers and Microsoft Graph stay Not Verified. The same scenarios run on SQLite and on real SQL Server.
/// </summary>
internal static class EmailScenarios
{
    private const string SysAdmin = "sysadmin@installation.example.test";
    public static Action<Dictionary<string, string?>> Settings(string mode = "Test") => s => { s["Ecp:Mode"] = mode; };

    private static async Task<string> PageAsync(HttpClient c, string url = "/Admin/Operations/Email")
    {
        var r = await c.GetAsync(url);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)r.StatusCode}");
        return await r.Content.ReadAsStringAsync();
    }

    public static string State(string html) { var m = Regex.Match(html, "Readiness: <span class=\"badge [a-z]+\">([A-Za-z]+)</span>"); return m.Groups[1].Value; }

    private static Task<HttpResponseMessage> Save(HttpClient c, string provider, string? host = null, int port = 587, bool tls = false, string from = "platform@company.example.test", string? userRef = null, string? passwordRef = null)
        => DemoApp.PostFormAsync(c, "/Admin/Operations/Email", new Dictionary<string, string>
        {
            ["Provider"] = provider, ["From"] = from, ["Host"] = host ?? "", ["Port"] = port.ToString(), ["UseTls"] = tls ? "true" : "false", ["UserRef"] = userRef ?? "", ["PasswordRef"] = passwordRef ?? ""
        }, "/Admin/Operations/Email?handler=Save");

    private static Task<HttpResponseMessage> Test(HttpClient c, string recipient = "tester@company.example.test")
        => DemoApp.PostFormAsync(c, "/Admin/Operations/Email", new Dictionary<string, string> { ["TestRecipient"] = recipient }, "/Admin/Operations/Email?handler=Test");

    private static async Task<string> FollowAsync(HttpClient c, HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return await PageAsync(c, r.Headers.Location!.OriginalString);
    }

    public static async Task A_real_test_message_makes_email_healthy_and_is_synthetic(DemoApp app)
    {
        using var smtp = new FakeSmtpServer();
        using var sys = await app.SignInAsync(SysAdmin);
        var saved = await FollowAsync(sys, await Save(sys, "smtp", "127.0.0.1", smtp.Port));
        Assert.Equal("NotVerified", State(saved));                         // saving proves nothing
        Assert.Contains("not verified until a test message", saved);
        Assert.Empty(smtp.Mail);

        var tested = await FollowAsync(sys, await Test(sys));
        Assert.Equal("Healthy", State(tested));
        var mail = Assert.Single(smtp.Mail);
        Assert.Contains("tester@company.example.test", mail.To);
        Assert.Contains("contains no contract information", mail.Data);
        Assert.DoesNotContain("Agreement", mail.Data);
        Assert.Contains("Passed", tested);

        var ready = await sys.GetStringAsync("/health/ready");
        Assert.Contains("R-EML-01", ready);
        var health = await PageAsync(sys, "/Admin/Health");
        Assert.Contains("R-EML-01", health);
        Assert.Contains("a test message was accepted", health);

        // The audit trail records the configuration and the test, with the masked recipient and no secret.
        var events = await app.WithTenantAsync(SystemTenant.Id, async sp => await sp.GetRequiredService<IAuditEventSource>().Events.Where(e => e.Action.StartsWith("operations.email.")).OrderBy(e => e.Sequence).Select(e => new { e.Action, e.NewValuesJson }).ToListAsync());
        Assert.Contains(events, e => e.Action == "operations.email.configured");
        var tests = events.Where(e => e.Action == "operations.email.tested").ToList();
        Assert.Single(tests);
        Assert.Contains("t***@company.example.test", tests[0].NewValuesJson);
        Assert.DoesNotContain("tester@company", tests[0].NewValuesJson);
    }

    public static async Task An_unreachable_provider_is_never_healthy(DemoApp app)
    {
        using var sys = await app.SignInAsync(SysAdmin);
        var saved = await FollowAsync(sys, await Save(sys, "smtp", "127.0.0.1", FakeSmtpServer.ClosedPort()));
        Assert.NotEqual("Healthy", State(saved));
        var tested = await FollowAsync(sys, await Test(sys));
        Assert.Equal("Failed", State(tested));
        Assert.Contains("EML-CONN-001", tested);
        Assert.Contains("could not connect", tested);
        Assert.DoesNotContain("SocketException", tested);                                       // plain language, no technical type names
        var ready = await sys.GetStringAsync("/health/ready");
        Assert.DoesNotContain("\"R-EML-01\",\"status\":\"Healthy\"", ready.Replace(" ", ""));
        Assert.Contains("Failed", await PageAsync(sys, "/Admin/Health"));
    }

    public static async Task A_server_that_refuses_the_recipient_fails_the_delivery_step(DemoApp app)
    {
        using var smtp = new FakeSmtpServer(SmtpBehaviour.RejectRecipient);
        using var sys = await app.SignInAsync(SysAdmin);
        await Save(sys, "smtp", "127.0.0.1", smtp.Port);
        var tested = await FollowAsync(sys, await Test(sys));
        Assert.Equal("Failed", State(tested));
        Assert.Contains("EML-DLV-002", tested);
        Assert.Contains("Connection", tested);
    }

    public static async Task A_service_that_is_not_a_mail_server_is_reported_plainly(DemoApp app)
    {
        using var smtp = new FakeSmtpServer(SmtpBehaviour.NotSmtp);
        using var sys = await app.SignInAsync(SysAdmin);
        await Save(sys, "smtp", "127.0.0.1", smtp.Port);
        var tested = await FollowAsync(sys, await Test(sys));
        Assert.Equal("Failed", State(tested));
        Assert.Contains("EML-CONN-002", tested);
    }

    public static async Task Requiring_tls_fails_against_a_server_without_tls(DemoApp app)
    {
        using var smtp = new FakeSmtpServer();
        using var sys = await app.SignInAsync(SysAdmin);
        await Save(sys, "smtp", "127.0.0.1", smtp.Port, tls: true);
        var tested = await FollowAsync(sys, await Test(sys));
        Assert.Equal("Failed", State(tested));
        Assert.Contains("EML-TLS-001", tested);
        Assert.Empty(smtp.Mail);                                                                // nothing was sent in the clear
    }

    public static async Task Changing_a_setting_withdraws_the_pass(DemoApp app)
    {
        using var smtp = new FakeSmtpServer();
        using var sys = await app.SignInAsync(SysAdmin);
        await Save(sys, "smtp", "127.0.0.1", smtp.Port);
        Assert.Equal("Healthy", State(await FollowAsync(sys, await Test(sys))));
        var changed = await FollowAsync(sys, await Save(sys, "smtp", "127.0.0.1", smtp.Port, from: "someone.else@company.example.test"));
        Assert.Equal("NotVerified", State(changed));                                            // the old pass belongs to the old settings
        Assert.Contains("No (settings changed)", changed);
    }

    public static async Task A_pass_goes_stale_by_policy(DemoApp app, FakeClock clock)
    {
        using var smtp = new FakeSmtpServer();
        using var sys = await app.SignInAsync(SysAdmin);
        await Save(sys, "smtp", "127.0.0.1", smtp.Port);
        Assert.Equal("Healthy", State(await FollowAsync(sys, await Test(sys))));
        clock.Advance(TimeSpan.FromDays(8));
        await using var scope = app.Factory.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(SystemTenant.Id);
        var info = await scope.ServiceProvider.GetRequiredService<IEmailConfigurationService>().ReadinessAsync();
        Assert.Equal(EmailState.Stale, info.State);
        Assert.Equal("Not Verified", info.Verdict);
        Assert.Equal("EML-RDY-002", info.Code);
    }

    public static async Task Simulated_providers_and_graph_stay_not_verified(DemoApp app)
    {
        using var sys = await app.SignInAsync(SysAdmin);
        var sim = await FollowAsync(sys, await Save(sys, "dev-capture"));
        Assert.Equal("NotVerified", State(sim));
        var simTested = await FollowAsync(sys, await Test(sys));
        Assert.Equal("Simulated", State(simTested));
        Assert.Contains("delivers nothing to a real mailbox", simTested);
        Assert.Contains("Not Verified", simTested);                                              // counts as evidence: Not Verified, never Passed
        Assert.DoesNotContain(">Healthy<", simTested);

        await Save(sys, "graph");
        var graph = await FollowAsync(sys, await Test(sys));
        Assert.Equal("NotVerified", State(graph));
        Assert.Contains("EML-GRAPH-001", graph);
    }

    public static async Task Secrets_are_references_only_and_never_shown(DemoApp app)
    {
        using var smtp = new FakeSmtpServer();
        using var sys = await app.SignInAsync(SysAdmin);
        var literal = await Save(sys, "smtp", "127.0.0.1", smtp.Port, userRef: "svc-user", passwordRef: "hunter2-literal");
        Assert.Equal(HttpStatusCode.OK, literal.StatusCode);                                     // refused: the form is shown again with the problem
        var body = await literal.Content.ReadAsStringAsync();
        Assert.Contains("secret reference", body);
        Assert.DoesNotContain("hunter2-literal", body);
        var ok = await FollowAsync(sys, await Save(sys, "smtp", "127.0.0.1", smtp.Port, userRef: "env:ECP_TEST_SMTP_USER", passwordRef: "env:ECP_TEST_SMTP_PASSWORD"));
        Assert.Contains("env:ECP_TEST_SMTP_PASSWORD", ok);
        var audit = await app.WithTenantAsync(SystemTenant.Id, async sp => await sp.GetRequiredService<IAuditEventSource>().Events.Where(e => e.Action == "operations.email.configured").Select(e => e.NewValuesJson).ToListAsync());
        Assert.DoesNotContain(audit, a => a!.Contains("hunter2"));
    }

    public static async Task Only_an_installation_administrator_may_change_email(DemoApp app)
    {
        foreach (var email in new[] { "tenantadmin@a.example.test", "orgadmin@a.example.test", "auditor@a.example.test" })
        {
            using var c = await app.SignInAsync(email);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/Admin/Operations/Email")).StatusCode);
        }
        using var anon = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync("/Admin/Operations/Email")).StatusCode);
    }
}

[Collection("Api")]
[Trait("Category", "Email")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class EmailEnablementTests
{
    [Fact] public async Task A_real_test_message_makes_email_healthy_and_is_synthetic() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.A_real_test_message_makes_email_healthy_and_is_synthetic(app); }
    [Fact] public async Task An_unreachable_provider_is_never_healthy() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.An_unreachable_provider_is_never_healthy(app); }
    [Fact] public async Task A_server_that_refuses_the_recipient_fails_the_delivery_step() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.A_server_that_refuses_the_recipient_fails_the_delivery_step(app); }
    [Fact] public async Task A_service_that_is_not_a_mail_server_is_reported_plainly() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.A_service_that_is_not_a_mail_server_is_reported_plainly(app); }
    [Fact] public async Task Requiring_tls_fails_against_a_server_without_tls() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.Requiring_tls_fails_against_a_server_without_tls(app); }
    [Fact] public async Task Changing_a_setting_withdraws_the_pass() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.Changing_a_setting_withdraws_the_pass(app); }
    [Fact] public async Task A_pass_goes_stale_by_policy() { var clock = new FakeClock(DateTime.UtcNow); using var app = DemoApp.Start(EmailScenarios.Settings(), s => s.AddSingleton<IClock>(clock)); await EmailScenarios.A_pass_goes_stale_by_policy(app, clock); }
    [Fact] public async Task Simulated_providers_and_graph_stay_not_verified() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.Simulated_providers_and_graph_stay_not_verified(app); }
    [Fact] public async Task Secrets_are_references_only_and_never_shown() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.Secrets_are_references_only_and_never_shown(app); }
    [Fact] public async Task Only_an_installation_administrator_may_change_email() { using var app = DemoApp.Start(EmailScenarios.Settings()); await EmailScenarios.Only_an_installation_administrator_may_change_email(app); }
}

[Collection("Api")]
[Trait("Category", "SqlServer")]
public sealed class EmailEnablementSqlServerTests
{
    [SqlServerFact] public async Task A_real_test_message_makes_email_healthy_on_real_SQL_Server() { using var app = DemoApp.StartSqlServer(EmailScenarios.Settings()); await EmailScenarios.A_real_test_message_makes_email_healthy_and_is_synthetic(app); }
    [SqlServerFact] public async Task An_unreachable_provider_is_never_healthy_on_real_SQL_Server() { using var app = DemoApp.StartSqlServer(EmailScenarios.Settings()); await EmailScenarios.An_unreachable_provider_is_never_healthy(app); }
    [SqlServerFact] public async Task Changing_a_setting_withdraws_the_pass_on_real_SQL_Server() { using var app = DemoApp.StartSqlServer(EmailScenarios.Settings()); await EmailScenarios.Changing_a_setting_withdraws_the_pass(app); }
}
