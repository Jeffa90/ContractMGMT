using Ecp.Host.Setup;
using Ecp.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;

namespace Ecp.Host.Pages.Setup;

[AllowAnonymous]
[EnableRateLimiting("signin")]
public sealed class IndexModel(ISetupService setup, SetupEnvironment env) : SetupPageModel(setup)
{
    public bool HasSession { get; private set; }
    public SetupSnapshot? Snapshot { get; private set; }
    public string? Message { get; private set; }
    public string Mode => env.HostMode.ToString();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        HasSession = await HasSessionAsync(ct);
        if (HasSession) Snapshot = await Setup.SnapshotAsync(false, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? token, CancellationToken ct)
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        var loopback = remote is null || IPAddress.IsLoopback(remote);
        var result = await Setup.BeginSessionAsync(token, loopback, ct);
        if (!result.Granted)
        {
            Message = result.Refusal;
            return Page();
        }
        Response.Cookies.Append(SetupGateMiddleware.SessionCookie, result.SessionId!, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = env.HostMode == Ecp.SharedKernel.EcpMode.Production || Request.IsHttps, Path = "/Setup", IsEssential = true,
            MaxAge = TimeSpan.FromMinutes(60)
        });
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEndAsync(CancellationToken ct)
    {
        if (await HasSessionAsync(ct)) await Setup.EndSessionAsync(ct);
        Response.Cookies.Delete(SetupGateMiddleware.SessionCookie, new CookieOptions { Path = "/Setup" });
        return RedirectToPage();
    }
}
