using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Notifications;

/// <summary>
/// Operations dashboard facts for the outbox and the background jobs (docs/OPERATIONS_DASHBOARD.md): counts and times only. No recipient, message, error text or job detail is read into a fact.
/// </summary>
internal sealed class NotificationsOperationalProbe(NotificationsDbContext db) : IOperationalProbe
{
    public string Area => "Notifications";

    /// <summary>A job run still recorded as running after this long is treated as stale: the process that ran it has probably stopped.</summary>
    public static readonly TimeSpan StaleRunning = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<OperationalFact>> ProbeAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var facts = new List<OperationalFact>();
        var emails = db.Deliveries.AsNoTracking().Where(d => d.Channel != DeliveryChannel.InApp);
        var pending = await emails.Where(d => d.Status == DeliveryStatus.Pending).GroupBy(_ => 1).Select(g => new { Count = g.Count(), Oldest = g.Min(d => d.CreatedUtc) }).FirstOrDefaultAsync(ct);
        facts.Add(new(Area, "Outbox.Pending", pending?.Count ?? 0, pending?.Oldest, FactCombine.Earliest));
        facts.Add(new(Area, "Outbox.Failed", await emails.CountAsync(d => d.Status == DeliveryStatus.Failed, ct)));
        facts.Add(new(Area, "Outbox.DeadLetter", await emails.CountAsync(d => d.Status == DeliveryStatus.DeadLetter, ct)));
        var since = nowUtc.AddHours(-24);
        facts.Add(new(Area, "Outbox.Sent24h", await emails.CountAsync(d => d.Status == DeliveryStatus.Sent && d.SentUtc >= since, ct)));

        var runs = db.JobRuns.AsNoTracking();
        facts.Add(new(Area, "Jobs.Succeeded24h", await runs.CountAsync(j => j.Status == JobStatus.Succeeded && j.StartedUtc >= since, ct)));
        facts.Add(new(Area, "Jobs.Failed24h", await runs.CountAsync(j => j.Status == JobStatus.Failed && j.StartedUtc >= since, ct)));
        var staleBefore = nowUtc - StaleRunning;
        facts.Add(new(Area, "Jobs.Running", await runs.CountAsync(j => j.Status == JobStatus.Running && j.StartedUtc >= staleBefore, ct)));
        facts.Add(new(Area, "Jobs.StaleRunning", await runs.CountAsync(j => j.Status == JobStatus.Running && j.StartedUtc < staleBefore, ct)));
        var lastStart = await runs.GroupBy(_ => 1).Select(g => (DateTime?)g.Max(j => j.StartedUtc)).FirstOrDefaultAsync(ct);
        facts.Add(new(Area, "Jobs.LastStartedUtc", lastStart is null ? 0 : 1, lastStart, FactCombine.Latest));
        var lastFailed = await runs.Where(j => j.Status == JobStatus.Failed).GroupBy(_ => 1).Select(g => (DateTime?)g.Max(j => j.StartedUtc)).FirstOrDefaultAsync(ct);
        facts.Add(new(Area, "Jobs.LastFailedUtc", lastFailed is null ? 0 : 1, lastFailed, FactCombine.Latest));
        return facts;
    }
}
