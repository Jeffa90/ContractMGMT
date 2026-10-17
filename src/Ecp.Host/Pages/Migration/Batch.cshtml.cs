using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

/// <summary>
/// The migration wizard for one batch: load sources, choose the profile, dry run, review, confirm and commit, reconcile, accept, roll back. Each step is a service call that
/// authorises on the server; the page only offers what the caller may do and explains what each step does.
/// </summary>
[RequestSizeLimit(2_147_483_648)]
[RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
public sealed class BatchModel(IMigrationBatches batches, IMigrationIntake intake, IMigrationValidation validation, IMigrationCommit commit, IMigrationRollback rollback,
    IMigrationReports reports, IMappingProfiles profiles, IOrganisationStructureService organisations) : MigrationPageModel
{
    public BatchDetail Batch { get; private set; } = null!;
    public IReadOnlyList<(Guid VersionId, string Label)> ProfileChoices { get; private set; } = [];
    public RollbackPreview? Preview { get; private set; }
    public ReconciliationDto? Reconciliation { get; private set; }
    public string? ProjectName => Batch.ProjectName;

    [BindProperty] public string Delimiter { get; set; } = "";
    [BindProperty] public string Encoding { get; set; } = "";
    [BindProperty] public string? Sheet { get; set; }
    [BindProperty] public Guid ProfileVersionId { get; set; }
    [BindProperty] public DuplicateStrategy Strategy { get; set; }
    [BindProperty] public DocumentDuplicateOption DocumentOption { get; set; }
    [BindProperty] public string? Reason { get; set; }
    [BindProperty] public string? Note { get; set; }
    [BindProperty] public bool Acknowledge { get; set; }
    [BindProperty] public bool ApproveExceptions { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    private UploadOptions Options() => new(Delimiter switch { "comma" => ',', "semicolon" => ';', "tab" => '\t', "pipe" => '|', _ => null }, string.IsNullOrWhiteSpace(Encoding) ? null : Encoding, string.IsNullOrWhiteSpace(Sheet) ? null : Sheet.Trim());

    public Task<IActionResult> OnPostUploadContractsAsync(Guid id, IFormFile? file, CancellationToken ct) => Upload(id, file, ct, async s =>
    {
        var dto = await intake.UploadContractsAsync(id, s, file!.FileName, Options(), ct);
        Flash($"The [[contract|plural|lower]] file was read: {dto.RowCount} row(s) are in staging with their source values kept. Next, make sure a profile is chosen and run the dry run.");
    });

    public Task<IActionResult> OnPostUploadManifestAsync(Guid id, IFormFile? file, CancellationToken ct) => Upload(id, file, ct, async s =>
    {
        var dto = await intake.UploadManifestAsync(id, s, file!.FileName, Options(), ct);
        Flash($"The attachment manifest was read: {dto.RowCount} row(s).");
    });

    public Task<IActionResult> OnPostUploadArchiveAsync(Guid id, IFormFile? file, CancellationToken ct) => Upload(id, file, ct, async s =>
    {
        var dto = await intake.UploadArchiveAsync(id, s, file!.FileName, ct);
        Flash($"The archive was checked and its {dto.RowCount} entries are held in restricted staging. Nothing has been imported.");
    });

    private Task<IActionResult> Upload(Guid id, IFormFile? file, CancellationToken ct, Func<Stream, Task> action) => RunAsync(async () =>
    {
        if (file is null || file.Length == 0) throw new Ecp.SharedKernel.DomainException("file_required", "Choose a file to load.");
        await using var stream = file.OpenReadStream();
        await action(stream);
        return RedirectToPage(new { id });
    }, () => LoadAsync(id, ct));

    public Task<IActionResult> OnPostSelectProfileAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await batches.SelectProfileAsync(id, ProfileVersionId, ct); Flash("The profile version was chosen. Run the dry run again to see its effect."); });

    public Task<IActionResult> OnPostStrategiesAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await batches.SetStrategiesAsync(id, Strategy, DocumentOption, ct); Flash("The duplicate options were saved. Run the dry run again."); });

    public Task<IActionResult> OnPostValidateAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        var summary = await validation.ValidateAsync(id, ct);
        if (summary.Blockers.Count > 0) { foreach (var b in summary.Blockers.Take(6)) ModelState.AddModelError(string.Empty, b); throw new Ecp.SharedKernel.DomainException("blocked", "The dry run could not start. Fix these first."); }
        Flash($"The dry run is complete: {summary.Accepted} row(s) would load, {summary.Rejected} would be rejected, {summary.Skipped} skipped. Nothing was written to the platform.");
    });

    public Task<IActionResult> OnPostConfirmAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await commit.ConfirmLegacyLoadAsync(id, ct); Flash("You confirmed this legacy load. Someone else must now commit it."); });

    public Task<IActionResult> OnPostCommitAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        var progress = await commit.CommitAsync(id, ct);
        if (progress.Status == BatchStatus.Failed) Flash("The commit stopped: " + progress.FailureReason + " Nothing was lost or duplicated; fix the cause and commit again to resume.");
        else Flash("The [[migration.batch|lower]] was committed. Reconcile the totals next.");
    });

    public Task<IActionResult> OnPostReconcileAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        var r = await commit.ReconcileAsync(id, ApproveExceptions, Note, ct);
        Flash(r.Balanced ? "The totals balance. The batch is reconciled." : r.ExceptionsApproved ? "The exceptions were approved and recorded. The batch is reconciled." : "The totals do not balance. See the reconciliation report.");
    });

    public Task<IActionResult> OnPostAcceptAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await commit.AcceptAsync(id, new SampleCheckInput(Note ?? "", Acknowledge), ct); Flash("Business acceptance was recorded. The [[migration.batch|lower]] is final and can no longer be rolled back."); });

    public Task<IActionResult> OnPostRollbackAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        var r = await rollback.RollBackAsync(id, Reason ?? "", ct);
        Flash($"The [[migration.batch|lower]] was rolled back: {r.Reversed} reversed, {r.Archived} archived, {r.Retained} kept, {r.NotReversible} could not be reversed.");
    });

    public Task<IActionResult> OnPostCompensateAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        var r = await rollback.CompensateAsync(id, Reason ?? "", ct);
        Flash($"Compensating archival was applied: {r.Archived} record(s) archived. Nothing was deleted.");
    });

    public Task<IActionResult> OnPostPurgeAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { var n = await rollback.PurgeStagingAsync(id, ct); Flash($"{n} staged copy(ies) were deleted."); });

    public Task<IActionResult> OnGetErrorsXlsxAsync(Guid id, CancellationToken ct) => RunAsync(async () => Download(await reports.ExportErrorsAsync(id, ExportFormat.Xlsx, ct)));
    public Task<IActionResult> OnGetErrorsCsvAsync(Guid id, CancellationToken ct) => RunAsync(async () => Download(await reports.ExportErrorsAsync(id, ExportFormat.Csv, ct)));
    public Task<IActionResult> OnGetProvenanceXlsxAsync(Guid id, CancellationToken ct) => RunAsync(async () => Download(await reports.ExportProvenanceAsync(id, ExportFormat.Xlsx, ct)));
    public Task<IActionResult> OnGetReconciliationXlsxAsync(Guid id, CancellationToken ct) => RunAsync(async () => Download(await reports.ExportReconciliationAsync(id, ExportFormat.Xlsx, ct)));

    private Task<IActionResult> Act(Guid id, CancellationToken ct, Func<Task> action) => RunAsync(async () => { await action(); return RedirectToPage(new { id }); }, () => LoadAsync(id, ct));

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Batch = await batches.GetAsync(id, ct);
        await LoadFormatterAsync(organisations, Batch.OrganisationId, ct);
        var choices = new List<(Guid, string)>();
        foreach (var p in (await profiles.ListAsync(ct)).Where(p => p.OrganisationId == Batch.OrganisationId))
            foreach (var v in (await profiles.GetAsync(p.Id, ct)).Versions.Where(v => v.Status == ProfileVersionStatus.Published)) choices.Add((v.Id, $"{p.Name}, version {v.VersionNumber}"));
        ProfileChoices = choices;
        Strategy = Batch.DuplicateStrategy; DocumentOption = Batch.DocumentOption;
        if (Batch.Status is BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Accepted) Reconciliation = await commit.PreviewReconciliationAsync(id, ct);
        if (Batch.Can.CanRollBack) Preview = await rollback.PreviewAsync(id, ct);
    }
}
