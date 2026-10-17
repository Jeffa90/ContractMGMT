using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Capabilities;

/// <summary>Which optional capabilities an organisation uses: what is in force, where that comes from (the organisation, its solution pack or the platform default), what a draft would change, and the compatibility checks.</summary>
[Authorize(Policy = "perm:capabilities.read")]
public sealed class IndexModel(ICapabilityAdministration capabilities, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation) : CapabilityPage(organisations, access, authorisation)
{
    public CapabilityOverview? Overview { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        Overview = await capabilities.OverviewAsync(org, ct);
        return Page();
    });
}
