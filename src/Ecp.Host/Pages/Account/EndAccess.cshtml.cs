using Ecp.Host.Security;
using Ecp.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Account;

/// <summary>Ends the support or emergency access this session is working under and returns to the person's own tenant. Reachable from the banner on every page.</summary>
public sealed class EndAccessModel(IElevationService elevations, SessionIssuer sessions) : EcpPageModel
{
    public IActionResult OnGet() => NotFound(); // changes state: POST only

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try { await elevations.EndCurrentAsync("Ended from the banner", ct); } catch (Ecp.SharedKernel.DomainException) { /* nothing active: still tidy the session */ }
        await sessions.EndElevationAsync(HttpContext);
        return LocalRedirect("/Admin/Support");
    }
}
