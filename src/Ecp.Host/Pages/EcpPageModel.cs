using Ecp.SharedKernel;
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

    protected static bool IsLocal(string? url) => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
