using Ecp.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Documents;

/// <summary>The malware gate: which scanner is connected, what is held and why, and a retry for a file whose scan failed. A file the scanner called infected stays in quarantine; nothing on this page releases it.</summary>
[Authorize(Policy = "perm:notifications.administer")]
public sealed class ScanningModel(IDocumentScanning scanning) : EcpPageModel
{
    public ScanOverview? Overview { get; private set; }
    public IReadOnlyList<ScanItem> Held { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Overview = await scanning.OverviewAsync(ct);
        Held = await scanning.HeldAsync(200, ct);
    }

    public Task<IActionResult> OnPostRescanAsync(Guid versionId, CancellationToken ct) => StepUpRunAsync(async () =>
    {
        var status = await scanning.RescanOneAsync(versionId, ct);
        Flash(status == Ecp.Documents.Domain.ScanStatus.Clean ? "The file was scanned and released." : $"The file was scanned again. It is {status.ToString().ToLowerInvariant()} and stays held.");
        return RedirectToPage();
    }, () => LoadAsync(ct));
}
