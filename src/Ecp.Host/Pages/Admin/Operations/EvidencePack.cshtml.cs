using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

/// <summary>The company implementation evidence pack: what the platform can state about this implementation, with Verified / Warning / Failed / Not Verified kept distinct. It never declares readiness.</summary>
[Authorize(Policy = "perm:operations.evidence.export")]
public sealed class EvidencePackModel(IImplementationEvidence evidence) : EcpPageModel
{
    public ImplementationEvidencePack Pack { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Pack = await evidence.BuildAsync(ct); return Page(); });

    public Task<IActionResult> OnGetDownloadAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var package = await evidence.ExportAsync(ct);
        Response.Headers["X-Evidence-Pack-Sha256"] = package.ZipSha256;
        return File(package.Zip, "application/zip", $"implementation-evidence-{package.Pack.GeneratedUtc:yyyyMMddTHHmmss}Z.zip");
    });
}
