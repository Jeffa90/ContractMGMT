using Ecp.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Account;

/// <summary>The plain explanation shown when Windows sign-in was refused. It says why, in words, and names the Windows account so an administrator can be asked to invite it.</summary>
[AllowAnonymous]
public sealed class AccessRefusedModel : EcpPageModel
{
    public string Heading { get; private set; } = "";
    public string Explanation { get; private set; } = "";
    public string? Account { get; private set; }

    public void OnGet(string? code)
    {
        (Heading, Explanation) = code switch
        {
            "not_invited" => ("You have not been invited", "No account has been invited for your Windows account. Ask an administrator to invite you."),
            "no_access" => ("You have no access yet", "Your account exists, but nobody has given you access to anything yet. Ask an administrator to give you access."),
            "user_disabled" => ("This account is disabled", "Your account has been disabled. Ask an administrator if you think this is a mistake."),
            "identity_mismatch" => ("This account needs checking", "The name of your Windows account is already linked to a different Windows identity. Ask an administrator to check who you are."),
            "domain_not_allowed" => ("This Windows domain is not accepted", "Accounts from your Windows domain cannot sign in here. Ask an administrator."),
            "windows_unavailable" => ("Windows sign-in did not supply your account", "The web server did not tell the application who you are. Windows sign-in may not be switched on for this site, or your browser may not be sending your Windows credentials. Ask your administrator."),
            "windows_not_enabled" => ("Windows sign-in is not switched on", "This installation does not use Windows sign-in. Use the sign-in page."),
            "not_set_up" => ("Setup is not finished", "This installation has not been set up yet."),
            _ => ("Sign-in was refused", "You could not be signed in. Ask an administrator.")
        };
        // Only the name the web server supplied for this request is shown; it is not stored by this page.
        Account = WindowsIdentityCaptureMiddleware.From(HttpContext)?.Account;
    }
}
