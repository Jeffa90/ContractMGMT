using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Terminology;

/// <summary>What people would read before and after the open draft is published, on representative headings, messages, notifications, exports and role names. Nothing here changes anything.</summary>
[Authorize(Policy = "perm:terminology.read")]
public sealed class PreviewModel(ITerminologyAdministration terminology, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation) : TerminologyPage(organisations, access, authorisation)
{
    public TerminologyPreviewResult? Result { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        Result = await terminology.PreviewAsync(org, Locale, ct);
        Locale = Result.Locale;
        return Page();
    });
}
