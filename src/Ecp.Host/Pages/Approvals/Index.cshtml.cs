using Ecp.Lifecycle;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Approvals;

[Authorize(Policy = "perm:contracts.read|approvals.action")]
public sealed class IndexModel(IRegisterAuthorisationService approvals) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public bool Everything { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public PagedResult<ApprovalQueueItem> Result { get; private set; } = new([], 0, 1, 25);
    public IReadOnlyList<EligibleApprover> Eligible { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        Result = await approvals.PendingAsync(!Everything, Math.Max(1, PageNumber), 25, ct);
        // Who may act is visible to everyone who can see the queue, backups included.
        var group = Result.Items.Select(i => i.ApproverGroupKey).FirstOrDefault();
        Eligible = group is null ? [] : await approvals.EligibleApproversAsync(group, ct);
        return Page();
    });
}
