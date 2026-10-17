using Ecp.Lifecycle;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Approvals;

[Authorize(Policy = "perm:contracts.read|approvals.action")]
public sealed class DetailsModel(IRegisterAuthorisationService approvals) : EcpPageModel
{
    public ApprovalDetail Approval { get; private set; } = null!;
    [BindProperty] public string? Comment { get; set; }
    [BindProperty] public string? ClientRequestId { get; set; }

    public Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) => RunAsync(async () => { Approval = await approvals.GetAsync(id, ct); ClientRequestId = Guid.NewGuid().ToString("N"); return Page(); });

    public Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await approvals.ApproveAsync(id, Comment, ClientRequestId, ct);
        Flash("Approved. The [[agreement|lower]] is registered, Effective and locked.");
        return RedirectToPage(new { id });
    }, async () => { Approval = await approvals.GetAsync(id, ct); });

    public Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken ct) => RunAsync(async () =>
    {
        await approvals.RejectAsync(id, Comment ?? "", ClientRequestId, ct);
        Flash("Rejected. The [[agreement|lower]] returns to Initial Creation with your comment.");
        return RedirectToPage(new { id });
    }, async () => { Approval = await approvals.GetAsync(id, ct); });
}
