using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class UpgradeReadinessModel(IUpgradeAdministration upgrades, IOperationsAdministration operations) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? BackupId { get; set; }
    public IReadOnlyList<BackupSummary> Backups { get; private set; } = [];
    public PreflightReport? Report { get; private set; }

    private async Task LoadAsync(CancellationToken ct) => Backups = (await operations.BackupsAsync(ct)).Where(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings).ToList();

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostRunAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Report = await upgrades.PreflightAsync(string.IsNullOrWhiteSpace(BackupId) ? null : BackupId, ct);
        return Page();
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnGetJsonAsync(CancellationToken ct) => RunAsync(async () => File(await upgrades.PreflightJsonAsync(string.IsNullOrWhiteSpace(BackupId) ? null : BackupId, ct), "application/json", "upgrade-preflight.json"));

    public Task<IActionResult> OnGetCsvAsync(CancellationToken ct) => RunAsync(async () => File(await upgrades.PreflightCsvAsync(string.IsNullOrWhiteSpace(BackupId) ? null : BackupId, ct), "text/csv", "upgrade-preflight.csv"));
}
