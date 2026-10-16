using System.Text.Json;
using Ecp.Audit;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Persistence;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.DataMigration;

/// <summary>The permissions the migration slice uses (permission-catalogue.csv).</summary>
public static class MigrationPermissions
{
    public const string Stage = "migration.stage";
    public const string Commit = "migration.commit";
    public const string LegacyLoad = "migration.legacy_load";
    public const string Rollback = "migration.rollback";
    public const string Accept = "migration.accept";
}

/// <summary>
/// Shared plumbing for the migration services: loading a project or batch for the signed-in user (404 when it is not theirs to see, 403 when they lack the permission),
/// who is acting, and recording messages. Authorisation is evaluated against the stored record's organisation, never against anything the caller supplies.
/// </summary>
internal sealed class MigrationKernel(MigrationDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IActorContext actor, IClock clock, IAccessService access,
    CurrentUser currentUser, IUserService users, IOptions<MigrationOptions> options)
{
    public MigrationDbContext Db => db;
    public IUnitOfWork Uow => uow;
    public IAuditWriter Audit => audit;
    public IClock Clock => clock;
    public MigrationOptions Options => options.Value;
    public Guid TenantId => tenant.RequireTenantId();
    public Guid? UserId => currentUser.UserId;
    public IAccessService Access => access;
    private string? _label;

    /// <summary>Who is acting, for records people read (a display name; the user id is on the audit event).</summary>
    public async Task<string> ActorLabelAsync(CancellationToken ct)
    {
        if (_label is not null) return _label;
        if (currentUser.UserId is { } id && await users.GetAsync(id, ct) is { } u) return _label = u.DisplayName;
        return _label = actor.ActorId ?? "system";
    }

    public static ResourceDescriptor Describe(Guid tenantId, Guid organisationId, string id)
        => new("MigrationProject", tenantId, organisationId, Id: id, Dimensions: ScopeDimensions.OrganisationOnly);

    public async Task<MigrationProject> ProjectAsync(Guid projectId, string permission, bool track, CancellationToken ct)
    {
        var query = track ? db.Projects.AsTracking() : db.Projects.AsNoTracking();
        var project = await query.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync(permission, Describe(project.TenantId, project.OrganisationId, project.Id.ToString()), ct);
        return project;
    }

    public async Task<(MigrationBatch Batch, MigrationProject Project)> BatchAsync(Guid batchId, string permission, bool track, CancellationToken ct)
    {
        var query = track ? db.Batches.AsTracking() : db.Batches.AsNoTracking();
        var batch = await query.FirstOrDefaultAsync(b => b.Id == batchId, ct) ?? throw new ResourceNotFoundException();
        var project = await (track ? db.Projects.AsTracking() : db.Projects.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == batch.ProjectId, ct) ?? throw new ResourceNotFoundException();
        await access.RequireAsync(permission, Describe(project.TenantId, project.OrganisationId, project.Id.ToString()), ct);
        return (batch, project);
    }

    public async Task<bool> HoldsAsync(string permission, MigrationProject project, CancellationToken ct)
        => (await access.CheckSilentAsync(permission, Describe(project.TenantId, project.OrganisationId, project.Id.ToString()), ct)).Allowed;

    public void Record(string action, string entityType, string entityId, Guid organisationId, Guid? batchId = null, object? values = null, string? reason = null)
        => audit.Record(new AuditEntry(action, entityType, entityId, OrganisationId: organisationId, MigrationBatchId: batchId, Reason: reason,
            NewValuesJson: values is null ? null : JsonSerializer.Serialize(values)));

    public StagingMessage Message(Guid batchId, MessagePhase phase, MessageSubject subject, Guid? subjectId, string location, bool isError, string code, string text, string? field = null)
        => StagingMessage.Create(TenantId, batchId, phase, subject, subjectId, location, isError ? MessageSeverity.Error : MessageSeverity.Warning, code, text, field, clock.UtcNow);
}
