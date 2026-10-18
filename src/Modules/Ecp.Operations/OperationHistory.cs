using Ecp.Operations.Domain;
using Ecp.Operations.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Operations;

public sealed record OperationRunDto(Guid Id, string Kind, string Status, string Reference, string Reason, string OperatorId, string OperatorName, string CorrelationId, DateTime StartedUtc, DateTime? CompletedUtc, string Summary, string DetailJson);

/// <summary>History of operations (backups, restores, validations, evidence exports). Rows hold a sanitised summary only.</summary>
public interface IOperationHistory
{
    Task<Guid> StartAsync(string kind, string reference, string reason, OperatorIdentity by, CancellationToken ct = default);
    Task FinishAsync(Guid id, string status, string summary, string detailJson, CancellationToken ct = default);
    Task<IReadOnlyList<OperationRunDto>> ListAsync(string? kind, int take, CancellationToken ct = default);
    Task<OperationRunDto?> FindAsync(Guid id, CancellationToken ct = default);
    Task<OperationRunDto?> FindByReferenceAsync(string kind, string reference, CancellationToken ct = default);
}

internal sealed class OperationHistory(OperationsDbContext db, IUnitOfWork uow, TenantContext tenant, IActorContext actor, IClock clock, Microsoft.Extensions.Logging.ILogger<OperationHistory> log) : IOperationHistory
{
    private static OperationRunDto Map(OperationRun r) => new(r.Id, r.Kind, r.Status, r.Reference, r.Reason, r.OperatorId, r.OperatorName, r.CorrelationId, r.StartedUtc, r.CompletedUtc, r.Summary, r.DetailJson);

    public async Task<Guid> StartAsync(string kind, string reference, string reason, OperatorIdentity by, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        return await uow.ExecuteAsync(async c =>
        {
            var run = OperationRun.Start(kind, reference, reason, by.Id, by.Name, actor.CorrelationId, clock.UtcNow);
            db.Runs.Add(run);
            await db.SaveChangesAsync(c);
            return run.Id;
        }, ct);
    }

    public async Task FinishAsync(Guid id, string status, string summary, string detailJson, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        await uow.ExecuteAsync(async c =>
        {
            var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == id, c) ?? throw new ResourceNotFoundException();
            run.Finish(status, summary, detailJson, clock.UtcNow);
            await db.SaveChangesAsync(c);
            OperationalLog.OperationFinished(log, run.Kind, status, run.CorrelationId);
        }, ct);
    }

    public async Task<IReadOnlyList<OperationRunDto>> ListAsync(string? kind, int take, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        var q = db.Runs.AsNoTracking();
        if (kind is not null) q = q.Where(r => r.Kind == kind);
        return (await q.OrderByDescending(r => r.StartedUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<OperationRunDto?> FindAsync(Guid id, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        var r = await db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? null : Map(r);
    }

    public async Task<OperationRunDto?> FindByReferenceAsync(string kind, string reference, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        var r = await db.Runs.AsNoTracking().Where(x => x.Kind == kind && x.Reference == reference).OrderByDescending(x => x.StartedUtc).FirstOrDefaultAsync(ct);
        return r is null ? null : Map(r);
    }
}
