using Ecp.Audit;
using Ecp.Migration.Domain;
using Ecp.Migration.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Migration;

public sealed record CreateProjectRequest(Guid OrganisationId, string Name, string SourceSystem, string? SourceSystemVersion, string DataOwner, DateTime ExtractionUtc);
public sealed record CreateBatchRequest(Guid ProjectId, BatchKind Kind, DuplicateStrategy DuplicateStrategy, DocumentDuplicateOption DocumentOption, bool IsLegacyLoad = false);
public sealed record BatchDto(Guid Id, Guid ProjectId, BatchKind Kind, BatchStatus Status, bool IsLegacyLoad);

public interface IMigrationService
{
    Task<Guid> CreateProjectAsync(CreateProjectRequest request, CancellationToken ct = default);
    Task<Guid> CreateBatchAsync(CreateBatchRequest request, CancellationToken ct = default);
    Task<BatchDto?> GetBatchAsync(Guid batchId, CancellationToken ct = default);
}

internal sealed class MigrationService(MigrationDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, ITenantDirectory tenants) : IMigrationService
{
    public async Task<Guid> CreateProjectAsync(CreateProjectRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        // Cross-tenant and cross-organisation references are rejected (INV-48): other tenants' organisations are invisible.
        if (!await tenants.OrganisationExistsAsync(r.OrganisationId, ct))
            throw new DomainException("organisation_not_found", "The organisation was not found.");
        var project = MigrationProject.Create(tenantId, r.OrganisationId, r.Name, r.SourceSystem, r.SourceSystemVersion, r.DataOwner, r.ExtractionUtc, clock.UtcNow);
        return await uow.ExecuteAsync(async c =>
        {
            db.Projects.Add(project);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
            { throw new DomainException("project_name_in_use", "A migration project with that name already exists."); }
            audit.Record(new AuditEntry("migration.project.created", nameof(MigrationProject), project.Id.ToString(), OrganisationId: project.OrganisationId,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { project.SourceSystem, project.DataOwner })));
            return project.Id;
        }, ct);
    }

    public async Task<Guid> CreateBatchAsync(CreateBatchRequest r, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == r.ProjectId, ct)
            ?? throw new DomainException("project_not_found", "The migration project was not found.");
        var batch = project.CreateBatch(r.Kind, r.DuplicateStrategy, r.DocumentOption, r.IsLegacyLoad, null, null, clock.UtcNow);
        return await uow.ExecuteAsync(async c =>
        {
            db.Batches.Add(batch);
            await db.SaveChangesAsync(c);
            // The batch id is carried on every audit event the batch produces (INV-41).
            audit.Record(new AuditEntry("migration.batch.created", nameof(MigrationBatch), batch.Id.ToString(), OrganisationId: batch.OrganisationId,
                MigrationBatchId: batch.Id, NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { batch.Kind, batch.DuplicateStrategy, batch.IsLegacyLoad })));
            return batch.Id;
        }, ct);
    }

    public Task<BatchDto?> GetBatchAsync(Guid batchId, CancellationToken ct = default)
        => db.Batches.AsNoTracking().Where(b => b.Id == batchId)
            .Select(b => new BatchDto(b.Id, b.ProjectId, b.Kind, b.Status, b.IsLegacyLoad)).FirstOrDefaultAsync(ct);
}

public static class MigrationModule
{
    public const string Name = "Migration";
    public const string Schema = "migration";
    public const int Tier = 7;

    public static IServiceCollection AddMigrationModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<MigrationDbContext>(Name, Tier, Schema);
        services.AddScoped<IMigrationService, MigrationService>();
        return services;
    }
}
