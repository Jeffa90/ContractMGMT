using Ecp.Host.Security;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ecp.Host.Pages;

[AllowAnonymous]
[EnableRateLimiting("signin")]
public sealed class SignInModel(DemoIdentityService demo, OidcSchemeManager oidc, ITenantDirectory tenants, EcpHostOptions host, Ecp.Identity.Windows.IWindowsSignIn windows, TenantContext tenantContext) : EcpPageModel
{
    public string ReturnUrl { get; private set; } = "/";
    public string? Message { get; private set; }
    public bool DemoEnabled => demo.IsEnabled;
    public bool DemoAllowedHere { get; private set; }
    public IReadOnlyList<DemoUser> DemoUsers { get; private set; } = [];
    public IReadOnlyList<RegisteredOidcProvider> Providers { get; private set; } = [];
    /// <summary>Windows sign-in is switched on for the tenant, and whether the web server has actually told us who the person is on this request.</summary>
    public bool WindowsAvailable { get; private set; }
    public bool WindowsInstallationAvailable { get; private set; }
    public bool WindowsIdentitySupplied { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? returnUrl, string? error, string? reason, CancellationToken ct)
    {
        await LoadAsync(returnUrl, ct);
        // With Windows sign-in the web server has already identified the person, so go straight on, unless they have just signed out or been refused (that would loop).
        if (WindowsAvailable && WindowsIdentitySupplied && error is null && reason is null)
            return RedirectToPage("/Account/WindowsSignIn", new { returnUrl = ReturnUrl });
        Message = error switch
        {
            null => reason switch
            {
                "inactive" => "Your session ended because the account or tenant is no longer active.",
                "signed_out" => "You have signed out.",
                _ => (WindowsAvailable && !WindowsIdentitySupplied) ? "The web server did not tell the application who you are, so Windows sign-in cannot continue. Ask your administrator to check that Windows Authentication is switched on for this site." : null
            },
            "not_invited" => "No account has been invited for this identity. Ask an administrator to invite you.",
            "user_disabled" => "This account is disabled.",
            _ => "The identity provider could not complete the sign-in."
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? pick, string? returnUrl, CancellationToken ct)
    {
        if (!demo.IsRequestAllowed(HttpContext)) return NotFound();
        var parts = (pick ?? "").Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var tenantId) || !Guid.TryParse(parts[1], out var userId)) return BadRequest();
        if (!await demo.SignInAsync(HttpContext, tenantId, userId))
        {
            await LoadAsync(returnUrl, ct);
            Message = "That user cannot sign in.";
            return Page();
        }
        return LocalRedirect(IsLocal(returnUrl) ? returnUrl! : "/");
    }

    private async Task LoadAsync(string? returnUrl, CancellationToken ct)
    {
        ReturnUrl = IsLocal(returnUrl) ? returnUrl! : "/";
        DemoAllowedHere = demo.IsRequestAllowed(HttpContext);
        if (DemoAllowedHere) DemoUsers = await demo.ListAsync(ct);
        // Organisation sign-in providers belong to a tenant. With one customer tenant the choice is unambiguous; with several, the host mapping decides.
        var all = (await tenants.ListAsync(ct));
        var chosen = all.Count == 1 ? all[0] : all.FirstOrDefault(t => string.Equals(t.Name, host.DemoTenantName, StringComparison.Ordinal));
        Providers = chosen is null ? [] : oidc.ForTenant(chosen.Id);
        WindowsIdentitySupplied = WindowsIdentityCaptureMiddleware.From(HttpContext) is not null;
        if (chosen is not null) using (tenantContext.Use(chosen.Id)) WindowsAvailable = await windows.ProviderKeyAsync(ct) is not null;
        using (tenantContext.Use(SystemTenant.Id)) WindowsInstallationAvailable = WindowsAvailable && await windows.ProviderKeyAsync(ct) is not null;
    }
}
