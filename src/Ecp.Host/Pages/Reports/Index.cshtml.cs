using Ecp.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Reports;

/// <summary>Reports: a hub over what the platform already provides (dashboards, saved lists, exports, the audit export). It is not a reporting module and says so.</summary>
[Authorize]
public sealed class IndexModel(IAccessService access) : EcpPageModel
{
    public bool CanReadContracts { get; private set; }
    public bool CanBrowseCategories { get; private set; }
    public bool CanReadAudit { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        CanReadContracts = await access.HoldsAnywhereAsync("contracts.read", ct);
        CanBrowseCategories = CanReadContracts;
        CanReadAudit = await access.HoldsAnywhereAsync("audit.read", ct) || await access.HoldsAnywhereAsync("audit.read_admin", ct);
        if (!CanReadContracts && !CanReadAudit && !await access.HoldsAnywhereAsync("records.export", ct)) return Forbid();
        return Page();
    }
}
