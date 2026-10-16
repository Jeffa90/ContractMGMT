using Ecp.Host.Setup;
using Ecp.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Setup;

[AllowAnonymous]
public sealed class ReportModel(ISetupService setup) : SetupPageModel(setup)
{
    public SetupReport? Report { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? format, CancellationToken ct)
    {
        if (!await HasSessionAsync(ct)) return RedirectToPage("Index");
        Report = await Setup.BuildReportAsync(ct);
        if (format == "json") return new JsonResult(Report) { ContentType = "application/json" };
        return Page();
    }
}
