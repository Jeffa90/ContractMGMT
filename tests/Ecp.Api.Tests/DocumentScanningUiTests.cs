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

    /// <summary>The readiness the real service would report for a scanner in this mode; the self-test is stood in for by saying whether it has passed.</summary>
    private sealed class StubReadiness(IMalwareScanner scanner, EcpMode mode, bool selfTestPassed) : IScannerReadiness
    {
        public Task<ScannerReadinessInfo> EvaluateAsync(CancellationToken ct = default)
        {
            var state = mode == EcpMode.Production && scanner.IsDemonstrationOnly ? ScannerState.Refused : !scanner.IsConnected ? ScannerState.NotConnected : scanner.IsDemonstrationOnly ? ScannerState.Demonstration
                : selfTestPassed ? ScannerState.Healthy : ScannerState.NotVerified;
            return Task.FromResult(new ScannerReadinessInfo(state, scanner.Name, scanner.ProviderKey, $"Scanner '{scanner.Name}' is {state}.", null, null, null, scanner.IsDemonstrationOnly));
        }
        public Task<ScannerTestResult> RunSelfTestAsync(string by, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static Task<ReadinessResult> Check(EcpMode mode, bool connected, bool demo, bool allowSkipped, bool selfTested = true)
    {
        var scanner = new Scanner("provider x", connected, demo);
        return new DocumentScanReadinessCheck(new StubReadiness(scanner, mode, selfTested), Options.Create(new DocumentsOptions { AllowSkippedScan = allowSkipped }), Facts(mode)).RunAsync(CancellationToken.None);
    }

    private sealed class ExternalReadiness : IScannerReadiness
    {
        public Task<ScannerReadinessInfo> EvaluateAsync(CancellationToken ct = default)
            => Task.FromResult(new ScannerReadinessInfo(ScannerState.External, "external", ScannerProviderOptions.External, "Scanned before upload.", "SCN-030", null, null, false));
        public Task<ScannerTestResult> RunSelfTestAsync(string by, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(EcpMode.Production)]
    [InlineData(EcpMode.Test)]
    public async Task ADR_0049_external_scanning_is_a_warning_in_every_mode_never_healthy_or_blocking(EcpMode mode)
    {
        var result = await new DocumentScanReadinessCheck(new ExternalReadiness(), Options.Create(new DocumentsOptions()), Facts(mode)).RunAsync(CancellationToken.None);
        Assert.Equal(ReadinessStatus.Degraded, result.Status);
        Assert.Contains("SCN-030", result.Message);
        // Allowing skipped scans in Production is still refused, external scanning or not.
        Assert.Equal(ReadinessStatus.Unhealthy, (await new DocumentScanReadinessCheck(new ExternalReadiness(), Options.Create(new DocumentsOptions { AllowSkippedScan = true }), Facts(EcpMode.Production)).RunAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ADR_0049_the_external_attestation_releases_files_as_clean_under_its_own_name_and_is_not_a_demonstration_scanner()
    {
        IMalwareScanner scanner = new ExternalScanningAttestation();
        Assert.Equal(ScanVerdict.Clean, await scanner.ScanAsync(new MemoryStream([1, 2, 3])));
        Assert.True(scanner.IsConnected);
        Assert.False(scanner.IsDemonstrationOnly);
        Assert.Contains("not by this platform", scanner.Name);
        Assert.Equal(ScannerProviderOptions.External, scanner.ProviderKey);
    }

    [Fact]
    public async Task Production_is_not_ready_without_a_real_connected_scanner_and_trials_say_what_they_have()
    {
        Assert.Equal(ReadinessStatus.Unhealthy, (await Check(EcpMode.Production, connected: false, demo: false, allowSkipped: false)).Status);
        Assert.Equal(ReadinessStatus.Unhealthy, (await Check(EcpMode.Production, connected: true, demo: true, allowSkipped: false)).Status);
        Assert.Equal(ReadinessStatus.Unhealthy, (await Check(EcpMode.Production, connected: true, demo: false, allowSkipped: true)).Status);
        // A connected real scanner that has not passed the self-test is not ready either: connected is not the same as working.
        Assert.Equal(ReadinessStatus.Unhealthy, (await Check(EcpMode.Production, connected: true, demo: false, allowSkipped: false, selfTested: false)).Status);
        var good = await Check(EcpMode.Production, connected: true, demo: false, allowSkipped: false);
        Assert.Equal(ReadinessStatus.Healthy, good.Status);
        Assert.Contains("provider x", good.Message);
        Assert.Equal(ReadinessStatus.Degraded, (await Check(EcpMode.Test, connected: false, demo: false, allowSkipped: false)).Status);
        Assert.Equal(ReadinessStatus.Healthy, (await Check(EcpMode.Demo, connected: true, demo: true, allowSkipped: false)).Status);
        // In Test the demonstration scanner is allowed but is a prominent warning, never healthy.
        var test = await Check(EcpMode.Test, connected: true, demo: true, allowSkipped: false);
        Assert.Equal(ReadinessStatus.Degraded, test.Status);
        Assert.Contains("Nobody has recorded an acceptance", test.Message);
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
