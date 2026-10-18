using Ecp.Setup.Organisations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Organisations;

/// <summary>The organisations in this tenant and how far each has got through setup. A Tenant Administrator creates organisations here; the decisions are made by the service.</summary>
[Authorize(Policy = "perm:organisations.manage")]
public sealed class IndexModel(IOrganisationCreation creation) : EcpPageModel
{
    [BindProperty] public string? Reason { get; set; }
    public IReadOnlyList<OrganisationOverview> Organisations { get; private set; } = [];
    public TenancyView Tenancy { get; private set; } = default!;

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Tenancy = await creation.TenancyAsync(ct);
        Organisations = await creation.ListAsync(ct);
    }

    public Task<IActionResult> OnPostWidenAsync(CancellationToken ct) => StepUpRunAsync(async () =>
    {
        await creation.WidenTenancyAsync(Reason ?? "", ct);
        Flash("This installation now supports several organisations. It cannot be narrowed again.");
        return RedirectToPage();
    }, () => LoadAsync(ct));
}
