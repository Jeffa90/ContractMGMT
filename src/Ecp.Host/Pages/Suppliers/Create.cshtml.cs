using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Suppliers;

[Authorize(Policy = "perm:suppliers.manage")]
public sealed class CreateModel(ISecuredSupplierService suppliers, IOrganisationStructureService organisations, IAccessService access) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? VendorNumber { get; set; }
    [BindProperty] public string? Abn { get; set; }
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) { await LoadAsync(ct); return Page(); }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return await RunAsync(async () =>
        {
            if (OrganisationId is null || string.IsNullOrWhiteSpace(Name)) { ModelState.AddModelError(nameof(Name), "Enter the supplier name."); return Page(); }
            var id = await suppliers.CreateAsync(new CreateSupplierRequest(OrganisationId.Value, Name, string.IsNullOrWhiteSpace(Abn) ? null : Abn, string.IsNullOrWhiteSpace(VendorNumber) ? null : VendorNumber), ct);
            Flash("Supplier added.");
            return RedirectToPage("Details", new { id });
        });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("suppliers.manage", ct);
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (OrganisationId is null && Organisations.Count > 0) OrganisationId = Organisations[0].Id;
    }
}
