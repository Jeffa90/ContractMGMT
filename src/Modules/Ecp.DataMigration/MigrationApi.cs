using Ecp.Audit;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecp.DataMigration;

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
            throw new DomainException("organisation_not_found", "The [[organisation|lower]] was not found.");
        var project = MigrationProject.Create(tenantId, r.OrganisationId, r.Name, r.SourceSystem, r.SourceSystemVersion, r.DataOwner, r.ExtractionUtc, clock.UtcNow, "system");
        return await uow.ExecuteAsync(async c =>
        {
            db.Projects.Add(project);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
            { throw new DomainException("project_name_in_use", "[[migration.project|lower|a|cap]] with that name already exists."); }
            audit.Record(new AuditEntry("migration.project.created", nameof(MigrationProject), project.Id.ToString(), OrganisationId: project.OrganisationId,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { project.SourceSystem, project.DataOwner })));
            return project.Id;
        }, ct);
    }

    public async Task<Guid> CreateBatchAsync(CreateBatchRequest r, CancellationToken ct = default)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == r.ProjectId, ct)
            ?? throw new DomainException("project_not_found", "The [[migration.project|lower]] was not found.");
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

/// <summary>The cutover freeze (MIGRATION_SUPPLEMENT 5.6): while a project's target freeze is in force, the Contracts module refuses to create contracts for people in that organisation.</summary>
internal sealed class MigrationCutoverFreeze(MigrationDbContext db, IClock clock) : Ecp.Contracts.ICutoverFreeze
{
    public async Task<string?> CreationBlockedAsync(Guid organisationId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var frozen = await db.Projects.AsNoTracking().Where(p => p.OrganisationId == organisationId && p.Status == ProjectStatus.Active && p.TargetFreezeEnabled
            && p.FreezeStartUtc <= now && now < p.FreezeEndUtc).Select(p => p.FreezeEndUtc).FirstOrDefaultAsync(ct);
        return frozen is null ? null : $"Contract creation is frozen for this organisation during a migration cutover until {frozen:yyyy-MM-dd HH:mm} UTC.";
    }
}

public static class MigrationModule
{
    public const string Name = "Migration";
    public const string Schema = "migration";
    public const int Tier = 7;

    public static IServiceCollection AddMigrationModule(this IServiceCollection services, Action<MigrationOptions>? configure = null)
    {
        services.AddOptions<MigrationOptions>();
        if (configure is not null) services.Configure(configure);
        services.AddModuleDbContext<MigrationDbContext>(Name, Tier, Schema);
        services.AddScoped<IMigrationService, MigrationService>();
        services.AddScoped<MigrationKernel>();
        services.AddScoped<BatchRuntime>();
        services.AddScoped<MigrationReconciler>();
        services.TryAddSingleton<IMigrationFaults, NoMigrationFaults>();
        services.AddScoped<IMigrationProjects, MigrationProjectService>();
        services.AddScoped<IMappingProfiles, MappingProfileService>();
        services.AddScoped<IMigrationBatches, MigrationBatchService>();
        services.AddScoped<IMigrationIntake, MigrationIntakeService>();
        services.AddScoped<IMigrationValidation, MigrationValidationService>();
        services.AddScoped<IMigrationCommit, MigrationCommitService>();
        services.AddScoped<IMigrationRollback, MigrationRollbackService>();
        services.AddScoped<IMigrationReports, MigrationReportService>();
        // The real cutover freeze replaces the Contracts module's default (nothing frozen).
        services.Replace(ServiceDescriptor.Scoped<Ecp.Contracts.ICutoverFreeze, MigrationCutoverFreeze>());
        return services;
    }
}
