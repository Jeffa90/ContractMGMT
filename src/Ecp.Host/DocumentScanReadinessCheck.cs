using Ecp.Documents;
using Ecp.Operations;
using Microsoft.Extensions.Options;

namespace Ecp.Host;

/// <summary>
/// R-DOC-01: the malware gate can do its job. In Production an uploaded file is released only by a connected, real scanner, so Production without one holds every file (safe, but nobody can use an attachment), with the
/// demonstration scanner would release files nothing really checked, and with skipped scans allowed would release files nobody scanned; each of those is reported as not ready. Elsewhere it says what the scanner is.
/// No real scanner is supplied with this product: a provider adapter has to be added before Production (docs/DOCUMENT_SCANNING.md).
/// </summary>
public sealed class DocumentScanReadinessCheck(IMalwareScanner scanner, IOptions<DocumentsOptions> options, Ecp.Operations.StartupFacts facts) : IReadinessCheck
{
    public string Id => "R-DOC-01";
    private const string Description = "Uploaded files are scanned before anyone can use them";

    public Task<ReadinessResult> RunAsync(CancellationToken ct)
    {
        var production = facts.Mode == Ecp.SharedKernel.EcpMode.Production;
        ReadinessResult Result(ReadinessStatus status, string text) => new(Id, Description, status, text);
        if (production && !scanner.IsConnected)
            return Task.FromResult(Result(ReadinessStatus.Unhealthy, "No malware scanner is connected. Every uploaded file is held and cannot be downloaded, copied or counted as evidence until a scanner provider is connected."));
        if (production && scanner.IsDemonstrationOnly)
            return Task.FromResult(Result(ReadinessStatus.Unhealthy, "The demonstration scanner is connected. It checks only for a standard test string and is not protection. Connect a real scanner provider."));
        if (production && options.Value.AllowSkippedScan)
            return Task.FromResult(Result(ReadinessStatus.Unhealthy, "Files whose scan was skipped are allowed to be used. Turn that off so that only files a scanner has called clean are released."));
        if (!scanner.IsConnected)
            return Task.FromResult(Result(ReadinessStatus.Degraded, "No malware scanner is connected, so uploaded files are held. That is expected in a trial; Production needs a scanner."));
        return Task.FromResult(Result(ReadinessStatus.Healthy, scanner.IsDemonstrationOnly ? "The demonstration scanner is connected (trial only)." : $"Scanner '{scanner.Name}' is connected."));
    }
}
