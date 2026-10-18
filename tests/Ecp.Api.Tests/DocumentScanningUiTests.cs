using System.Net;
using Ecp.Documents;
using Ecp.Host;
using Ecp.Operations;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>G-25 through the pages and the readiness check: the scanning page shows what is held and why and is closed to people without the permission; Production readiness fails closed in every way a scanner can be missing or false.</summary>
[Collection("Api")]
[Trait("Category", "Governance")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class DocumentScanningUiTests
{
    private sealed class Scanner(string name, bool connected, bool demo) : IMalwareScanner
    {
        public string Name => name; public bool IsConnected => connected; public bool IsDemonstrationOnly => demo;
        public Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default) => Task.FromResult(ScanVerdict.Clean);
    }

    private static StartupFacts Facts(EcpMode mode) => new(mode, Ecp.Persistence.DatabaseProvider.SqlServer, true, false, false, false, "/data");

    private static Task<ReadinessResult> Check(EcpMode mode, bool connected, bool demo, bool allowSkipped)
        => new DocumentScanReadinessCheck(new Scanner("provider x", connected, demo), Options.Create(new DocumentsOptions { AllowSkippedScan = allowSkipped }), Facts(mode)).RunAsync(CancellationToken.None);

    [Fact]
    public async Task Production_is_not_ready_without_a_real_connected_scanner_and_trials_say_what_they_have()
    {
        Assert.Equal(ReadinessStatus.Unhealthy, (await Check(EcpMode.Production, connected: false, demo: false, allowSkipped: false)).Status);
        Assert.Equal(ReadinessStatus.Unhealthy, (await Check(EcpMode.Production, connected: true, demo: true, allowSkipped: false)).Status);
        Assert.Equal(ReadinessStatus.Unhealthy, (await Check(EcpMode.Production, connected: true, demo: false, allowSkipped: true)).Status);
        var good = await Check(EcpMode.Production, connected: true, demo: false, allowSkipped: false);
        Assert.Equal(ReadinessStatus.Healthy, good.Status);
        Assert.Contains("provider x", good.Message);
        Assert.Equal(ReadinessStatus.Degraded, (await Check(EcpMode.Test, connected: false, demo: false, allowSkipped: false)).Status);
        Assert.Equal(ReadinessStatus.Healthy, (await Check(EcpMode.Demo, connected: true, demo: true, allowSkipped: false)).Status);
    }

    [Fact]
    public async Task The_scanning_page_is_for_administrators_shows_the_demonstration_scanner_honestly_and_is_accessible()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync("tenantadmin@a.example.test");
        var response = await admin.GetAsync("/Admin/Documents/Scanning");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Accessibility.AssertBasics(body, "/Admin/Documents/Scanning");
        Assert.Contains("Demonstration scanner", body);
        Assert.Contains("never released", body);
        using var viewer = await app.SignInAsync("viewer.all@a.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Documents/Scanning")).StatusCode);
    }
}
