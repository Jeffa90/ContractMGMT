using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Suppliers;

[Authorize(Policy = "perm:suppliers.read")]
public sealed class IndexModel(ISecuredSupplierService suppliers, IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    public IReadOnlyList<SupplierListItem> Suppliers { get; private set; } = [];
    public bool CanManage { get; private set; }
    private Dictionary<Guid, string> _orgs = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Suppliers = await suppliers.ListAsync(Q, ct);
        CanManage = await access.HoldsAnywhereAsync("suppliers.manage", ct);
        _orgs = (await organisations.ListOrganisationsAsync(ct)).ToDictionary(o => o.Id, o => o.Name);
        return Page();
    }

    public string OrganisationName(Guid id) => _orgs.TryGetValue(id, out var n) ? n : "";
}
