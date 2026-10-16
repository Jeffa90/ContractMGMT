using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ecp.Host.Pages;

[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string Title { get; private set; } = "Something went wrong";
    public string Detail { get; private set; } = "An unexpected error occurred. Nothing was changed by this request.";
    public string? CorrelationId { get; private set; }

    // The status code comes from the query string the re-execution sets, never from a form field of the original request that happens to be called "code".
    public void OnGet([FromQuery] int? code) => Fill(code);
    public void OnPost([FromQuery] int? code) => Fill(code);

    private void Fill(int? code)
    {
        CorrelationId = HttpContext.TraceIdentifier;
        (Title, Detail) = code switch
        {
            403 => ("Access denied", "You do not have permission to do that. If you think you should, ask an administrator."),
            404 => ("Not found", "That page or record does not exist, or it is not visible to you."),
            401 => ("Sign in required", "Sign in to continue."),
            429 => ("Too many requests", "Please wait a moment and try again."),
            _ => ("Something went wrong", "An unexpected error occurred. Nothing was changed by this request.")
        };
        Response.StatusCode = code is >= 400 and < 600 ? code.Value : 500;
    }
}
