using Ecp.Access;
using Ecp.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Access;

/// <summary>My access: what the signed-in person can do, in plain English, with where and why. The detail underneath (roles, scope, ceiling) is kept for those who want it.</summary>
[Authorize]
public sealed class IndexModel(IAccessService access, IEffectiveAccess effective) : EcpPageModel
{
    public EffectiveAccessReport Report { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (access.CurrentUserId is null) return Challenge();
        Report = await effective.ForMeAsync(ct);
        return Page();
    }
}
