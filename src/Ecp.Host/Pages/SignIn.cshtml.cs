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
public sealed class SignInModel(DemoIdentityService demo, OidcSchemeManager oidc, ITenantDirectory tenants, EcpHostOptions host) : EcpPageModel
{
    public string ReturnUrl { get; private set; } = "/";
    public string? Message { get; private set; }
    public bool DemoEnabled => demo.IsEnabled;
    public bool DemoAllowedHere { get; private set; }
    public IReadOnlyList<DemoUser> DemoUsers { get; private set; } = [];
    public IReadOnlyList<RegisteredOidcProvider> Providers { get; private set; } = [];

    public async Task OnGetAsync(string? returnUrl, string? error, string? reason, CancellationToken ct)
    {
        await LoadAsync(returnUrl, ct);
        Message = error switch
        {
            null => reason == "inactive" ? "Your session ended because the account or tenant is no longer active." : null,
            "not_invited" => "No account has been invited for this identity. Ask an administrator to invite you.",
            "user_disabled" => "This account is disabled.",
            _ => "The identity provider could not complete the sign-in."
        };
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
    }
}
