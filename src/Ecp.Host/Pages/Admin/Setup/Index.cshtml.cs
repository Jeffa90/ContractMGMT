using Ecp.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Setup;

/// <summary>Installation administrators review the setup report and reopen individual configuration steps. Reopening needs a recent authentication.</summary>
[Authorize(Policy = "perm:setup.run")]
public sealed class IndexModel(ISetupService setup) : EcpPageModel
{
    public SetupReport? Report { get; private set; }
    public SetupSnapshot? Snapshot { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? format, CancellationToken ct)
    {
        Report = await setup.BuildReportAsync(ct);
        Snapshot = await setup.SnapshotAsync(amending: true, ct);
        if (format == "json") return new JsonResult(Report);
        return Page();
    }
}
