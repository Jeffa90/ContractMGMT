using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Host.Pages;

/// <summary>
/// Shared page behaviour: domain rule violations become field-level messages the user can act on; absent or out-of-scope records are 404 and
/// permission failures are 403, exactly as the services decide (the page never makes its own authorisation decision).
/// </summary>
public abstract class EcpPageModel : PageModel
{
    /// <summary>The terminology in force for this request (organisation, then solution pack, then neutral wording). Presentation only.</summary>
    protected Ecp.Host.Terminology.TermContext Terms => HttpContext.RequestServices.GetRequiredService<Ecp.Host.Terminology.TermContext>();

    /// <summary>How this request shows dates, times, numbers and amounts: the formats of the organisation the page is about (see <see cref="PageFormat"/>).</summary>
    protected PageFormat Format => HttpContext.RequestServices.GetRequiredService<PageFormat>();

    /// <summary>Replaces <c>[[term]]</c> tokens in a message with the organisation's own words.</summary>
    protected string T(string message) => Terms.Expand(message);

    protected void Flash(string message) => TempData["Flash"] = T(message);

    /// <summary>Runs a handler body; maps service exceptions to HTTP results and domain errors to ModelState on <paramref name="page"/>.</summary>
    protected async Task<IActionResult> RunAsync(Func<Task<IActionResult>> body, Func<Task>? reload = null)
    {
        try { return await body(); }
        catch (ResourceNotFoundException) { return NotFound(); }
        catch (ForbiddenException) { return Forbid(); }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.RawMessage);   // tokens are expanded when the message is shown
            if (reload is not null) await reload(); // redisplay the page with fresh data and the message
            return Page();
        }
    }

    /// <summary>
    /// Runs a handler that needs the person to have signed in recently (publishing and rolling back configuration). Without a recent sign-in the person is taken to confirm their identity and returned here.
    /// The services check again; this only makes the redirect friendly.
    /// </summary>
    protected async Task<IActionResult> StepUpRunAsync(Func<Task<IActionResult>> body, Func<Task>? reload = null)
    {
        var authorisation = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
        if (!(await authorisation.AuthorizeAsync(User, Ecp.Host.Security.Policies.RecentAuthentication)).Succeeded)
            return Redirect("/Reauth?returnUrl=" + Uri.EscapeDataString(Request.Path.Value + Request.QueryString.Value));
        return await RunAsync(body, reload);
    }

    protected static bool IsLocal(string? url) => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
