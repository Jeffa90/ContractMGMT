using Ecp.Audit;
using Ecp.Identity;
using Ecp.Lifecycle;
using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Notifications;

internal sealed class NotificationAdmin(
    NotificationsDbContext db, IUnitOfWork uow, IAuditWriter audit, IClock clock, IAccessService access, IAccessDirectory directory, ILifecycleOutbox outbox, IOptions<NotificationsOptions> options,
    IEmailDeliveryProviderFactory providers) : INotificationAdmin
{
    /// <summary>The organisations the caller administers notifications for (null: all in the tenant). A caller with no such grant gets a 403.</summary>
    private async Task<IReadOnlyList<Guid>?> ScopeAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("notifications.administer", ct);
        if (grants.Count == 0) throw new ForbiddenException("Administering notifications needs the notifications.administer permission.");
        if (grants.Any(g => g.OrganisationIds is null)) return null;
        return grants.SelectMany(g => g.OrganisationIds!).Distinct().ToList();
    }

    private static IQueryable<Notification> InScope(IQueryable<Notification> q, IReadOnlyList<Guid>? orgs) => orgs is null ? q : q.Where(n => orgs.Contains(n.OrganisationId));

    public async Task<NotificationSummary> SummaryAsync(CancellationToken ct = default)
    {
        var orgs = await ScopeAsync(ct);
        var notifications = InScope(db.Notifications.AsNoTracking(), orgs);
        var deliveries = from d in db.Deliveries.AsNoTracking() join n in notifications on d.NotificationId equals n.Id select d;
        var since = clock.UtcNow.AddHours(-24);
        var tracking = db.Tracking.AsNoTracking().AsQueryable();
        if (orgs is not null) tracking = tracking.Where(t => orgs.Contains(t.OrganisationId));
        var waiting = (await outbox.PendingAsync(500, ct)).Count;
        return new NotificationSummary(
            await deliveries.CountAsync(d => d.Status == DeliveryStatus.Pending, ct), await deliveries.CountAsync(d => d.Status == DeliveryStatus.Failed, ct), await deliveries.CountAsync(d => d.Status == DeliveryStatus.DeadLetter, ct),
            await deliveries.CountAsync(d => d.Status == DeliveryStatus.Sent && d.SentUtc >= since, ct), await tracking.CountAsync(t => t.AcknowledgedUtc == null || t.AcknowledgedUtc < t.LastReminderSentUtc, ct), waiting,
            options.Value.EmailProvider, providers.IsSimulated(options.Value.EmailProvider));
    }

    public async Task<PagedResult<DeliveryRow>> DeliveriesAsync(string? status, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var orgs = await ScopeAsync(ct);
        var query = from d in db.Deliveries.AsNoTracking() join n in InScope(db.Notifications.AsNoTracking(), orgs) on d.NotificationId equals n.Id select new { d, n };
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<DeliveryStatus>(status, true, out var s)) query = query.Where(x => x.d.Status == s);
        else if (string.Equals(status, "problems", StringComparison.OrdinalIgnoreCase)) query = query.Where(x => x.d.Status == DeliveryStatus.Failed || x.d.Status == DeliveryStatus.DeadLetter);
        var total = await query.CountAsync(ct);
        var p = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 200);
        var rows = await query.OrderByDescending(x => x.d.CreatedUtc).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var items = new List<DeliveryRow>();
        foreach (var x in rows)
            items.Add(new DeliveryRow(x.d.Id, x.n.Id, await directory.DisplayNameAsync(x.d.RecipientUserId, ct) ?? "(unknown)", x.d.Channel.ToString(), x.d.Provider, x.d.Status.ToString(), x.d.Attempts, x.d.LastAttemptUtc,
                x.d.NextAttemptUtc, x.d.LastError, x.n.Title, x.n.EventType, x.d.CorrelationId, x.d.CreatedUtc));
        return new PagedResult<DeliveryRow>(items, total, p, size);
    }

    public async Task<IReadOnlyList<AttemptRow>> AttemptsAsync(Guid deliveryId, CancellationToken ct = default)
    {
        var orgs = await ScopeAsync(ct);
        var owner = await (from d in db.Deliveries.AsNoTracking() join n in InScope(db.Notifications.AsNoTracking(), orgs) on d.NotificationId equals n.Id where d.Id == deliveryId select d.Id).AnyAsync(ct);
        if (!owner) throw new ResourceNotFoundException();
        return (await db.Attempts.AsNoTracking().Where(a => a.DeliveryId == deliveryId).OrderBy(a => a.Number).ToListAsync(ct))
            .Select(a => new AttemptRow(a.Number, a.Provider, a.StartedUtc, a.DurationMs, a.Succeeded, a.Error, a.CorrelationId)).ToList();
    }

    public async Task RetryAsync(Guid deliveryId, CancellationToken ct = default)
    {
        var orgs = await ScopeAsync(ct);
        var delivery = await db.Deliveries.FirstOrDefaultAsync(d => d.Id == deliveryId, ct) ?? throw new ResourceNotFoundException();
        var notification = await InScope(db.Notifications.AsNoTracking(), orgs).FirstOrDefaultAsync(n => n.Id == delivery.NotificationId, ct) ?? throw new ResourceNotFoundException();
        var found = new { Delivery = delivery, notification.OrganisationId, notification.Classification };
        await uow.ExecuteAsync(async c =>
        {
            found.Delivery.Requeue(clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("notification.delivery_retried", nameof(NotificationDelivery), deliveryId.ToString(), OrganisationId: found.OrganisationId, Classification: found.Classification));
        }, ct);
    }

    public async Task<IReadOnlyList<JobRow>> JobsAsync(int take = 50, CancellationToken ct = default)
    {
        await ScopeAsync(ct);
        return (await db.JobRuns.AsNoTracking().OrderByDescending(j => j.StartedUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct))
            .Select(j => new JobRow(j.JobName, j.Status.ToString(), j.StartedUtc, j.FinishedUtc, j.Processed, j.Failures, j.Detail)).ToList();
    }

    public async Task<PagedResult<CapturedEmailRow>> CapturedEmailsAsync(int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        await ScopeAsync(ct);
        // The capture mailbox exists only where nothing is really delivered.
        if (options.Value.Mode == EcpMode.Production) throw new DomainException("not_available", "The development mailbox is not available in Production.");
        var total = await db.Captured.CountAsync(ct);
        var p = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 200);
        var rows = await db.Captured.AsNoTracking().OrderByDescending(x => x.CapturedUtc).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<CapturedEmailRow>(rows.Select(x => new CapturedEmailRow(x.Id, x.ToAddress, x.Subject, x.Body, x.CapturedUtc, x.CorrelationId)).ToList(), total, p, size);
    }
}

