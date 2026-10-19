using Ecp.Documents;
using Ecp.Operations;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;

namespace Ecp.Host;

/// <summary>
/// R-DOC-01: the malware gate can do its job. In Production an uploaded file is released only by a connected, real scanner that has passed the provider-neutral self-test, so Production without one holds every file (safe, but
/// nobody can use an attachment), with the demonstration scanner would release files nothing really checked, and with skipped scans allowed would release files nobody scanned; each of those is reported as not ready. In Test
/// the demonstration scanner is allowed but is reported as a prominent warning, with the written acceptance when someone recorded one. No real scanner is supplied with this product (docs/DOCUMENT_SCANNING.md).
/// </summary>
public sealed class DocumentScanReadinessCheck(IScannerReadiness readiness, IOptions<DocumentsOptions> options, StartupFacts facts, IMockScannerAcceptance? acceptance = null) : IReadinessCheck
{
    public string Id => "R-DOC-01";
    private const string Description = "Uploaded files are scanned before anyone can use them";

    public async Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        var production = facts.Mode == EcpMode.Production;
        ReadinessResult Result(ReadinessStatus status, string text) => new(Id, Description, status, text);
        var info = await readiness.EvaluateAsync(ct);
        var code = info.Code is { Length: > 0 } c ? $" [{c}]" : "";
        if (production && options.Value.AllowSkippedScan)
            return Result(ReadinessStatus.Unhealthy, "Files whose scan was skipped are allowed to be used. Turn that off so that only files a scanner has called clean are released.");
        if (production)
            return info.State == ScannerState.Healthy ? Result(ReadinessStatus.Healthy, info.Summary)
                : Result(ReadinessStatus.Unhealthy, $"{info.Summary}{code}");
        switch (info.State)
        {
            case ScannerState.Demonstration when facts.Mode == EcpMode.Test:
                var accepted = acceptance is null ? null : await acceptance.CurrentAsync(ct);
                return Result(ReadinessStatus.Degraded, accepted is null
                    ? $"{info.Summary} Nobody has recorded an acceptance of this for a rehearsal.{code}"
                    : $"{info.Summary} Accepted by {accepted.AcceptedBy} on {accepted.AcceptedUtc:yyyy-MM-dd} until {accepted.ExpiresUtc:yyyy-MM-dd} ({accepted.Scope}): {accepted.Reason}{code}");
            case ScannerState.Demonstration: return Result(ReadinessStatus.Healthy, info.Summary);
            case ScannerState.NotConnected: return Result(ReadinessStatus.Degraded, $"{info.Summary}{code}");
            case ScannerState.Healthy: return Result(ReadinessStatus.Healthy, info.Summary);
            default: return Result(ReadinessStatus.Degraded, $"{info.Summary}{code}");
        }
    }
}
