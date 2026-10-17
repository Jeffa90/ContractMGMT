using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Suppliers;

[Authorize(Policy = "perm:suppliers.manage")]
public sealed class CreateModel(ISecuredSupplierService suppliers, ISupplierIdentifiers identifiers, IOrganisationStructureService organisations, IAccessService access, IRegionalSettingsResolver regional) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OrganisationId { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? VendorNumber { get; set; }
    /// <summary>Identifier values by type key (the organisation's identifier profile decides which types are offered).</summary>
    [BindProperty] public Dictionary<string, string> Identifier { get; set; } = [];
    /// <summary>Address values by field key (the organisation's address profile decides which fields are offered).</summary>
    [BindProperty] public Dictionary<string, string> Address { get; set; } = [];
    public IReadOnlyList<OrganisationSummary> Organisations { get; private set; } = [];
    public IReadOnlyList<IdentifierTypeInfo> Types { get; private set; } = [];
    public AddressProfile AddressProfile { get; private set; } = AddressProfile.Neutral;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) { await LoadAsync(ct); return Page(); }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return await RunAsync(async () =>
        {
            if (OrganisationId is null || string.IsNullOrWhiteSpace(Name)) { ModelState.AddModelError(nameof(Name), "Enter the [[supplier|lower]] name."); return Page(); }
            var values = Identifier.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var address = new SupplierAddress(Address.GetValueOrDefault("line1"), Address.GetValueOrDefault("line2"), Address.GetValueOrDefault("locality"), Address.GetValueOrDefault("region"),
                Address.GetValueOrDefault("postal"), Address.GetValueOrDefault("country"));
            var id = await suppliers.CreateAsync(new CreateSupplierRequest(OrganisationId.Value, Name, null, string.IsNullOrWhiteSpace(VendorNumber) ? null : VendorNumber, null, values, Address: address), ct);
            Flash("[[supplier]] added.");
            return RedirectToPage("Details", new { id });
        });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("suppliers.manage", ct);
        Organisations = (await organisations.ListOrganisationsAsync(ct)).Where(o => grants.Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(o.Id))).ToList();
        if (OrganisationId is null && Organisations.Count > 0) OrganisationId = Organisations[0].Id;
        if (OrganisationId is not { } org) return;
        await Terms.UseOrganisationAsync(org, ct);
        AddressProfile = (await regional.GetAsync(org, ct)).Address;
        try { Types = await identifiers.TypesAsync(org, ct); }
        catch (ForbiddenException) { Types = []; }
    }
}
