using Ecp.Lifecycle;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Variations;

[Authorize(Policy = "perm:contracts.read")]
public sealed class IndexModel(IVariationService variations) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public PagedResult<VariationListItem> Result { get; private set; } = new([], 0, 1, 25);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { Result = await variations.InProgressAsync(Math.Max(1, PageNumber), 25, ct); return Page(); });
}
