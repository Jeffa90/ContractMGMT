using Ecp.Notifications;
using Ecp.SharedKernel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecp.Host.Pages.Admin;

[Authorize(Policy = "perm:notifications.administer")]
public sealed class NotificationsModel(INotificationAdmin admin, INotificationJobs jobs) : EcpPageModel
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; } = "problems";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public Guid? Delivery { get; set; }
    public NotificationSummary Summary { get; private set; } = null!;
    public PagedResult<DeliveryRow> Deliveries { get; private set; } = new([], 0, 1, 25);
    public IReadOnlyList<JobRow> Jobs { get; private set; } = [];
    public IReadOnlyList<AttemptRow> Attempts { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken ct) => RunAsync(async () => { await LoadAsync(ct); return Page(); });

    private async Task LoadAsync(CancellationToken ct)
    {
        Summary = await admin.SummaryAsync(ct);
        Deliveries = await admin.DeliveriesAsync(string.IsNullOrWhiteSpace(Status) || Status == "all" ? null : Status, Math.Max(1, PageNumber), 25, ct);
        Jobs = await admin.JobsAsync(20, ct);
        if (Delivery is { } d) Attempts = await admin.AttemptsAsync(d, ct);
    }

    public Task<IActionResult> OnPostRetryAsync(Guid id, CancellationToken ct) => RunAsync(async () => { await admin.RetryAsync(id, ct); Flash("The delivery is queued to be tried again."); return RedirectToPage(); }, () => LoadAsync(ct));

    public Task<IActionResult> OnPostRunNowAsync(CancellationToken ct) => RunAsync(async () =>
    {
        // Administrators can run the background work for their own tenant on demand (it is the same work the timer runs).
        await admin.SummaryAsync(ct);
        var rows = await jobs.RunAllAsync(ct);
        Flash("Ran " + string.Join(", ", rows.Select(r => $"{r.JobName} ({r.Status})")) + ".");
        return RedirectToPage();
    }, () => LoadAsync(ct));
}
