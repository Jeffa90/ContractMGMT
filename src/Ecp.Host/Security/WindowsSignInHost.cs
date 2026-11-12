using System.Security.Claims;
using System.Security.Principal;
using Ecp.Identity.Windows;

namespace Ecp.Host.Security;

/// <summary>
/// Under IIS the web server authenticates the person (Windows Authentication) and, in-process, puts a Windows principal on the request before the application
/// runs. That principal proves who the person is; it is NOT an application session and grants nothing. This middleware lifts it out of <c>HttpContext.User</c>
/// (so the authorisation fallback policy cannot mistake it for a signed-in user) and keeps only the account name and SID for the sign-in page to decide on.
/// It runs before <c>UseAuthentication</c>; the application's own session cookie then sets <c>HttpContext.User</c> as it always did.
/// </summary>
public sealed class WindowsIdentityCaptureMiddleware(RequestDelegate next)
{
    public const string ItemKey = "ecp:windows-login";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity is { IsAuthenticated: true } identity)
        {
            if (Read(context.User, identity) is { } login) context.Items[ItemKey] = login;
            context.User = new ClaimsPrincipal(new ClaimsIdentity());   // anonymous until the application's own session says otherwise
        }
        await next(context);
    }

    /// <summary>Account name and SID as supplied by the server. Nothing else (no groups, no email, no claims) is carried.</summary>
    internal static WindowsLogin? Read(ClaimsPrincipal user, IIdentity identity)
    {
        string? sid = null, name = identity.Name;
        if (OperatingSystem.IsWindows() && identity is WindowsIdentity windows) sid = windows.User?.Value;
        sid ??= user.FindFirst(ClaimTypes.PrimarySid)?.Value ?? user.FindFirst(ClaimTypes.Sid)?.Value;
        return string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(name) ? null : new WindowsLogin(name, sid);
    }

    public static WindowsLogin? From(HttpContext context) => context.Items.TryGetValue(ItemKey, out var v) ? v as WindowsLogin : null;
}
