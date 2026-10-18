using Ecp.Recovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin.Operations;

[Authorize(Policy = "perm:operations.read")]
public sealed class IndexModel(IOperationsAdministration operations) : EcpPageModel
{
    public OperationsOverview Overview { get; private set; } = null!;
    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Overview = await operations.OverviewAsync(ct); return Page(); });
}
