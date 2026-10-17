using Ecp.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Packs;

/// <summary>
/// Shared behaviour of the Pack Manager pages. Reading needs only the read permission. Anything that changes what the installation trusts or can use (installing, retiring, assigning, trust keys) also needs a
/// recent sign-in, the same step-up the installation setup uses. The services still check the permission on every call; the page makes no decision of its own.
/// </summary>
public abstract class PackPage(IAuthorizationService authorisation) : EcpPageModel
{
    /// <summary>The page-level policy check for a handler that needs more than the page itself (the service still checks the permission on every call).</summary>
    protected async Task<bool> HoldsAsync(string permission) => (await authorisation.AuthorizeAsync(User, Policies.PermissionPrefix + permission)).Succeeded;

    protected async Task<bool> RecentAsync() => (await authorisation.AuthorizeAsync(User, Policies.RecentAuthentication)).Succeeded;
    protected IActionResult ReauthRedirect() => Redirect("/Reauth?returnUrl=" + Uri.EscapeDataString(Request.Path.Value + Request.QueryString.Value));

    /// <summary>For a change: sends the person to sign in again first when their sign-in is not recent.</summary>
    protected async Task<IActionResult> StepUpAsync(Func<Task<IActionResult>> body, Func<Task>? reload = null)
        => await RecentAsync() ? await RunAsync(body, reload) : ReauthRedirect();
}
