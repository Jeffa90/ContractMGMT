using System.ComponentModel.DataAnnotations;
using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class BackupsModel(IOperationsAdministration operations) : EcpPageModel
{
    public IReadOnlyList<BackupSummary> Backups { get; private set; } = [];
    public OperationsOverview Overview { get; private set; } = null!;
    [BindProperty, Required, StringLength(500)] public string Reason { get; set; } = "";
    [BindProperty] public bool HoldMaintenance { get; set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct) { Overview = await operations.OverviewAsync(ct); Backups = await operations.BackupsAsync(ct); }

    public Task<IActionResult> OnPostStartAsync(CancellationToken ct) => RunAsync(async () =>
    {
        if (!ModelState.IsValid) { await LoadAsync(ct); return Page(); }
        var result = await operations.StartBackupAsync(Reason, HoldMaintenance, ct);
        if (result.Succeeded) Flash($"Backup {result.BackupId} was created and its integrity was checked ({result.Outcome}).");
        else Flash($"Backup {result.BackupId} failed and is not a recovery point. {result.Failure}");
        return RedirectToPage("/Admin/Operations/BackupDetails", new { id = result.BackupId });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostVerifyAsync(string id, CancellationToken ct) => RunAsync(async () =>
    {
        var result = await operations.VerifyBackupAsync(id, ct);
        Flash($"Verification of {id}: {result.Outcome}.");
        return RedirectToPage("/Admin/Operations/BackupDetails", new { id });
    }, () => LoadAsync(ct));
}
