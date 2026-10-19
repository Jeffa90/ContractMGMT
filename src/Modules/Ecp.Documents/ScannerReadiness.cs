using System.Text;
using System.Text.Json;
using Ecp.Audit;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Documents;

/// <summary>
/// How the malware scanner is configured (<c>Documents:Scanner</c>). These are the settings any provider adapter reads; the platform ships no real adapter (docs/DOCUMENT_SCANNING.md, section on connecting a provider).
/// The provider key chooses the adapter; <c>mock</c> is the demonstration scanner and is accepted only in Demo and Test, and only when asked for by name (Demo uses it by default).
/// </summary>
public sealed class ScannerProviderOptions
{
    /// <summary>The adapter key: <c>none</c>, <c>mock</c>, or the key a provider adapter registers under. Empty means the mode's default (<c>mock</c> in Demo, <c>none</c> otherwise).</summary>
    public string Provider { get; set; } = "";
    /// <summary>A service address or name for the adapter (a host and port, or a URL). Not a secret.</summary>
    public string? Endpoint { get; set; }
    /// <summary>A secret reference (env:, file: or keyvault:) for the adapter's credential. The value is never stored or shown.</summary>
    public string? SecretRef { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>The largest file the adapter will be asked to scan. A larger file is held, not released.</summary>
    public long MaxFileSizeBytes { get; set; } = 100L * 1024 * 1024;
    /// <summary>Seconds an adapter waits before it tries a failed scan again; the number of tries is <c>Documents:MaxScanAttempts</c>.</summary>
    public int RetryBackoffSeconds { get; set; } = 60;
    /// <summary>How long a passed self-test counts before readiness says the scanner needs testing again.</summary>
    public int SelfTestMaxAgeHours { get; set; } = 24;
    public EcpMode Mode { get; set; } = EcpMode.Production;

    public string EffectiveProvider => string.IsNullOrWhiteSpace(Provider) ? (Mode == EcpMode.Demo ? "mock" : "none") : Provider.Trim().ToLowerInvariant();
}

public enum ScannerState
{
    /// <summary>No scanner is connected: every file is held.</summary>
    NotConnected,
    /// <summary>The demonstration scanner. Allowed in Demo and Test; never protection; never Verified.</summary>
    Demonstration,
    /// <summary>A scanner is connected but has not been tested with the current configuration.</summary>
    NotVerified,
    Healthy,
    Failed,
    Stale,
    /// <summary>The configuration asks for something this mode refuses (the demonstration scanner in Production).</summary>
    Refused,
}

public sealed record ScannerTestResult(DateTime TestedUtc, string Provider, string Outcome, string Code, string Detail, string CorrelationId);

public sealed record ScannerReadinessInfo(ScannerState State, string ProviderName, string ProviderKey, string Summary, string? Code, ScannerTestResult? LastTest, DateTime? ExpiresUtc, bool DemonstrationOnly)
{
    /// <summary>The evidence verdict. A demonstration scanner, a missing scanner and an untested one are Not Verified; only a connected, real scanner that passed its self-test is Passed.</summary>
    public string Verdict => State switch { ScannerState.Healthy => "Passed", ScannerState.Failed or ScannerState.Refused => "Failed", _ => "Not Verified" };
}

/// <summary>What a scanner says about itself when asked whether it is reachable. The default for an adapter that does not implement a health probe is "reachable if connected".</summary>
public sealed record ScannerHealth(bool Reachable, string Detail);

/// <summary>The last self-test, kept in memory only: after a restart the scanner reads Not Verified until it is tested again, which is the honest state.</summary>
public sealed class ScannerTestStore
{
    private volatile ScannerTestResult? _last;
    private volatile string _fingerprint = "";
    public ScannerTestResult? Last => _last;
    public string Fingerprint => _fingerprint;
    public void Set(ScannerTestResult result, string fingerprint) { _last = result; _fingerprint = fingerprint; }
}

public interface IScannerReadiness
{
    Task<ScannerReadinessInfo> EvaluateAsync(CancellationToken ct = default);
    /// <summary>
    /// A provider-neutral test of the connected scanner: reachable, returns Clean for harmless content, and returns Infected for the standard anti-malware test file (EICAR, assembled at run time, harmless). It proves the scanner
    /// responds and detects the standard test file; it does not prove the scanner is good.
    /// </summary>
    Task<ScannerTestResult> RunSelfTestAsync(string by, CancellationToken ct = default);
}

internal sealed class ScannerReadinessService(IMalwareScanner scanner, ScannerProviderOptions options, ScannerTestStore store, IClock clock, IAuditWriter audit, IUnitOfWork uow, TenantContext tenant, IActorContext actor) : IScannerReadiness
{
    /// <summary>The standard anti-malware test string, assembled from parts so this source file is not itself flagged by scanners.</summary>
    internal static byte[] Eicar() => Encoding.ASCII.GetBytes(string.Concat(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$", "EICAR-STANDARD-ANTIVIRUS-TEST-FILE", "!$H+H*"));

    private string Fingerprint => $"{scanner.Name}|{options.EffectiveProvider}|{options.Endpoint}|{options.SecretRef}|{options.TimeoutSeconds}|{options.MaxFileSizeBytes}";

    public Task<ScannerReadinessInfo> EvaluateAsync(CancellationToken ct = default)
    {
        var last = store.Fingerprint == Fingerprint ? store.Last : null;
        ScannerReadinessInfo Info(ScannerState state, string summary, string? code = null, DateTime? expires = null)
            => new(state, scanner.Name, options.EffectiveProvider, summary, code, last, expires, scanner.IsDemonstrationOnly);
        var production = options.Mode == EcpMode.Production;

        if (production && scanner.IsDemonstrationOnly) return Task.FromResult(Info(ScannerState.Refused, "The demonstration scanner is connected in Production. It checks only for a standard test string and is not protection. Connect a real scanner provider.", "SCN-010"));
        if (production && string.Equals(options.EffectiveProvider, "mock", StringComparison.Ordinal)) return Task.FromResult(Info(ScannerState.Refused, "The configuration asks for the demonstration scanner, which Production refuses.", "SCN-010"));
        if (!scanner.IsConnected) return Task.FromResult(Info(ScannerState.NotConnected, production
            ? "No malware scanner is connected. Every uploaded file is held until a scanner provider is connected and tested."
            : "No malware scanner is connected, so uploaded files are held. That is expected in a trial; Production needs a scanner.", "SCN-001"));
        if (scanner.IsDemonstrationOnly)
            return Task.FromResult(Info(ScannerState.Demonstration, options.Mode == EcpMode.Test
                ? "TEST MODE: the demonstration scanner is in use. It checks only for a standard test string and is not protection. It is never counted as a verified scanner."
                : "The demonstration scanner is in use (Demo). It checks only for a standard test string and is not protection.", "SCN-020"));
        if (last is null) return Task.FromResult(Info(ScannerState.NotVerified, "A scanner is connected but has not been tested with these settings. Run the scanner self-test.", "SCN-002"));
        if (last.Outcome != "Passed") return Task.FromResult(Info(ScannerState.Failed, "The last scanner self-test failed: " + last.Detail, last.Code));
        var expires = last.TestedUtc + TimeSpan.FromHours(Math.Max(1, options.SelfTestMaxAgeHours));
        if (clock.UtcNow > expires) return Task.FromResult(Info(ScannerState.Stale, "The last passing scanner self-test is older than the policy allows. Test again.", "SCN-003", expires));
        return Task.FromResult(Info(ScannerState.Healthy, "The scanner is reachable, returned Clean for harmless content and detected the standard test file.", null, expires));
    }

    public async Task<ScannerTestResult> RunSelfTestAsync(string by, CancellationToken ct = default)
    {
        string outcome = "Passed", code = "", detail = "The scanner responded correctly to a harmless sample and to the standard test file.";
        if (!scanner.IsConnected) (outcome, code, detail) = ("NotVerified", "SCN-001", "No scanner is connected, so there is nothing to test.");
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 300)));
            try
            {
                var health = await scanner.CheckHealthAsync(timeout.Token);
                if (!health.Reachable) (outcome, code, detail) = ("Failed", "SCN-004", "The scanner is not reachable: " + Clip(health.Detail));
                else
                {
                    var clean = await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes("ECP scanner self-test: harmless text.")), timeout.Token);
                    if (clean != ScanVerdict.Clean) (outcome, code, detail) = ("Failed", "SCN-005", $"The scanner answered '{clean}' for harmless content; it must answer Clean.");
                    else
                    {
                        var infected = await scanner.ScanAsync(new MemoryStream(Eicar()), timeout.Token);
                        if (infected != ScanVerdict.Infected) (outcome, code, detail) = ("Failed", "SCN-006", $"The scanner answered '{infected}' for the standard anti-malware test file; it must answer Infected.");
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { (outcome, code, detail) = ("Failed", "SCN-007", "The scanner did not answer within the time limit."); }
            catch (Exception ex) when (ex is not OperationCanceledException) { (outcome, code, detail) = ("Failed", "SCN-008", "The scanner could not be reached (" + ex.GetType().Name + ")."); }
        }
        var result = new ScannerTestResult(clock.UtcNow, scanner.Name, outcome, code, detail, actor.CorrelationId);
        store.Set(result, Fingerprint);
        using var _ = tenant.UseInstallationScope();
        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry("operations.scanner.tested", "Scanner", scanner.Name, outcome == "Passed" ? AuditOutcome.Success : AuditOutcome.Failed,
                NewValuesJson: JsonSerializer.Serialize(new { provider = scanner.Name, outcome, code, demonstration = scanner.IsDemonstrationOnly, by })));
            return Task.CompletedTask;
        }, ct);
        return result;
    }

    private static string Clip(string s) => s.Length <= 200 ? s : s[..200];
}
