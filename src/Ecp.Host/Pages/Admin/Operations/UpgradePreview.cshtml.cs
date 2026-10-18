using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class UpgradePreviewModel(IUpgradeAdministration upgrades, IOperationsAdministration operations) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? BackupId { get; set; }
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public string Confirmation { get; set; } = "";
    [BindProperty] public string[] Accepted { get; set; } = [];
    [BindProperty] public string? ExpectedManifestSha256 { get; set; }
    [BindProperty] public string? TargetDatabase { get; set; }
    public IReadOnlyList<BackupSummary> Backups { get; private set; } = [];
    public UpgradePlan? Plan { get; private set; }
    public UpgradeResult? Result { get; private set; }
    public RehearsalResult? Rehearsal { get; private set; }

    private async Task LoadAsync(CancellationToken ct)
    {
        Backups = (await operations.BackupsAsync(ct)).Where(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings).ToList();
        if (!string.IsNullOrWhiteSpace(BackupId) && Result is null && Rehearsal is null) Plan = await upgrades.PlanAsync(BackupId, ct);
    }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostApplyAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Result = await upgrades.ApplyAsync(BackupId ?? "", Reason, Confirmation, Accepted, ExpectedManifestSha256, ct);
        await LoadAsync(ct);
        return Page();
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostRehearseAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Rehearsal = await upgrades.RehearseAsync(BackupId ?? "", TargetDatabase ?? "", Reason, Confirmation, Accepted, ct);
        await LoadAsync(ct);
        return Page();
    }, () => LoadAsync(ct));
}