/// <summary>Runs the background work for the current tenant and keeps a record of each run.</summary>
internal sealed class NotificationJobs(
    NotificationsDbContext db, IUnitOfWork uow, ITenantContext tenant, IClock clock, INotificationDispatcher dispatcher, IDeliveryRunner delivery, IReminderScanner reminders, IRecordUnlockService unlocks) : INotificationJobs
{
    public async Task<IReadOnlyList<JobRow>> RunAllAsync(CancellationToken ct = default)
    {
        var rows = new List<JobRow>();
        rows.Add(await RunAsync("lifecycle-events", async () => (await dispatcher.DispatchAsync(ct), 0, "Notifications created from lifecycle events and ownership changes"), ct));
        rows.Add(await RunAsync("email-delivery", async () => (await delivery.DeliverDueAsync(ct), 0, "Deliveries attempted"), ct));
        rows.Add(await RunAsync("reminder-scan", async () =>
        {
            var r = await reminders.ScanAsync(ct);
            return (r.RemindersSent, r.SkippedNoRecipient, $"{r.ContractsScanned} contracts scanned; {r.NotificationsCreated} notifications; {r.WindowsSuppressed} windows covered; {r.SkippedNoRecipient} with nobody to tell");
        }, ct));
        rows.Add(await RunAsync("unlock-expiry", async () => (await unlocks.CloseExpiredAsync(ct), 0, "Emergency unlocks closed at their time limit"), ct));
        return rows;
    }

    private async Task<JobRow> RunAsync(string name, Func<Task<(int Processed, int Failures, string Detail)>> work, CancellationToken ct)
    {
        var run = JobRun.Start(tenant.RequireTenantId(), name, clock.UtcNow);
        await uow.ExecuteAsync(async c => { db.JobRuns.Add(run); await db.SaveChangesAsync(c); }, ct);
        try
        {
            var (processed, failures, detail) = await work();
            await uow.ExecuteAsync(async c => { run.Finish(processed, failures, detail, clock.UtcNow); await db.SaveChangesAsync(c); }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var tracked = await db.JobRuns.FirstAsync(j => j.Id == run.Id, ct);
            await uow.ExecuteAsync(async c => { tracked.Fail(ex is DomainException de ? $"{de.Code}: {de.Message}" : ex.GetType().Name + ": " + ex.Message, clock.UtcNow); await db.SaveChangesAsync(c); }, ct);
            run = tracked;
        }
        return new JobRow(run.JobName, run.Status.ToString(), run.StartedUtc, run.FinishedUtc, run.Processed, run.Failures, run.Detail);
    }
}
