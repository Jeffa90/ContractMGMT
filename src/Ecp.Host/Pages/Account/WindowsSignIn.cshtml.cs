using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.Identity.Windows;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecp.Host.Pages.Account;

/// <summary>
/// Turns the Windows identity the web server authenticated into an application session, if and only if the person was invited, is active and has access.
/// Nothing is created here and no role is read from Windows. A refusal is audited and shown on a plain page that names the Windows account, because that
/// is what an administrator needs in order to invite the person.
/// </summary>
[AllowAnonymous]
[EnableRateLimiting("signin")]
public sealed class WindowsSignInModel(ITenantDirectory tenants, TenantContext tenant, IWindowsSignIn windows, IUserService users, IProviderRegistry providers, SessionIssuer sessions, EcpHostOptions host,
    ILogger<WindowsSignInModel> log) : EcpPageModel
{
    public async Task<IActionResult> OnGetAsync(string? returnUrl, string? scope, string? testProvider, CancellationToken ct)
    {
        var target = IsLocal(returnUrl) ? returnUrl! : "/";
        var login = WindowsIdentityCaptureMiddleware.From(HttpContext);
        if (login is null) return RedirectToPage("/Account/AccessRefused", new { code = "windows_unavailable" });

        var tenantId = scope == "installation" ? SystemTenant.Id : await CustomerTenantAsync(ct);
        if (tenantId is null) return RedirectToPage("/Account/AccessRefused", new { code = "not_set_up" });

        using var _ = tenant.Use(tenantId.Value);
        var resolution = await windows.ResolveAsync(login, auditRefusal: true, ct);
        if (!resolution.Succeeded)
        {
            log.LogWarning("Windows sign-in refused ({Code}) for {Account}", resolution.DenialCode, login.Account);
            return RedirectToPage("/Account/AccessRefused", new { code = resolution.DenialCode ?? "denied" });
        }
        var user = (await users.GetAsync(resolution.UserId!.Value, ct))!;
        var provider = await windows.ProviderKeyAsync(ct) ?? "windows";
        await sessions.SignInAsync(HttpContext, tenantId.Value, user.Id, user.DisplayName, provider);
        // Setup and administration "Test sign-in": a real, successful sign-in (invited, active, with access) is what proves the administrator can get in.
        if (!string.IsNullOrEmpty(testProvider) && testProvider == provider) await providers.RecordTestSignInAsync(testProvider, ct);
        return LocalRedirect(target);
    }

    /// <summary>With one customer tenant the choice is unambiguous; with several the configured tenant name decides (as for every other sign-in method).</summary>
    private async Task<Guid?> CustomerTenantAsync(CancellationToken ct)
    {
        var all = await tenants.ListAsync(ct);
        var chosen = all.Count == 1 ? all[0] : all.FirstOrDefault(t => string.Equals(t.Name, host.DemoTenantName, StringComparison.Ordinal));
        return chosen?.Id;
    }
}
