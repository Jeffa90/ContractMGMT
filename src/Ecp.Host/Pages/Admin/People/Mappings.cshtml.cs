using Ecp.Access;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.People;

/// <summary>
/// Identity group mappings (Advanced). Explains what a mapping is for, lists the existing ones in words, and adds one through a short form that is previewed in a sentence before it is saved. The service
/// requires the right to manage mappings, applies the delegation rules and keeps the choice of access to a closed, safe list.
/// </summary>
[Authorize(Policy = "perm:rolemappings.manage")]
public sealed class MappingsModel(IGroupMappingAdministration mappings, IOrganisationStructureService organisations, ICategoryAdministration categoryAdmin, IAccessService access) : EcpPageModel
{
    [BindProperty] public string? ProviderKey { get; set; }
    [BindProperty] public string? GroupName { get; set; }
    [BindProperty] public string? RoleCode { get; set; }
    [BindProperty] public Guid OrganisationId { get; set; }
    [BindProperty] public string Scope { get; set; } = nameof(ScopeChoice.WholeOrganisation);
    [BindProperty] public List<Guid> ScopeIds { get; set; } = [];
    [BindProperty] public string? Ceiling { get; set; }

    public IReadOnlyList<MappingRow> Rows { get; private set; } = [];
    public IReadOnlyList<ProviderChoice> Providers { get; private set; } = [];
    public IReadOnlyList<MappingRole> Roles => mappings.Roles;
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<Choice> Places { get; private set; } = [];
    public IReadOnlyList<string> Levels { get; private set; } = [];
    public string DefaultLevel { get; private set; } = "";
    public MappingPreview? Preview { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private MappingRequest AsRequest() => new(ProviderKey ?? "", GroupName ?? "", RoleCode ?? "", OrganisationId, Enum.TryParse<ScopeChoice>(Scope, out var s) ? s : ScopeChoice.WholeOrganisation, ScopeIds, Ceiling);

    public Task<IActionResult> OnPostPreviewAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Preview = await mappings.PreviewAsync(AsRequest(), ct);
        return Page();
    }, reload: () => LoadAsync(ct));

    public Task<IActionResult> OnPostSaveAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        await mappings.AddAsync(AsRequest(), ct);
        Flash("The mapping is saved. People in that group get the access at their next sign-in.");
        return RedirectToPage();
    }, reload: () => LoadAsync(ct));

    public Task<IActionResult> OnPostEndAsync(Guid id, string? reason, CancellationToken ct) => StepUpRunAsync(async () =>
    {
        await mappings.EndAsync(id, string.IsNullOrWhiteSpace(reason) ? "Ended from the mappings screen" : reason, ct);
        Flash("The mapping is ended. Access it already gave ends for each person when they next sign in.");
        return RedirectToPage();
    }, reload: () => LoadAsync(ct));

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await mappings.ListAsync(ct);
        Providers = await mappings.ProvidersAsync(ct);
        var grants = await access.GrantsAsync("rolemappings.manage", ct);
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (OrganisationId == Guid.Empty && Organisations.Count > 0) OrganisationId = Organisations[0].Id;
        var scale = await access.ClassificationScaleAsync(ct);
        Levels = scale.Levels.Take(scale.HighCeilingThreshold).ToList(); DefaultLevel = scale.DefaultLevel;
        if (OrganisationId != Guid.Empty && Organisations.Any(o => o.Id == OrganisationId))
        {
            var s = await organisations.GetAsync(OrganisationId, ct);
            var places = new List<Choice>();
            places.AddRange(s.Divisions.Where(d => d.IsActive).Select(d => new Choice(d.Id, Terms.Expand("[[division]]") + ": " + d.Name)));
            places.AddRange(s.BusinessUnits.Where(b => b.IsActive).Select(b => new Choice(b.Id, Terms.Expand("[[business.unit]]") + ": " + b.Name)));
            places.AddRange(s.Sites.Where(x => x.IsActive).Select(x => new Choice(x.Id, Terms.Expand("[[site]]") + ": " + x.Name)));
            places.AddRange((await categoryAdmin.ListAllAsync(ct)).Where(c => c.OrganisationId == OrganisationId && c.IsActive).Select(c => new Choice(c.Id, Terms.Expand("[[category]]") + ": " + c.Name)));
            Places = places;
        }
    }
}
