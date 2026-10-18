using Ecp.Audit;
using Ecp.Operations.Domain;
using Ecp.Operations.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Operations;

public sealed record MaintenanceStatus(bool Enabled, string Cause, string Reason, DateTime? SinceUtc, string ChangedBy)
{
    public static readonly MaintenanceStatus Off = new(false, MaintenanceCauses.Manual, "", null, "");
    public bool RestoreRelated => Enabled && MaintenanceCauses.IsRestoreRelated(Cause);
}

/// <summary>The identity recorded against an operator action. Taken from the verified session by the host, never from request input.</summary>
public sealed record OperatorIdentity(string Id, string Name);

/// <summary>
/// Maintenance mode (docs/MAINTENANCE_MODE.md): one installation-wide flag. While it is on, the host refuses new state-changing requests, background jobs stop taking new work, and every page shows a banner.
/// Health, readiness and the Operations pages stay available. Turning it on or off is audited with a reason.
/// </summary>
public interface IMaintenanceService
{
    Task<MaintenanceStatus> GetAsync(CancellationToken ct = default);
    Task<MaintenanceStatus> EnableAsync(string reason, OperatorIdentity by, string cause = MaintenanceCauses.Manual, CancellationToken ct = default);
    /// <summary>Leaves maintenance mode. A restore that failed, or one still being validated, cannot be left by accident: the caller must say so (<paramref name="acknowledgeRestoreState"/>).</summary>
    Task<MaintenanceStatus> DisableAsync(string reason, OperatorIdentity by, bool acknowledgeRestoreState = false, CancellationToken ct = default);
}

/// <summary>
/// A very short-lived in-process copy of the flag so that the host does not read the database on every request. A change made in this process is visible at once; a change made by another instance is seen within
/// <see cref="Ttl"/>. (No tenant data is held here: the flag belongs to the installation.)
/// </summary>
public sealed class MaintenanceSnapshot
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private MaintenanceStatus? _status;
    private long _loadedAt;

    public bool TryGet(out MaintenanceStatus status)
    {
        lock (_gate)
        {
            if (_status is not null && Environment.TickCount64 - _loadedAt < Ttl.TotalMilliseconds) { status = _status; return true; }
        }
        status = MaintenanceStatus.Off;
        return false;
    }

    public void Set(MaintenanceStatus status) { lock (_gate) { _status = status; _loadedAt = Environment.TickCount64; } }
    public void Invalidate() { lock (_gate) _status = null; }
}

internal sealed class MaintenanceService(OperationsDbContext db, IUnitOfWork uow, IAuditWriter audit, IAfterCommit afterCommit, TenantContext tenant, IClock clock, MaintenanceSnapshot snapshot) : IMaintenanceService
{
    private static MaintenanceStatus Map(MaintenanceState? s) => s is null || !s.Enabled ? MaintenanceStatus.Off : new(true, s.Cause, s.Reason, s.SinceUtc, s.ChangedBy);

    public async Task<MaintenanceStatus> GetAsync(CancellationToken ct = default)
    {
        if (snapshot.TryGet(out var cached)) return cached;
        using var _ = tenant.UseInstallationScope();
        var status = Map(await db.Maintenance.AsNoTracking().FirstOrDefaultAsync(ct));
        snapshot.Set(status);
        return status;
    }

    public async Task<MaintenanceStatus> EnableAsync(string reason, OperatorIdentity by, string cause = MaintenanceCauses.Manual, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("reason_required", "A reason is required to enter maintenance mode.");
        using var _ = tenant.UseInstallationScope();
        var status = await uow.ExecuteAsync(async c =>
        {
            var row = await db.Maintenance.FirstOrDefaultAsync(c);
            if (row is null) { row = MaintenanceState.Create(clock.UtcNow); db.Maintenance.Add(row); }
            var was = Map(row);
            row.Enable(cause, reason.Trim(), by.Id, clock.UtcNow);
            audit.Record(new AuditEntry("operations.maintenance.enabled", "MaintenanceState", MaintenanceState.RowId.ToString(), Reason: reason.Trim(),
                OldValuesJson: System.Text.Json.JsonSerializer.Serialize(new { enabled = was.Enabled, cause = was.Cause }), NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { enabled = true, cause, by = by.Id })));
            await db.SaveChangesAsync(c);
            afterCommit.Register(snapshot.Invalidate);
            return Map(row);
        }, ct);
        snapshot.Set(status);
        return status;
    }

    public async Task<MaintenanceStatus> DisableAsync(string reason, OperatorIdentity by, bool acknowledgeRestoreState = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("reason_required", "A reason is required to leave maintenance mode.");
        using var _ = tenant.UseInstallationScope();
        var status = await uow.ExecuteAsync(async c =>
        {
            var row = await db.Maintenance.FirstOrDefaultAsync(c);
            if (row is null || !row.Enabled) return MaintenanceStatus.Off;
            if (MaintenanceCauses.IsRestoreRelated(row.Cause) && !acknowledgeRestoreState)
                throw new DomainException("restore_state", row.Cause == MaintenanceCauses.RestoreFailed
                    ? "A restore failed and the installation is held in maintenance mode. It stays held until an operator confirms, knowingly, that it should be released."
                    : "A restore is in progress or waiting for post-restore validation. Maintenance mode is released by the restore workflow when validation passes, or by an operator who confirms the restore state.");
            var was = row.Cause;
            row.Disable(reason.Trim(), by.Id, clock.UtcNow);
            audit.Record(new AuditEntry("operations.maintenance.disabled", "MaintenanceState", MaintenanceState.RowId.ToString(), Reason: reason.Trim(),
                OldValuesJson: System.Text.Json.JsonSerializer.Serialize(new { enabled = true, cause = was }), NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { enabled = false, by = by.Id, acknowledgedRestoreState = acknowledgeRestoreState })));
            await db.SaveChangesAsync(c);
            afterCommit.Register(snapshot.Invalidate);
            return MaintenanceStatus.Off;
        }, ct);
        snapshot.Set(status);
        return status;
    }
}
