using Ecp.Configuration;
using Ecp.Setup.Organisations;
using Ecp.SharedKernel.Localisation;
using Ecp.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Organisations;

/// <summary>
/// The organisation wizard on one page. "Check" shows what would happen and every problem against its field, and changes nothing; "Create" does it. The service repeats every check, so the page decides nothing.
/// </summary>
[Authorize(Policy = "perm:organisations.manage")]
public sealed class NewModel(IOrganisationCreation creation, ISolutionPackCatalogue catalogue) : EcpPageModel
{
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? LegalName { get; set; }
    [BindProperty] public string? Key { get; set; }
    [BindProperty] public string? Country { get; set; }
    [BindProperty] public string? Culture { get; set; }
    [BindProperty] public string? TimeZone { get; set; }
    [BindProperty] public string? Currency { get; set; }
    [BindProperty] public string? ReferenceSeparator { get; set; } = "-";
    [BindProperty] public int ReferenceDigits { get; set; } = 6;
    [BindProperty] public string? PackKey { get; set; }
    [BindProperty] public Guid? InitialAdministratorId { get; set; }
    [BindProperty] public string? StructureCsv { get; set; }

    public OrganisationCreationPlan? Plan { get; private set; }
    public NewOrganisationResult? Created { get; private set; }
    public IReadOnlyList<PersonName> People { get; private set; } = [];
    public IReadOnlyList<(string Key, string Label)> Packs { get; private set; } = [];
    public IReadOnlyList<KeyValuePair<string, string>> Countries { get; } = RegionalCatalogue.Countries.OrderBy(c => c.Value, StringComparer.Ordinal).ToList();
    public bool IsSingle { get; private set; }

    public string ErrorFor(string field) => Plan is null ? "" : string.Join(" ", Plan.Problems.Where(p => p.IsError && p.Field == field).Select(p => p.Message));

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        People = await creation.CandidateAdministratorsAsync(ct);
        Packs = catalogue.ListEntries().Where(e => e.IsUsable && !e.Retired).GroupBy(e => e.PackKey).Select(g => g.First()).OrderBy(e => e.PackKey, StringComparer.Ordinal)
            .Select(e => (e.PackKey, e.DisplayName.Resolve("en") + " (" + e.PackKey + ")")).ToList();
        IsSingle = (await creation.TenancyAsync(ct)).IsSingleOrganisation;
    }

    private NewOrganisationInput Input() => new(Name ?? "", LegalName ?? "", Key ?? "", Country, Culture ?? "", TimeZone ?? "", Currency ?? "", ReferenceSeparator ?? "-", ReferenceDigits,
        string.IsNullOrWhiteSpace(PackKey) ? null : PackKey, InitialAdministratorId, StructureCsv);

    public Task<IActionResult> OnPostCheckAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        Plan = await creation.PreviewAsync(Input(), ct);
        return Page();
    }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostCreateAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        Created = await creation.CreateAsync(Input(), ct);
        await LoadAsync(ct);
        return Page();
    }, () => LoadAsync(ct));
}
