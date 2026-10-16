using Ecp.Lifecycle;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Renewals;

[Authorize(Policy = "perm:contracts.read")]
public sealed class IndexModel(IRenewalService renewals) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public PagedResult<RenewalInfo> Result { get; private set; } = new([], 0, 1, 25);
    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Result = await renewals.InProgressAsync(Math.Max(1, PageNumber), 25, ct); return Page(); });
}
