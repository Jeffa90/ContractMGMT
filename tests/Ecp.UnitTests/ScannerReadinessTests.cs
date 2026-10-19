using Ecp.Documents;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>G-25: the provider-neutral scanner boundary. The readiness states, the self-test and the rule that the demonstration scanner is never a verified scanner and is refused in Production.</summary>
[Trait("Category", "Unit")]
public class ScannerReadinessTests
{
    /// <summary>A scanner that behaves like a competent provider: Clean for harmless text, Infected for the standard test file.</summary>
    private sealed class GoodScanner : IMalwareScanner
    {
        public string Name => "unit-provider";
        public bool Reachable { get; set; } = true;
        public Task<ScannerHealth> CheckHealthAsync(CancellationToken ct = default) => Task.FromResult(new ScannerHealth(Reachable, Reachable ? "up" : "connection refused"));
        public async Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream(); await content.CopyToAsync(ms, ct);
            return System.Text.Encoding.ASCII.GetString(ms.ToArray()).Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE") ? ScanVerdict.Infected : ScanVerdict.Clean;
        }
    }

    private sealed class BlindScanner : IMalwareScanner
    {
        public string Name => "blind";
        public Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default) => Task.FromResult(ScanVerdict.Clean);   // says Clean to everything
    }

    private sealed class BrokenScanner : IMalwareScanner
    {
        public string Name => "broken";
        public Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default) => Task.FromResult(ScanVerdict.Error);
    }

    private static ScannerReadinessService Service(IMalwareScanner scanner, EcpMode mode, FakeClock? clock = null, ScannerTestStore? store = null, Action<ScannerProviderOptions>? configure = null)
    {
        var options = new ScannerProviderOptions { Mode = mode };
        configure?.Invoke(options);
        // The audit writer, unit of work, tenant and actor are only used when a test result is recorded; evaluation does not touch them.
        return new ScannerReadinessService(scanner, options, store ?? new ScannerTestStore(), clock ?? new FakeClock(), null!, null!, null!, null!);
    }

    [Fact]
    public async Task No_scanner_is_not_connected_and_never_verified()
    {
        var info = await Service(new DisabledMalwareScannerProbe(), EcpMode.Production).EvaluateAsync();
        Assert.Equal(ScannerState.NotConnected, info.State);
        Assert.Equal("Not Verified", info.Verdict);
        Assert.Equal("SCN-001", info.Code);
    }

    [Theory]
    [InlineData(EcpMode.Demo, ScannerState.Demonstration)]
    [InlineData(EcpMode.Test, ScannerState.Demonstration)]
    [InlineData(EcpMode.Production, ScannerState.Refused)]
    public async Task The_demonstration_scanner_is_never_verified_and_is_refused_in_production(EcpMode mode, ScannerState expected)
    {
        var info = await Service(new MockMalwareScanner(), mode, configure: o => o.Provider = "mock").EvaluateAsync();
        Assert.Equal(expected, info.State);
        Assert.NotEqual("Passed", info.Verdict);
        if (mode == EcpMode.Test) Assert.Contains("TEST MODE", info.Summary);
        if (mode == EcpMode.Production) Assert.Equal("SCN-010", info.Code);
    }

    [Fact]
    public async Task Production_refuses_the_mock_provider_even_when_a_different_scanner_object_is_registered()
    {
        var info = await Service(new GoodScanner(), EcpMode.Production, configure: o => o.Provider = "mock").EvaluateAsync();
        Assert.Equal(ScannerState.Refused, info.State);
    }

    [Fact]
    public async Task A_connected_scanner_is_not_verified_until_it_has_been_tested()
    {
        var info = await Service(new GoodScanner(), EcpMode.Production).EvaluateAsync();
        Assert.Equal(ScannerState.NotVerified, info.State);
        Assert.Equal("Not Verified", info.Verdict);
    }

    [Fact]
    public void The_default_provider_is_the_demonstration_scanner_only_in_demo()
    {
        Assert.Equal("mock", new ScannerProviderOptions { Mode = EcpMode.Demo }.EffectiveProvider);
        Assert.Equal("none", new ScannerProviderOptions { Mode = EcpMode.Test }.EffectiveProvider);
        Assert.Equal("none", new ScannerProviderOptions { Mode = EcpMode.Production }.EffectiveProvider);
        Assert.Equal("mock", new ScannerProviderOptions { Mode = EcpMode.Test, Provider = "Mock" }.EffectiveProvider);
    }

    [Fact]
    public void The_standard_test_file_is_assembled_at_run_time_and_is_the_standard_string()
    {
        var text = System.Text.Encoding.ASCII.GetString(ScannerReadinessService.Eicar());
        Assert.Equal(68, text.Length);
        Assert.StartsWith("X5O!P%@AP[4", text);
        Assert.EndsWith("EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*", text);
    }

    private sealed class DisabledMalwareScannerProbe : IMalwareScanner
    {
        public string Name => "none";
        public bool IsConnected => false;
        public Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default) => Task.FromResult(ScanVerdict.Skipped);
    }
}
