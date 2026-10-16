using Ecp.Audit.Domain;
using Ecp.Audit.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Audit;

public enum AuditOutcome { Success, Denied, Failed }

public sealed record AuditEntry(
    string Action, string EntityType, string EntityId, AuditOutcome Outcome = AuditOutcome.Success,
    Guid? OrganisationId = null, string? Reason = null, string? OldValuesJson = null, string? NewValuesJson = null,
    Guid? MigrationBatchId = null, Guid? ConfigReleaseId = null, Guid? ElevationId = null);

/// <summary>Records audit events. Must be called inside <see cref="IUnitOfWork"/>; events commit with the change.</summary>
public interface IAuditWriter
{
    void Record(AuditEntry entry);
}

internal sealed class AuditBuffer
{
    private readonly List<AuditEntry> _entries = [];
    public void Add(AuditEntry entry) => _entries.Add(entry);
    public List<AuditEntry> Drain() { var copy = _entries.ToList(); _entries.Clear(); return copy; }
}

internal sealed class AuditWriter(AuditBuffer buffer, IScopedConnection connection) : IAuditWriter
{
    public void Record(AuditEntry entry)
    {
        if (connection.Transaction is null)
            throw new InvalidOperationException("Audit events must be recorded inside IUnitOfWork.ExecuteAsync so they commit with the change.");
        buffer.Add(entry);
    }
}

/// <summary>Writes buffered events inside the unit-of-work transaction, extending the tenant's hash chain.</summary>
internal sealed class AuditFlusher(AuditBuffer buffer, AuditDbContext db, ITenantContext tenant, IActorContext actor, IClock clock, IScopedConnection connection) : IBeforeCommitHook
{
    public async Task BeforeCommitAsync(CancellationToken ct)
    {
        var entries = buffer.Drain();
        if (entries.Count == 0) return;
        var tenantId = tenant.RequireTenantId();
        await db.EnlistAsync(ct);
        if (connection.Provider == DatabaseProvider.SqlServer)
        {
            // Serialises chain extension per tenant for the life of the transaction so the chain cannot fork.
            await db.Database.ExecuteSqlRawAsync(
                "DECLARE @r int; EXEC @r = sp_getapplock @Resource = {0}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000; IF @r < 0 THROW 50001, N'Could not acquire the audit chain lock.', 1;",
                [$"audit:{tenantId:N}"], ct);
        }
        var last = await db.Events.Where(e => e.TenantId == tenantId).OrderByDescending(e => e.Sequence)
            .Select(e => new { e.Sequence, e.Hash }).FirstOrDefaultAsync(ct);
        var sequence = last?.Sequence ?? 0;
        var previous = last?.Hash ?? AuditEvent.GenesisHash;
        foreach (var entry in entries)
        {
            var ev = AuditEvent.Create(tenantId, ++sequence, clock.UtcNow, actor, entry, previous);
            previous = ev.Hash;
            db.Events.Add(ev);
        }
        await db.SaveChangesAsync(ct);
    }
}

public sealed class AuditChainVerifier(AuditDbContext db)
{
    /// <summary>Verifies the current tenant's chain. Streams events; memory use is constant.</summary>
    public async Task<AuditChainVerification> VerifyAsync(CancellationToken ct = default)
    {
        var tenantId = db.CurrentTenantId;
        var events = db.Events.AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.Sequence).AsAsyncEnumerable();
        long expected = 1, count = 0; var previous = AuditEvent.GenesisHash;
        await foreach (var e in events.WithCancellation(ct))
        {
            if (e.Sequence != expected) return new(false, count, expected, $"Sequence gap: expected {expected}, found {e.Sequence}.");
            if (e.PreviousHash != previous) return new(false, count, e.Sequence, "Previous hash does not match the preceding event.");
            if (AuditHasher.Compute(e, e.PreviousHash) != e.Hash) return new(false, count, e.Sequence, "Event content does not match its hash.");
            previous = e.Hash; expected++; count++;
        }
        return new(true, count, null, null);
    }
}

public static class AuditModule
{
    public const string Name = "Audit";
    public const string Schema = "audit";
    public const int Tier = 1;

    public static IServiceCollection AddAuditModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<AuditDbContext>(Name, Tier, Schema);
        services.AddScoped<AuditBuffer>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IBeforeCommitHook, AuditFlusher>();
        services.AddScoped<AuditChainVerifier>();
        return services;
    }
}
