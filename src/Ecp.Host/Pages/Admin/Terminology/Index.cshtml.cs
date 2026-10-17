using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Terminology;

/// <summary>The wording in force for an organisation: every platform key with the label people read, where it comes from (the organisation, its solution pack or the platform), and what a draft would change.</summary>
[Authorize(Policy = "perm:terminology.read")]
public sealed class IndexModel(ITerminologyAdministration terminology, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation) : TerminologyPage(organisations, access, authorisation)
{
    public TerminologyOverview? Overview { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        Overview = await terminology.OverviewAsync(org, Locale, ct);
        Locale = Overview.Locale;
        return Page();
    });
}
