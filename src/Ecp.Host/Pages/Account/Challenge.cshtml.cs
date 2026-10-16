using Ecp.Host.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Account;

/// <summary>Starts sign-in with a registered organisation identity provider. Only schemes registered from stored provider settings are accepted.</summary>
[AllowAnonymous]
public sealed class ChallengeModel(OidcSchemeManager oidc) : EcpPageModel
{
    public IActionResult OnGet(string? scheme, string? returnUrl, string? reauth, string? testProvider)
    {
        if (scheme is null || oidc.Get(scheme) is null) return NotFound();
        var props = new AuthenticationProperties { RedirectUri = IsLocal(returnUrl) ? returnUrl : "/" };
        if (reauth == "1") props.Items["ecp:reauth"] = "1";
        if (!string.IsNullOrEmpty(testProvider)) props.Items["ecp:test-provider"] = testProvider; // records a successful test sign-in for the provider (setup and administration)
        return Challenge(props, scheme);
    }
}
