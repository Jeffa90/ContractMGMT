using Ecp.Operations;
using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class MaintenanceModel(IOperationsAdministration operations) : EcpPageModel
{
    public MaintenanceStatus Status { get; private set; } = MaintenanceStatus.Off;
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public bool AcknowledgeRestoreState { get; set; }

    private async Task LoadAsync(CancellationToken ct) => Status = await operations.MaintenanceAsync(ct);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostEnterAsync(CancellationToken ct) => RunAsync(async () => { await operations.EnterMaintenanceAsync(Reason, ct); Flash("Maintenance mode is on. Changes are paused."); return RedirectToPage(); }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostLeaveAsync(CancellationToken ct) => RunAsync(async () => { await operations.LeaveMaintenanceAsync(Reason, AcknowledgeRestoreState, ct); Flash("Maintenance mode is off."); return RedirectToPage(); }, () => LoadAsync(ct));
}
