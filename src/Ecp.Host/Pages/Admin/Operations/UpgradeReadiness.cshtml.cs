using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class UpgradeReadinessModel(IUpgradeAdministration upgrades, IOperationsAdministration operations, IEnablementAdministration enablement) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? BackupId { get; set; }
    public IReadOnlyList<BackupSummary> Backups { get; private set; } = [];
    public PreflightReport? Report { get; private set; }
    /// <summary>What the report counts as when used as evidence (D14.13): Ready is Passed, Ready with warnings only when each warning has a recorded acceptance, Blocked is Failed, Not Verified stays Not Verified.</summary>
    public EvidenceJudgement? Judgement { get; private set; }
    public IReadOnlySet<string> AcceptedCodes { get; private set; } = new HashSet<string>();
    public IReadOnlyList<Ecp.Operations.AcceptedExceptionDto> Acceptances { get; private set; } = [];
    [BindProperty] public string Code { get; set; } = "";
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public int ValidDays { get; set; } = 30;

    private async Task LoadAsync(CancellationToken ct)
    {
        Backups = (await operations.BackupsAsync(ct)).Where(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings).ToList();
        Acceptances = await enablement.AcceptedExceptionsAsync(false, ct);
        AcceptedCodes = Acceptances.Where(a => a.Active && a.Kind == Ecp.Operations.AcceptedExceptionKinds.PreflightWarning).Select(a => a.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public Task<IActionResult> OnPostAcceptAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await enablement.AcceptWarningAsync(Ecp.Operations.AcceptedExceptionKinds.PreflightWarning, Code, "installation", Reason, ValidDays, ct);
        Flash($"The warning {Code} was accepted on the record. Run the pre-flight again to see it counted.");
        return RedirectToPage(new { BackupId });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await enablement.RevokeAcceptanceAsync(id, ct);
        Flash("The acceptance was revoked.");
        return RedirectToPage(new { BackupId });
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostRunAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Report = await upgrades.PreflightAsync(string.IsNullOrWhiteSpace(BackupId) ? null : BackupId, ct);
        Judgement = EvidenceSemantics.Judge(Report.Outcome, Report.Checks.Where(c => c.Outcome == CheckOutcome.Warning).Select(c => c.Code), AcceptedCodes);
        return Page();
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnGetJsonAsync(CancellationToken ct) => RunAsync(async () => File(await upgrades.PreflightJsonAsync(string.IsNullOrWhiteSpace(BackupId) ? null : BackupId, ct), "application/json", "upgrade-preflight.json"));

    public Task<IActionResult> OnGetCsvAsync(CancellationToken ct) => RunAsync(async () => File(await upgrades.PreflightCsvAsync(string.IsNullOrWhiteSpace(BackupId) ? null : BackupId, ct), "text/csv", "upgrade-preflight.csv"));
}
