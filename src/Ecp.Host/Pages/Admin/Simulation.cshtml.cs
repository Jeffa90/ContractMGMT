using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

[Authorize(Policy = "perm:users.manage")]
public sealed class SimulationModel(IAccessSimulator simulator, IPeopleDirectory people, IOrganisationStructureService structure, IAccessService access) : EcpPageModel
{
    [BindProperty] public Guid UserId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid OrganisationId { get; set; }
    [BindProperty] public Guid? CategoryId { get; set; }
    [BindProperty] public Guid? BusinessUnitId { get; set; }
    [BindProperty] public Guid? SiteId { get; set; }
    [BindProperty] public string Classification { get; set; } = "";
    [BindProperty] public bool Locked { get; set; }

    public IReadOnlyList<PersonItem> People { get; private set; } = [];
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<CategoryItem> Categories { get; private set; } = [];
    public IReadOnlyList<BusinessUnitDto> BusinessUnits { get; private set; } = [];
    public IReadOnlyList<SiteDto> Sites { get; private set; } = [];
    public IReadOnlyList<string> Levels { get; private set; } = [];
    public IReadOnlyList<SimulationResult> Results { get; private set; } = [];
    public string UserName { get; private set; } = "";

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        var subject = await simulator.SubjectOfAsync(UserId, ct);
        UserName = People.FirstOrDefault(p => p.Id == UserId)?.DisplayName ?? "";
        Results = await simulator.SimulateAsync(subject, new SampleRecord(OrganisationId, CategoryId, BusinessUnitId, SiteId, Classification, Locked), ct);
        return Page();
    });

    private async Task LoadAsync(CancellationToken ct)
    {
        People = await people.ManageableUsersAsync(ct);
        Organisations = await structure.ListOrganisationsAsync(ct);
        if (OrganisationId == Guid.Empty && Organisations.Count > 0) OrganisationId = Organisations[0].Id;
        var s = OrganisationId == Guid.Empty ? null : await structure.GetAsync(OrganisationId, ct);
        BusinessUnits = s?.BusinessUnits ?? []; Sites = s?.Sites ?? [];
        Categories = OrganisationId == Guid.Empty ? [] : await simulator.CategoriesOfAsync(OrganisationId, ct);
        var scale = await access.ClassificationScaleAsync(ct);
        Levels = scale.Levels;
        if (string.IsNullOrEmpty(Classification)) Classification = scale.DefaultLevel;
        if (UserId == Guid.Empty && People.Count > 0) UserId = People[0].Id;
    }
}
