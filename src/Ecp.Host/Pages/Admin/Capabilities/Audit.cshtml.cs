using Ecp.Configuration;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Capabilities;

/// <summary>The audit trail of one organisation's capability choices: drafts, validations, publications and rollbacks, under their stable action codes. Entries cannot be edited.</summary>
[Authorize(Policy = "perm:capabilities.audit.read")]
public sealed class AuditModel(ICapabilityAdministration capabilities, IOrganisationStructureService organisations, IAccessService access, IAuthorizationService authorisation) : CapabilityPage(organisations, access, authorisation)
{
    public PagedResult<CapabilityAuditItem> Items { get; private set; } = new([], 0, 1, 50);

    public Task<IActionResult> OnGetAsync(int? page, CancellationToken ct) => RunAsync(async () =>
    {
        var org = await ResolveOrganisationAsync(ct);
        Items = await capabilities.AuditAsync(org, page ?? 1, ct);
        return Page();
    });
}
