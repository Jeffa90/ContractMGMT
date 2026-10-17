using Ecp.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Suppliers;

[Authorize(Policy = "perm:suppliers.read")]
public sealed class DetailsModel(ISecuredSupplierService suppliers) : EcpPageModel
{
    public SupplierListItem Supplier { get; private set; } = null!;
    public IReadOnlyList<ContractListItem> Contracts { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        Supplier = await suppliers.GetAsync(id, ct);
        await Terms.UseOrganisationAsync(Supplier.OrganisationId, ct);
        Contracts = await suppliers.ContractsOfAsync(id, ct);
        return Page();
    });
}
