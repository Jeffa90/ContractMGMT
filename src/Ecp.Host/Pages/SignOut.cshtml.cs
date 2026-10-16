using Ecp.Host.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages;

[AllowAnonymous]
public sealed class SignOutModel : EcpPageModel
{
    public void OnGet() { }

    /// <summary>Sign-out is a POST with antiforgery protection so another site cannot sign a user out.</summary>
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(EcpSchemes.Session);
        return RedirectToPage("/SignIn");
    }
}
