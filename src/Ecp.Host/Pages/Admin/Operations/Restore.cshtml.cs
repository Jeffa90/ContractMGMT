using Ecp.Operations.Domain;
using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.restore")]
public sealed class RestoreModel(IOperationsAdministration operations) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? BackupId { get; set; }
    [BindProperty(SupportsGet = true)] public string? TargetDatabase { get; set; }
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public string Confirmation { get; set; } = "";
    [BindProperty] public bool AllowDifferentEnvironment { get; set; }
    [BindProperty] public string? ExpectedManifestSha256 { get; set; }
    public IReadOnlyList<BackupSummary> Backups { get; private set; } = [];
    public IReadOnlyList<Ecp.Operations.OperationRunDto> History { get; private set; } = [];
    public RestorePreview? Preview { get; private set; }
    public RestoreResult? Result { get; private set; }

    private async Task LoadAsync(CancellationToken ct)
    {
        Backups = (await operations.BackupsAsync(ct)).Where(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings or BackupState.Unverified).ToList();
        History = await operations.RunsAsync(OperationKinds.Restore, ct);
        if (!string.IsNullOrWhiteSpace(BackupId) && !string.IsNullOrWhiteSpace(TargetDatabase)) Preview = await operations.PreviewRestoreAsync(BackupId, TargetDatabase, ct);
    }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostStartAsync(CancellationToken ct) => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(BackupId) || string.IsNullOrWhiteSpace(TargetDatabase)) { ModelState.AddModelError(string.Empty, "Choose a backup and a new target database name."); await LoadAsync(ct); return Page(); }
        Result = await operations.StartRestoreAsync(BackupId, TargetDatabase, Reason, Confirmation, AllowDifferentEnvironment, ExpectedManifestSha256, ct);
        await LoadAsync(ct);
        return Page();
    }, () => LoadAsync(ct));
}
