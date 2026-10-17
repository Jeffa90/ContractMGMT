using Ecp.DataMigration;
using Ecp.DataMigration.Domain;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

public sealed class ProjectModel(IMigrationProjects projects, IMigrationBatches batches, IMappingProfiles profiles, IOrganisationStructureService organisations) : MigrationPageModel
{
    public ProjectDetail Project { get; private set; } = null!;
    public IReadOnlyList<(Guid VersionId, string Label)> ProfileChoices { get; private set; } = [];

    [BindProperty] public ReferencePolicy Policy { get; set; }
    // legacy-load policy
    [BindProperty] public bool AllowRegisteredDate { get; set; }
    [BindProperty] public DateOnly ExpiresDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
    [BindProperty] public string AllowedStatuses { get; set; } = "";
    [BindProperty] public bool AllowOverwrite { get; set; }
    [BindProperty] public string? OverwriteReason { get; set; }
    // cutover freeze
    [BindProperty] public DateTime FreezeStart { get; set; } = DateTime.UtcNow.Date;
    [BindProperty] public DateTime FreezeEnd { get; set; } = DateTime.UtcNow.Date.AddDays(1);
    [BindProperty] public bool TargetFreeze { get; set; }
    // new batch
    [BindProperty] public string? BatchName { get; set; }
    [BindProperty] public BatchKind Kind { get; set; } = BatchKind.DryRun;
    [BindProperty] public DuplicateStrategy Strategy { get; set; } = DuplicateStrategy.SkipExisting;
    [BindProperty] public DocumentDuplicateOption DocumentOption { get; set; } = DocumentDuplicateOption.AddVersion;
    [BindProperty] public bool IsLegacyLoad { get; set; }
    [BindProperty] public Guid? ProfileVersionId { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    public Task<IActionResult> OnPostPolicyAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await projects.SetReferencePolicyAsync(id, Policy, ct); Flash("The reference policy was saved."); });

    public Task<IActionResult> OnPostLegacyAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        var statuses = AllowedStatuses.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        await projects.SetLegacyPolicyAsync(id, new LegacyPolicyInput(AllowRegisteredDate, DateTime.SpecifyKind(ExpiresDate.ToDateTime(TimeOnly.MaxValue), DateTimeKind.Utc), statuses, AllowOverwrite, OverwriteReason), ct);
        Flash("The legacy-load policy was saved. A legacy load also needs a second person to confirm it before it is committed.");
    });

    public Task<IActionResult> OnPostCloseLegacyAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await projects.CloseLegacyPolicyAsync(id, ct); Flash("The legacy-load policy was closed. Further loads of locked records are refused."); });

    public Task<IActionResult> OnPostFreezeAsync(Guid id, CancellationToken ct) => Act(id, ct, async () =>
    {
        await projects.SetFreezeAsync(id, DateTime.SpecifyKind(FreezeStart, DateTimeKind.Utc), DateTime.SpecifyKind(FreezeEnd, DateTimeKind.Utc), TargetFreeze, ct);
        Flash(TargetFreeze ? "The freeze window was saved and the target freeze is ON: people cannot create contracts in this organisation during the window." : "The freeze window was saved; the target freeze is off.");
    });

    public Task<IActionResult> OnPostCloseProjectAsync(Guid id, CancellationToken ct) => Act(id, ct, async () => { await projects.CloseProjectAsync(id, ct); Flash("The [[migration.project|lower]] was closed."); });

    public Task<IActionResult> OnPostCreateBatchAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        var batchId = await batches.CreateAsync(new CreateBatchInput(id, BatchName, Kind, Strategy, DocumentOption, IsLegacyLoad, ProfileVersionId), ct);
        Flash("The [[migration.batch|lower]] was created. Load the source files next.");
        return RedirectToPage("Batch", new { id = batchId });
    }, () => LoadAsync(id, ct));

    private Task<IActionResult> Act(Guid id, CancellationToken ct, Func<Task> action) => RunAsync(async () => { await action(); return RedirectToPage(new { id }); }, () => LoadAsync(id, ct));

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Project = await projects.GetAsync(id, ct);
        await LoadFormatterAsync(organisations, Project.OrganisationId, ct);
        var choices = new List<(Guid, string)>();
        foreach (var p in (await profiles.ListAsync(ct)).Where(p => p.OrganisationId == Project.OrganisationId))
            foreach (var v in (await profiles.GetAsync(p.Id, ct)).Versions.Where(v => v.Status == ProfileVersionStatus.Published)) choices.Add((v.Id, $"{p.Name}, version {v.VersionNumber}"));
        ProfileChoices = choices;
        Policy = Project.ReferencePolicy;
        if (AllowedStatuses.Length == 0) AllowedStatuses = string.Join(", ", Project.LegacyAllowedStatuses);
    }
}
