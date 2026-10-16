using Ecp.DataMigration;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration;

public sealed class IndexModel(IMigrationProjects projects) : MigrationPageModel
{
    public IReadOnlyList<ProjectSummary> Projects { get; private set; } = [];
    public IReadOnlyList<OrganisationOption> Organisations { get; private set; } = [];

    [BindProperty] public Guid OrganisationId { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string SourceSystem { get; set; } = "";
    [BindProperty] public string? SourceSystemVersion { get; set; }
    [BindProperty] public string DataOwner { get; set; } = "";
    [BindProperty] public DateOnly ExtractionDate { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var id = await projects.CreateAsync(new CreateProjectInput(OrganisationId, Name, SourceSystem, SourceSystemVersion, DataOwner, DateTime.SpecifyKind(ExtractionDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)), ct);
        Flash("The migration project was created. Next, set the reference policy and, if needed, the legacy-load policy, then create a batch.");
        return RedirectToPage("Project", new { id });
    }, () => LoadAsync(ct));

    private async Task LoadAsync(CancellationToken ct)
    {
        Projects = await projects.ListAsync(ct);
        Organisations = await projects.ListOrganisationsAsync(ct);
        if (OrganisationId == Guid.Empty && Organisations.Count > 0) OrganisationId = Organisations[0].Id;
    }
}
