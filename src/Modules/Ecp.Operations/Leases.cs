using System.Text.RegularExpressions;
using Ecp.Audit;
using Ecp.Operations.Domain;
using Ecp.Operations.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Operations;

/// <summary>A held lease. <see cref="Generation"/> is a fencing token: it changes each time the lease changes hands.</summary>
public sealed record LeaseGrant(string Name, string ScopeKey, string OwnerId, int Generation, DateTime ExpiresUtc);

public sealed record LeaseInfo(string Name, string ScopeKey, string OwnerId, string OwnerDescription, DateTime AcquiredUtc, DateTime HeartbeatUtc, DateTime ExpiresUtc, DateTime? ReleasedUtc, int Generation, bool IsActive);

/// <summary>
/// Operation leases (docs/OPERATION_LEASES.md): at most one owner holds a given (name, scope) at a time, across every instance sharing the database. Acquisition is decided by the database (a unique key and an
/// optimistic-concurrency token), not by in-process locking, so it holds across processes. Call these outside a unit of work: each call commits on its own.
/// </summary>
public interface IOperationLeases
{
    /// <summary>The scope key for installation-wide work.</summary>
    const string InstallationScope = "installation";

    /// <summary>Takes the lease if it is free, released or expired, or already held by <paramref name="ownerId"/>. Returns null when another owner holds it. An expired lease taken from another owner is audited.</summary>
    Task<LeaseGrant?> TryAcquireAsync(string name, string scopeKey, string ownerId, string ownerDescription, TimeSpan ttl, CancellationToken ct = default);
    /// <summary>Extends a held lease. Returns null if the lease was lost (expired and taken, or stolen); the caller must stop the work.</summary>
    Task<LeaseGrant?> RenewAsync(LeaseGrant grant, TimeSpan ttl, CancellationToken ct = default);
    Task ReleaseAsync(LeaseGrant grant, CancellationToken ct = default);
    /// <summary>Takes a lease from its current owner, whatever its state. For an operator who has established that the owner is gone. Requires a reason and is audited; the previous owner's next renewal fails.</summary>
    Task<LeaseGrant> StealAsync(string name, string scopeKey, string newOwnerId, string ownerDescription, string reason, TimeSpan ttl, CancellationToken ct = default);
    Task<IReadOnlyList<LeaseInfo>> ListAsync(CancellationToken ct = default);
    /// <summary>
    /// Releases every lease that is still active. For a restored copy: a lease recorded in a backup belongs to the process that was running when the backup was made, which does not exist in the copy, so none of them may
    /// stand in the way of the work done on the copy. Audited; returns how many were released.
    /// </summary>
    Task<int> ReleaseAllAsync(string reason, CancellationToken ct = default);
}

