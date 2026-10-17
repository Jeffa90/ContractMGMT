using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Capabilities;

/// <summary>What would change if the open draft were published: which capabilities switch, what that does to the pages and lists, and whether the result fits the organisation's workflows and work in flight. Nothing here changes anything.</summary>
[Authorize(Policy = "perm:capabilities.read")]
public sealed class PreviewModel(ICapabilityAdministration capabilities, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation) : CapabilityPage(organisations, access, authorisation)
{
    public CapabilityPreviewResult? Result { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        Result = await capabilities.PreviewAsync(org, ct);
        return Page();
    });
}
