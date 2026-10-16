using Ecp.Notifications;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Notifications;

[Authorize]
public sealed class IndexModel(INotificationInbox inbox) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public bool UnreadOnly { get; set; }
    [BindProperty(SupportsGet = true)] public bool NeedsAcknowledgement { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    public PagedResult<InboxItem> Result { get; private set; } = new([], 0, 1, 25);

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct) => Result = await inbox.ListAsync(new InboxFilter(UnreadOnly, NeedsAcknowledgement, null, Math.Max(1, PageNumber)), ct);

    public Task<IActionResult> OnPostReadAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await inbox.MarkReadAsync(id, ct); return RedirectToPage(); }, () => LoadAsync(ct));
    public Task<IActionResult> OnPostReadAllAsync(CancellationToken ct) => RunAsync(async () => { await inbox.MarkAllReadAsync(ct); Flash("Everything is marked as read."); return RedirectToPage(); }, () => LoadAsync(ct));
    public Task<IActionResult> OnPostAcknowledgeAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await inbox.AcknowledgeAsync(id, ct); Flash("Thank you. The reminder is acknowledged and the other owners can see that."); return RedirectToPage(); }, () => LoadAsync(ct));
}
