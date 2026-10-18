using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

/// <summary>The operations dashboard. It only displays what <see cref="IOperationsDashboard"/> builds: no operational logic lives here.</summary>
[Authorize(Policy = "perm:operations.read")]
public sealed class DashboardModel(IOperationsDashboard dashboard) : EcpPageModel
{
    public OperationsDashboard View { get; private set; } = null!;
    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { View = await dashboard.BuildAsync(ct); return Page(); });
    public Task<IActionResult> OnGetJsonAsync(CancellationToken ct) => RunAsync(async () => new JsonResult(DashboardExport.Of(await dashboard.BuildAsync(ct))));
}
