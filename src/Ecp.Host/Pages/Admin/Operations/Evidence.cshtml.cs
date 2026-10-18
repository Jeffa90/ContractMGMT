using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class EvidenceModel(IOperationsAdministration operations) : EcpPageModel
{
    public EvidenceReport Report { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Report = await operations.EvidenceAsync(ct); return Page(); });

    public Task<IActionResult> OnGetJsonAsync(CancellationToken ct) => RunAsync(async () => File(await operations.EvidenceJsonAsync(ct), "application/json", "recovery-evidence.json"));

    public Task<IActionResult> OnGetCsvAsync(CancellationToken ct) => RunAsync(async () => File(await operations.EvidenceCsvAsync(ct), "text/csv", "recovery-evidence.csv"));

    public Task<IActionResult> OnGetBundleAsync(CancellationToken ct) => RunAsync(async () => File(await operations.SupportBundleAsync(ct), "application/zip", "support-bundle.zip"));
}
