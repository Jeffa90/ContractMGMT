using Ecp.Approvals;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Approvals;

/// <summary>
/// Describe a sample submission, pick the version of the matrix to try (in force, a numbered release or your open draft) and a date, and see exactly what the platform would do: the matching rows, the steps, the people,
/// and why each person is in or out. Nothing is stored except an audit entry for the run. The sample's categories, units and sites are chosen from the organisation's own data.
/// </summary>
[Authorize(Policy = "perm:approvals.simulate")]
public sealed class SimulateModel(IApprovalSimulator simulator, IApprovalSimulationInputs inputs, IOrganisationStructureService organisations, IPeopleDirectory people, IAccessService access) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string? MatrixCode { get; set; }
    [BindProperty] public string Source { get; set; } = "published";
    [BindProperty] public int? ReleaseNumber { get; set; }
    [BindProperty] public DateOnly? AsOf { get; set; }
    [BindProperty] public decimal? Value { get; set; }
    [BindProperty] public string? Currency { get; set; }
    [BindProperty] public Guid? CategoryId { get; set; }
    [BindProperty] public Guid? BusinessUnitId { get; set; }
    [BindProperty] public Guid? SiteId { get; set; }
    [BindProperty] public Guid? LegalEntityId { get; set; }
    [BindProperty] public string? Classification { get; set; }
    [BindProperty] public string? ContractType { get; set; }
    [BindProperty] public string? RiskLevel { get; set; }
    [BindProperty] public string? SupplierType { get; set; }
    [BindProperty] public string? CustomFields { get; set; }
    [BindProperty] public Guid? SubmitterId { get; set; }

    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public SimulationChoices Choices { get; private set; } = new([], [], [], [], []);
    public IReadOnlyList<MatrixSummary> Matrices { get; private set; } = [];
    public IReadOnlyList<PersonItem> People { get; private set; } = [];
    public SimulationResult? Result { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Organisations = await OrganisationChoices.AllowedAsync(organisations, access, ct, "approvals.simulate");
        OrganisationId ??= Organisations.FirstOrDefault()?.Id;
        if (OrganisationId is not { } org) return;
        Choices = await inputs.ChoicesAsync(org, ct);
        try { Matrices = await simulator.MatricesAsync(org, MatrixSource.Published, ct); } catch (Ecp.SharedKernel.ForbiddenException) { Matrices = []; }
        try { People = await people.ManageableUsersAsync(ct); } catch (Ecp.SharedKernel.ForbiddenException) { People = []; }
        MatrixCode ??= Matrices.FirstOrDefault()?.Code;
        Classification ??= Choices.Classifications.FirstOrDefault(c => c.Equals("Internal", StringComparison.OrdinalIgnoreCase)) ?? Choices.Classifications.FirstOrDefault();
    }

    private static IReadOnlyDictionary<string, string?> ParseCustom(string? text)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50))
        {
            var i = line.IndexOf('=');
            if (i > 0) result[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return result;
    }

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => RunAsync(async () =>
    {
        await LoadAsync(ct);
        if (OrganisationId is not { } org) return Page();
        var subject = await inputs.BuildAsync(new SimulationInputs(org, Value, Currency, CategoryId, BusinessUnitId, SiteId, LegalEntityId, Classification, ContractType, RiskLevel, SupplierType, ParseCustom(CustomFields)), ct);
        var source = Source switch { "release" => MatrixSource.Release(ReleaseNumber ?? 0), "draft" => MatrixSource.Draft, _ => MatrixSource.Published };
        Result = await simulator.SimulateAsync(new SimulationRequest(org, MatrixCode ?? "", subject.Facts, subject.Resource, SubmitterId, AsOf, source), ct);
        return Page();
    }, () => LoadAsync(ct));
}
