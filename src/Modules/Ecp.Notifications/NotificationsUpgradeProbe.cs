using Ecp.Notifications.Domain;
using Ecp.Notifications.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Notifications;

/// <summary>Upgrade pre-flight for the outbox and background jobs: dispatch pauses while the installation is in maintenance mode, and queued messages are sent after it is released.</summary>
internal sealed class NotificationsUpgradeProbe(NotificationsDbContext db) : IUpgradeProbe
{
    public string Area => "Notifications";

    public async Task<IReadOnlyList<UpgradeFinding>> ProbeAsync(CancellationToken ct = default)
    {
        var findings = new List<UpgradeFinding>();
        var pending = await db.Deliveries.AsNoTracking().CountAsync(d => d.Status == DeliveryStatus.Pending, ct);
        if (pending > 0)
            findings.Add(new("Notifications", "UPG-NTF-001", UpgradeFindingSeverity.Info, "Messages are waiting in the outbox",
                "Queued messages are not sent while the installation is in maintenance mode. They are sent, once each, after it is released.", $"{pending} waiting message(s).", "", pending));
        var dead = await db.Deliveries.AsNoTracking().CountAsync(d => d.Status == DeliveryStatus.DeadLetter, ct);
        if (dead > 0)
            findings.Add(new("Notifications", "UPG-NTF-002", UpgradeFindingSeverity.Info, "Dead-lettered messages exist", "Messages that could not be delivered after retries are kept as they are and are not retried by an upgrade.", $"{dead} dead-lettered message(s).", "", dead));
        var running = await db.JobRuns.AsNoTracking().CountAsync(j => j.Status == JobStatus.Running, ct);
        if (running > 0)
            findings.Add(new("Notifications", "UPG-NTF-003", UpgradeFindingSeverity.Warning, "A background job is recorded as running",
                "A background job is recorded as still running. Maintenance mode lets it finish and starts no new one; the upgrade waits for the job lease to clear before it begins.", $"{running} job run(s) recorded as running.",
                "If the platform was stopped while the job ran, the record is stale and clears itself; otherwise wait for it to finish.", running));
        return findings;
    }
}
