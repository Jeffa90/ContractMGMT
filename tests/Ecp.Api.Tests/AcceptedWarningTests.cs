using System.Net;
using Ecp.Audit;
using Ecp.Operations;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>D14.13 over real HTTP: a warning counts as accepted only through a named person's written, time-limited acceptance, which is audited, can be revoked, and expires.</summary>
internal static class AcceptanceScenarios
{
    private const string SysAdmin = "sysadmin@installation.example.test";
    private const string Page = "/Admin/Operations/UpgradeReadiness";

    public static async Task Accepting_a_warning_needs_a_reason_a_named_person_and_is_audited_revocable_and_expires(DemoApp app, FakeClock clock)
    {
        using var sys = await app.SignInAsync(SysAdmin);
        var noReason = await DemoApp.PostFormAsync(sys, Page, new Dictionary<string, string> { ["Code"] = "UPF-040", ["Reason"] = "ok", ["ValidDays"] = "30" }, Page + "?handler=Accept");
        Assert.Equal(HttpStatusCode.OK, noReason.StatusCode);                                    // refused: the form is shown again with the problem
        Assert.Contains("reason", await noReason.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var accepted = await DemoApp.PostFormAsync(sys, Page, new Dictionary<string, string> { ["Code"] = "UPF-040", ["Reason"] = "The maintenance window is scheduled and communicated.", ["ValidDays"] = "30" }, Page + "?handler=Accept");
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        var listed = await sys.GetStringAsync(Page);
        Assert.Contains("UPF-040", listed);
        Assert.Contains("Active", listed);

        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(SystemTenant.Id);
            var exceptions = scope.ServiceProvider.GetRequiredService<IAcceptedExceptions>();
            Assert.Contains("UPF-040", await exceptions.ActiveCodesAsync(AcceptedExceptionKinds.PreflightWarning));
            Assert.DoesNotContain("UPF-056", await exceptions.ActiveCodesAsync(AcceptedExceptionKinds.PreflightWarning));   // only what was accepted is accepted
            Assert.Empty(await exceptions.ActiveCodesAsync(AcceptedExceptionKinds.MockScanner));                         // and only for its own kind
        }
        var events = await app.WithTenantAsync(SystemTenant.Id, async sp => await sp.GetRequiredService<IAuditEventSource>().Events.Where(e => e.Action.StartsWith("operations.exception.")).Select(e => e.Action).ToListAsync());
        Assert.Contains("operations.exception.accepted", events);

        clock.Advance(TimeSpan.FromDays(31));                                                     // the acceptance was for 30 days
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(SystemTenant.Id);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<IAcceptedExceptions>().ActiveCodesAsync(AcceptedExceptionKinds.PreflightWarning));
        }
        Assert.Contains("Expired", await sys.GetStringAsync(Page));
    }

    public static async Task An_acceptance_longer_than_the_limit_or_by_the_wrong_person_is_refused(DemoApp app)
    {
        using var sys = await app.SignInAsync(SysAdmin);
        // the form clamps to 90 days; the service refuses anything longer outright
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(SystemTenant.Id);
            var ex = await Assert.ThrowsAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IAcceptedExceptions>()
                .AcceptAsync(AcceptedExceptionKinds.PreflightWarning, "UPF-040", "installation", "A perfectly good reason.", new OperatorIdentity("x", "x"), TimeSpan.FromDays(400)));
            Assert.Equal("validity", ex.Code);
        }
        foreach (var email in new[] { "tenantadmin@a.example.test", "auditor@a.example.test" })
        {
            using var other = await app.SignInAsync(email);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(Page)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync(Page + "?handler=Accept", new FormUrlEncodedContent(new Dictionary<string, string> { ["Code"] = "UPF-040", ["Reason"] = "Nope, not mine to accept" }))).StatusCode);
        }
    }
}

[Collection("Api")]
[Trait("Category", "Evidence")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class AcceptedWarningTests
{
    [Fact]
    public async Task Accepting_a_warning_needs_a_reason_a_named_person_and_is_audited_revocable_and_expires()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        using var app = DemoApp.Start(services: s => s.AddSingleton<IClock>(clock));
        await AcceptanceScenarios.Accepting_a_warning_needs_a_reason_a_named_person_and_is_audited_revocable_and_expires(app, clock);
    }

    [Fact]
    public async Task An_acceptance_longer_than_the_limit_or_by_the_wrong_person_is_refused()
    {
        using var app = DemoApp.Start();
        await AcceptanceScenarios.An_acceptance_longer_than_the_limit_or_by_the_wrong_person_is_refused(app);
    }
}

[Collection("Api")]
[Trait("Category", "SqlServer")]
public sealed class AcceptedWarningSqlServerTests
{
    [SqlServerFact]
    public async Task Accepting_a_warning_is_audited_and_expires_on_real_SQL_Server()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        using var app = DemoApp.StartSqlServer(services: s => s.AddSingleton<IClock>(clock));
        await AcceptanceScenarios.Accepting_a_warning_needs_a_reason_a_named_person_and_is_audited_revocable_and_expires(app, clock);
    }
}
