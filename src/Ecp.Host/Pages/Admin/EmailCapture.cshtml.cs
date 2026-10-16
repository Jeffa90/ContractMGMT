using Ecp.Notifications;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

[Authorize(Policy = "perm:notifications.administer")]
public sealed class EmailCaptureModel(INotificationAdmin admin, EcpHostOptions host) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public PagedResult<CapturedEmailRow> Result { get; private set; } = new([], 0, 1, 25);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () =>
    {
        if (host.Mode == EcpMode.Production) return NotFound();   // there is no mailbox where mail is really delivered
        Result = await admin.CapturedEmailsAsync(Math.Max(1, PageNumber), 25, ct);
        return Page();
    });
}
