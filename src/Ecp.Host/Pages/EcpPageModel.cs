using Ecp.SharedKernel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ecp.Host.Pages;

/// <summary>
/// Shared page behaviour: domain rule violations become field-level messages the user can act on; absent or out-of-scope records are 404 and
/// permission failures are 403, exactly as the services decide (the page never makes its own authorisation decision).
/// </summary>
public abstract class EcpPageModel : PageModel
{
    protected void Flash(string message) => TempData["Flash"] = message;

    /// <summary>Runs a handler body; maps service exceptions to HTTP results and domain errors to ModelState on <paramref name="page"/>.</summary>
    protected async Task<IActionResult> RunAsync(Func<Task<IActionResult>> body, Func<Task>? reload = null)
    {
        try { return await body(); }
        catch (ResourceNotFoundException) { return NotFound(); }
        catch (ForbiddenException) { return Forbid(); }
        catch (DomainException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            if (reload is not null) await reload(); // redisplay the page with fresh data and the message
            return Page();
        }
    }

    protected static bool IsLocal(string? url) => !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
