using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Contracts;

[Authorize(Policy = "perm:contracts.read")]
public sealed class DetailsModel(ISecuredContractService contracts, ISecuredContractDocuments documents, IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    public ContractDetails Contract { get; private set; } = null!;
    public string OrganisationName { get; private set; } = "";
    public string? BusinessUnitName { get; private set; }
    public string? SiteName { get; private set; }
    public string? Value { get; private set; }
    public string? Start { get; private set; }
    public string? End { get; private set; }
    public string? Registered { get; private set; }
    public string Created { get; private set; } = "";
    public IReadOnlyList<ContractDocumentItem> Documents { get; private set; } = [];
    public bool CanSeeMigration { get; private set; }
    public string? SourceExtracted { get; private set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        Contract = await contracts.GetAsync(id, ct);
        var fmt = await OrgLocale.ForAsync(organisations, Contract.OrganisationId, ct);
        var structure = await organisations.GetAsync(Contract.OrganisationId, ct);
        OrganisationName = (await organisations.GetOrganisationAsync(Contract.OrganisationId, ct))?.Name ?? "";
        BusinessUnitName = structure.BusinessUnits.FirstOrDefault(b => b.Id == Contract.BusinessUnitId)?.Name;
        SiteName = structure.Sites.FirstOrDefault(s => s.Id == Contract.SiteId)?.Name;
        Value = Contract is { EstimatedValue: { } v } ? fmt.Money(v, Contract.CurrencyCode) : null;
        Start = Contract.StartDate is { } s ? fmt.Date(s) : null;
        End = Contract.EndDate is { } e ? fmt.Date(e) : null;
        Registered = Contract.RegisteredDateUtc is { } r ? fmt.DateTime(r) : null;
        Created = fmt.DateTime(Contract.CreatedUtc);
        Documents = await documents.ListAsync(id, ct);
        SourceExtracted = Contract.Provenance?.SourceExtractedUtc is { } x ? fmt.DateTime(x) : null;
        CanSeeMigration = await access.HoldsAnywhereAsync("migration.stage", ct);
        return Page();
    });
}
