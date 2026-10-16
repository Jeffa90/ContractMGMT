using Ecp.DataMigration;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Migration.Profiles;

public sealed class IndexModel(IMappingProfiles profiles, IMigrationProjects projects) : MigrationPageModel
{
    public IReadOnlyList<ProfileSummary> Profiles { get; private set; } = [];
    public IReadOnlyList<OrganisationOption> Organisations { get; private set; } = [];
    [BindProperty] public Guid OrganisationId { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string SourceSystem { get; set; } = "";
    [BindProperty] public string? SourceSystemVersion { get; set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var id = await profiles.CreateAsync(OrganisationId, Name, SourceSystem, SourceSystemVersion, ct);
        Flash("The profile was created with an empty draft. Load your field mappings and lookups into it.");
        return RedirectToPage("Details", new { id });
    }, () => LoadAsync(ct));

    private async Task LoadAsync(CancellationToken ct)
    {
        Profiles = await profiles.ListAsync(ct);
        Organisations = await projects.ListOrganisationsAsync(ct);
        if (OrganisationId == Guid.Empty && Organisations.Count > 0) OrganisationId = Organisations[0].Id;
    }
}
