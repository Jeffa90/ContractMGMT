using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class RollbackEvidenceModel(IUpgradeAdministration upgrades, IOperationsAdministration operations) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? BackupId { get; set; }
    [BindProperty] public string? TargetDatabase { get; set; }
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public string Confirmation { get; set; } = "";
    public IReadOnlyList<BackupSummary> Backups { get; private set; } = [];
    public RollbackEvidenceSummary? Last { get; private set; }
    public RollbackEvidenceResult? Result { get; private set; }

    private async Task LoadAsync(CancellationToken ct)
    {
        Backups = (await operations.BackupsAsync(ct)).Where(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings).ToList();
        Last = await upgrades.LastRollbackEvidenceAsync(ct);
    }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostProveAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Result = await upgrades.ProveRollbackAsync(BackupId ?? "", TargetDatabase ?? "", Reason, Confirmation, ct);
        await LoadAsync(ct);
        return Page();
    }, () => LoadAsync(ct));
}
