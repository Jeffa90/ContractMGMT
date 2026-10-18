using Ecp.Operations;
using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class LeasesModel(IOperationsAdministration operations) : EcpPageModel
{
    public IReadOnlyList<LeaseInfo> Leases { get; private set; } = [];
    [BindProperty] public string Reason { get; set; } = "";

    private async Task LoadAsync(CancellationToken ct) => Leases = await operations.LeasesAsync(ct);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostTakeOverAsync(string name, string scope, CancellationToken ct) => RunAsync(async () =>
    {
        await operations.TakeOverLeaseAsync(name, scope, Reason, ct);
        Flash("The lease was taken over and released. The previous owner's next renewal will fail, so it stops.");
        return RedirectToPage();
    }, () => LoadAsync(ct));
}
