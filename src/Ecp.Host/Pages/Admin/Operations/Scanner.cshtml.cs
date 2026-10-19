using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

/// <summary>The malware scanner as a whole: how it is configured, whether it has been tested, and, in Test only, the written acceptance of the demonstration scanner for a masked-data rehearsal (G-25).</summary>
[Authorize(Policy = "perm:operations.read")]
public sealed class ScannerModel(IEnablementAdministration enablement) : EcpPageModel
{
    public ScannerAdminView View { get; private set; } = null!;
    [BindProperty] public string Scope { get; set; } = "masked-data rehearsal";
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public int ValidDays { get; set; } = 14;

    private async Task LoadAsync(CancellationToken ct) => View = await enablement.ScannerAsync(ct);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostTestAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var r = await enablement.TestScannerAsync(ct);
        Flash(r.Outcome == "Passed" ? "The scanner self-test passed." : r.Outcome == "NotVerified" ? "The scanner self-test could not run: " + r.Detail : "The scanner self-test failed: " + r.Detail + $" (reference {r.CorrelationId})");
        return RedirectToPage();
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostAcceptAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await enablement.AcceptMockScannerAsync(Scope, Reason, ValidDays, ct);
        Flash("The use of the demonstration scanner was accepted for this rehearsal. It is still not a real scanner.");
        return RedirectToPage();
    }, () => LoadAsync(ct));
}