internal sealed partial class OperationLeases(OperationsDbContext db, IUnitOfWork uow, IAuditWriter audit, TenantContext tenant, IClock clock) : IOperationLeases
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._:-]{0,98}$", RegexOptions.NonBacktracking)] private static partial Regex ValidName();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,98}$", RegexOptions.NonBacktracking)] private static partial Regex ValidScope();

    private static void Check(string name, string scope, string owner)
    {
        if (!ValidName().IsMatch(name)) throw new DomainException("invalid_lease", "A lease name uses lower-case letters, digits and . _ : - only.");
        if (!ValidScope().IsMatch(scope)) throw new DomainException("invalid_lease", "A lease scope is the word installation or an identifier.");
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 200) throw new DomainException("invalid_lease", "A lease owner is required.");
    }

    private static LeaseGrant Grant(OperationLease l) => new(l.Name, l.ScopeKey, l.OwnerId, l.Generation, l.ExpiresUtc);

    public async Task<LeaseGrant?> TryAcquireAsync(string name, string scopeKey, string ownerId, string ownerDescription, TimeSpan ttl, CancellationToken ct = default)
    {
        Check(name, scopeKey, ownerId);
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(12)) throw new DomainException("invalid_lease", "A lease lasts between a moment and twelve hours.");
        using var _ = tenant.UseInstallationScope();
        try
        {
            return await uow.ExecuteAsync<LeaseGrant?>(async c =>
            {
                var now = clock.UtcNow;
                var lease = await db.Leases.FirstOrDefaultAsync(l => l.Name == name && l.ScopeKey == scopeKey, c);
                if (lease is null)
                {
                    lease = OperationLease.Create(name, scopeKey, ownerId, ownerDescription, now, ttl);
                    db.Leases.Add(lease);
                    await db.SaveChangesAsync(c);
                    return Grant(lease);
                }
                if (lease.IsActiveAt(now) && lease.OwnerId != ownerId) return null;
                if (lease.IsActiveAt(now)) lease.Renew(now, ttl);
                else
                {
                    var staleFrom = lease.ReleasedUtc is null && lease.OwnerId != ownerId ? lease.OwnerId : null;
                    lease.Take(ownerId, ownerDescription, now, ttl);
                    if (staleFrom is not null)
                        audit.Record(new AuditEntry("operations.lease.expired_takeover", "OperationLease", $"{name}/{scopeKey}", Reason: "The previous owner stopped renewing the lease, so it expired and was taken over.",
                            NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { name, scope = scopeKey, previousOwner = staleFrom, newOwner = ownerId, generation = lease.Generation })));
                }
                await db.SaveChangesAsync(c);
                return Grant(lease);
            }, ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
        {
            // Another instance won the race (the unique key or the concurrency token said so). Not an error: the work belongs to them.
            db.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task<LeaseGrant?> RenewAsync(LeaseGrant grant, TimeSpan ttl, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        try
        {
            return await uow.ExecuteAsync<LeaseGrant?>(async c =>
            {
                var now = clock.UtcNow;
                var lease = await db.Leases.FirstOrDefaultAsync(l => l.Name == grant.Name && l.ScopeKey == grant.ScopeKey, c);
                if (lease is null || lease.OwnerId != grant.OwnerId || lease.Generation != grant.Generation || lease.ReleasedUtc is not null) return null;
                if (lease.ExpiresUtc <= now) return null;      // it lapsed: someone else may be about to take it, so the work stops rather than race them
                lease.Renew(now, ttl);
                await db.SaveChangesAsync(c);
                return Grant(lease);
            }, ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException) { db.ChangeTracker.Clear(); return null; }
    }

    public async Task ReleaseAsync(LeaseGrant grant, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        try
        {
            await uow.ExecuteAsync(async c =>
            {
                var lease = await db.Leases.FirstOrDefaultAsync(l => l.Name == grant.Name && l.ScopeKey == grant.ScopeKey, c);
                if (lease is null || lease.OwnerId != grant.OwnerId || lease.Generation != grant.Generation) return;   // no longer ours: nothing to release
                lease.Release(clock.UtcNow);
                await db.SaveChangesAsync(c);
            }, ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException) { db.ChangeTracker.Clear(); }
    }

    public async Task<LeaseGrant> StealAsync(string name, string scopeKey, string newOwnerId, string ownerDescription, string reason, TimeSpan ttl, CancellationToken ct = default)
    {
        Check(name, scopeKey, newOwnerId);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("reason_required", "A reason is required to take over an operation lease.");
        using var _ = tenant.UseInstallationScope();
        return await uow.ExecuteAsync(async c =>
        {
            var now = clock.UtcNow;
            var lease = await db.Leases.FirstOrDefaultAsync(l => l.Name == name && l.ScopeKey == scopeKey, c);
            string? previous = null;
            if (lease is null) { lease = OperationLease.Create(name, scopeKey, newOwnerId, ownerDescription, now, ttl); db.Leases.Add(lease); }
            else { previous = lease.IsActiveAt(now) ? lease.OwnerId : null; lease.Take(newOwnerId, ownerDescription, now, ttl); }
            audit.Record(new AuditEntry("operations.lease.taken_over", "OperationLease", $"{name}/{scopeKey}", Reason: reason.Trim(),
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { name, scope = scopeKey, previousOwnerWasActive = previous is not null, newOwner = newOwnerId, generation = lease.Generation })));
            await db.SaveChangesAsync(c);
            return Grant(lease);
        }, ct);
    }

    public async Task<int> ReleaseAllAsync(string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("reason_required", "A reason is required to release leases.");
        using var _ = tenant.UseInstallationScope();
        return await uow.ExecuteAsync(async c =>
        {
            var now = clock.UtcNow;
            var active = (await db.Leases.ToListAsync(c)).Where(l => l.IsActiveAt(now)).ToList();
            foreach (var l in active) l.Release(now);
            if (active.Count > 0)
                audit.Record(new AuditEntry("operations.lease.released_all", "OperationLease", "all", Reason: reason.Trim(), NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { released = active.Select(l => l.Name + "/" + l.ScopeKey).Order(StringComparer.Ordinal) })));
            await db.SaveChangesAsync(c);
            return active.Count;
        }, ct);
    }

    public async Task<IReadOnlyList<LeaseInfo>> ListAsync(CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        var now = clock.UtcNow;
        var rows = await db.Leases.AsNoTracking().OrderBy(l => l.Name).ThenBy(l => l.ScopeKey).ToListAsync(ct);
        return rows.Select(l => new LeaseInfo(l.Name, l.ScopeKey, l.OwnerId, l.OwnerDescription, l.AcquiredUtc, l.HeartbeatUtc, l.ExpiresUtc, l.ReleasedUtc, l.Generation, l.IsActiveAt(now))).ToList();
    }
}

/// <summary>
/// Holds a lease for the life of an operation and keeps it renewed from a timer; <see cref="Lost"/> becomes true if a renewal fails, and the operation must stop. Renewal runs in a scope of its own (a scope's database
/// connection is not safe to share between the operation and its timer).
/// </summary>
public sealed class LeaseHolder : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeSpan _ttl;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private LeaseGrant _grant;

    private LeaseHolder(IServiceScopeFactory scopes, LeaseGrant grant, TimeSpan ttl)
    {
        _scopes = scopes; _grant = grant; _ttl = ttl;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(ttl / 3);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var next = await scope.ServiceProvider.GetRequiredService<IOperationLeases>().RenewAsync(_grant, _ttl, _stop.Token);
                    if (next is null) { Lost = true; return; }
                    _grant = next;
                }
            }
            catch (OperationCanceledException) { }
            catch { Lost = true; }
        });
    }

    public bool Lost { get; private set; }
    public LeaseGrant Grant => _grant;

    /// <summary>Acquires the lease and starts renewing it, or returns null if another owner holds it.</summary>
    public static async Task<LeaseHolder?> TryAcquireAsync(IServiceScopeFactory scopes, string name, string scopeKey, string ownerId, string ownerDescription, TimeSpan ttl, CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var grant = await scope.ServiceProvider.GetRequiredService<IOperationLeases>().TryAcquireAsync(name, scopeKey, ownerId, ownerDescription, ttl, ct);
        return grant is null ? null : new LeaseHolder(scopes, grant, ttl);
    }

    public void ThrowIfLost()
    {
        if (Lost) throw new DomainException("lease_lost", "The operation lease was lost (it expired or was taken over), so the operation stopped to avoid running twice.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop; } catch { /* the loop reports its own failure through Lost */ }
        if (!Lost)
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IOperationLeases>().ReleaseAsync(_grant);
        }
        _stop.Dispose();
    }
}
