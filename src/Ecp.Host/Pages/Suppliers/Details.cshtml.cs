using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.SharedKernel.Localisation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Suppliers;

[Authorize(Policy = "perm:suppliers.read")]
public sealed class DetailsModel(ISecuredSupplierService suppliers, ISupplierIdentifiers identifiers, IRegionalSettingsResolver regional, IAccessService access) : EcpPageModel
{
    public SupplierListItem Supplier { get; private set; } = null!;
    public IReadOnlyList<ContractListItem> Contracts { get; private set; } = [];
    public IReadOnlyList<SupplierIdentifierItem> Identifiers { get; private set; } = [];
    public IReadOnlyList<IdentifierTypeInfo> Types { get; private set; } = [];
    public SupplierAddress Address { get; private set; } = new(null, null, null, null, null, null);
    public AddressProfile AddressProfile { get; private set; } = AddressProfile.Neutral;
    public bool CanManage { get; private set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await LoadAsync(id, ct); return Page(); });

    public Task<IActionResult> OnPostAddAsync(Guid id, string typeKey, string value, CancellationToken ct) => RunAsync(async () =>
    {
        await identifiers.AddAsync(id, typeKey, value, ct);
        Flash("The identifier was added. Its format was checked; it was not verified against any registry.");
        return RedirectToPage(new { id });
    }, () => LoadAsync(id, ct));

    public Task<IActionResult> OnPostEndAsync(Guid id, Guid identifierId, CancellationToken ct) => RunAsync(async () =>
    {
        await identifiers.EndAsync(identifierId, ct);
        Flash("The identifier was ended. It stays in the history.");
        return RedirectToPage(new { id });
    }, () => LoadAsync(id, ct));

    private async Task LoadAsync(Guid id, CancellationToken ct)
    {
        Supplier = await suppliers.GetAsync(id, ct);
        await Terms.UseOrganisationAsync(Supplier.OrganisationId, ct);
        Contracts = await suppliers.ContractsOfAsync(id, ct);
        Identifiers = await identifiers.ListAsync(id, ct);
        Types = await identifiers.TypesAsync(Supplier.OrganisationId, ct);
        Address = await suppliers.GetAddressAsync(id, ct);
        AddressProfile = (await regional.GetAsync(Supplier.OrganisationId, ct)).Address;
        CanManage = (await access.GrantsAsync("suppliers.manage", ct)).Any(g => g.OrganisationIds is null || g.OrganisationIds.Contains(Supplier.OrganisationId));
    }

    public string VerificationText(string status) => status switch
    {
        IdentifierVerification.FormatValid => T("Format checked"),
        IdentifierVerification.FormatInvalid => T("Format not valid"),
        IdentifierVerification.ExternallyVerified => T("Verified externally"),
        IdentifierVerification.VerificationFailed => T("Verification failed"),
        _ => T("Not checked")
    };
}
