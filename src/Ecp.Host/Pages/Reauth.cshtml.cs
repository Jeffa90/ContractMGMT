using System.Security.Claims;
using Ecp.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages;

/// <summary>
/// Step-up authentication before a sensitive action (for example reopening setup). A real identity provider is asked to authenticate again
/// (prompt=login, max_age=0); the demonstration provider offers an explicit confirmation and exists only in Demo and Test modes.
/// </summary>
[Authorize]
public sealed class ReauthModel(OidcSchemeManager oidc, DemoIdentityService demo, SessionIssuer sessions, Ecp.Identity.Windows.IWindowsSignIn windows, Ecp.SharedKernel.TenantContext tenantContext) : EcpPageModel
{
    public string ReturnUrl { get; private set; } = "/";
    public bool IsDemo { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = IsLocal(returnUrl) ? returnUrl! : "/";
        var provider = User.FindFirstValue(EcpClaims.Provider);
        if (provider == DemoIdentityService.ProviderKey)
        {
            IsDemo = demo.IsEnabled;
            return IsDemo ? Page() : Forbid();
        }
        if (Guid.TryParse(User.FindFirstValue(EcpClaims.TenantId), out var windowsTenant) && provider is not null && Guid.TryParse(User.FindFirstValue(EcpClaims.UserId), out var windowsUser))
        {
            using var _ = tenantContext.Use(windowsTenant);
            if (await windows.ProviderKeyAsync(ct) == provider)
            {
                // Windows cannot ask for a password again: the web server proves the same Windows identity is still behind this browser. That is the strongest step-up this method offers.
                var login = WindowsIdentityCaptureMiddleware.From(HttpContext);
                if (login is null || !await windows.IsLinkedAsync(windowsUser, login, ct)) return Forbid();
                await sessions.RefreshReauthenticationAsync(HttpContext);
                return LocalRedirect(ReturnUrl);
            }
        }
        if (Guid.TryParse(User.FindFirstValue(EcpClaims.TenantId), out var tenantId) && provider is not null)
        {
            var scheme = OidcSchemeManager.SchemeName(tenantId, provider);
            if (oidc.Get(scheme) is not null)
                return Redirect($"/Account/Challenge?scheme={Uri.EscapeDataString(scheme)}&reauth=1&returnUrl={Uri.EscapeDataString(ReturnUrl)}");
        }
        return Forbid(); // no way to re-authenticate this session: the action stays closed
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (User.FindFirstValue(EcpClaims.Provider) != DemoIdentityService.ProviderKey || !demo.IsRequestAllowed(HttpContext)) return Forbid();
        await sessions.RefreshReauthenticationAsync(HttpContext);
        return LocalRedirect(IsLocal(returnUrl) ? returnUrl! : "/");
    }
}
