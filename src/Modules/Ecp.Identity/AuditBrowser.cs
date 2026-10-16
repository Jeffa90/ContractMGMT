using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Audit;
using Ecp.Audit.Domain;
using Ecp.Identity.Access;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Identity;

public sealed record AuditEventDto(
    long Sequence, DateTime OccurredUtc, string ActorType, string? ActorId, string Action, string EntityType, string EntityId, string Outcome,
    Guid? OrganisationId, string? Reason, string? OldValuesJson, string? NewValuesJson, string PreviousHash, string Hash, Guid? MigrationBatchId = null);

public sealed record AuditFilter(string? Action = null, string? EntityType = null, string? Actor = null, DateTime? FromUtc = null, DateTime? ToUtc = null,
    Guid? OrganisationId = null, int Page = 1, int PageSize = 50, Guid? MigrationBatchId = null);

public sealed record AuditEvidence(DateTime ExportedUtc, string ExportedBy, Guid TenantId, int EventCount, long? FirstSequence, long? LastSequence,
    bool TenantChainValid, long ChainEventsChecked, string Sha256OfEvents, string EventsJson);

/// <summary>
/// Reads audit events within the caller's scopes. <c>audit.read</c> shows events for the caller's organisations with their recorded values;
/// <c>audit.read_admin</c> shows administrative events only (roles, structure, configuration) without business field values.
/// Tenant-level events (no organisation) are visible only to tenant-wide assignments.
/// </summary>
public interface IAuditBrowser
{
    Task<PagedResult<AuditEventDto>> QueryAsync(AuditFilter filter, CancellationToken ct = default);
    /// <summary>Exports the matching events with hashes and the tenant chain verification result. Requires <c>audit.export</c>; the export is itself audited.</summary>
    Task<AuditEvidence> ExportAsync(AuditFilter filter, CancellationToken ct = default);
}

internal sealed class AuditBrowser(IAuditEventSource eventSource, IAccessService access, Ecp.Persistence.IUnitOfWork uow, IAuditWriter audit, AuditChainVerifier verifier, IClock clock) : IAuditBrowser
{
    /// <summary>Action prefixes that describe administration rather than business data.</summary>
    internal static readonly string[] AdministrativePrefixes =
        ["role.", "user.", "rolemapping.", "identityprovider.", "structure.", "organisation.", "tenant.", "config.", "category.ownership", "setup.", "onboarding.", "authorisation.", "approval.matrix.", "approval.delegation.", "approval.simulated"];

    public async Task<PagedResult<AuditEventDto>> QueryAsync(AuditFilter f, CancellationToken ct = default)
    {
        var query = await VisibleAsync(f, ct);
        var page = Math.Max(1, f.Page); var size = Math.Clamp(f.PageSize, 1, 200);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(e => e.Sequence).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        // Business field values are shown only where the caller holds audit.read for the event's organisation; administrative readers see the fact, not the values.
        var readGrants = await access.GrantsAsync("audit.read", ct);
        var allOrgs = readGrants.Any(g => g.OrganisationIds is null);
        var readOrgs = readGrants.Where(g => g.OrganisationIds is not null).SelectMany(g => g.OrganisationIds!).ToHashSet();
        bool MayShowValues(AuditEvent e) => readGrants.Count > 0 && (allOrgs || (e.OrganisationId is { } o && readOrgs.Contains(o)));
        return new PagedResult<AuditEventDto>(rows.Select(e => ToDto(e, redact: !MayShowValues(e))).ToList(), total, page, size);
    }

    private async Task<IQueryable<AuditEvent>> VisibleAsync(AuditFilter f, CancellationToken ct)
    {
        var readGrants = await access.GrantsAsync("audit.read", ct);
        var adminGrants = await access.GrantsAsync("audit.read_admin", ct);
        if (readGrants.Count == 0 && adminGrants.Count == 0) throw new ForbiddenException("You cannot read audit events.");

        IQueryable<AuditEvent> query = eventSource.Events;
        var readOrgs = readGrants.Any(g => g.OrganisationIds is null) ? null : readGrants.SelectMany(g => g.OrganisationIds!).Distinct().ToArray();
        var adminOrgs = adminGrants.Any(g => g.OrganisationIds is null) ? null : adminGrants.SelectMany(g => g.OrganisationIds!).Distinct().ToArray();
        var hasRead = readGrants.Count > 0; var hasAdmin = adminGrants.Count > 0;
        var readOrgList = readOrgs ?? [];
        // An event about a classified record is visible only where the caller's grant reaches that classification (privileged levels need their explicit grant). Events that describe no record carry none.
        var readClasses = readGrants.SelectMany(g => g.AllowedClassifications).Distinct().ToArray();
        var adminOrgList = adminOrgs ?? [];
        var readAllOrgs = readOrgs is null; var adminAllOrgs = adminOrgs is null;

        // (audit.read: events of the caller's organisations; tenant-wide readers also see tenant-level events)
        // OR (audit.read_admin: administrative events of the caller's organisations). The prefix list mirrors AdministrativePrefixes;
        // it is written out so every database provider translates it (a unit test keeps the two in step).
        query = query.Where(e =>
            (hasRead && (readAllOrgs || (e.OrganisationId != null && readOrgList.Contains(e.OrganisationId.Value))) && (e.Classification == null || readClasses.Contains(e.Classification)))
            || (hasAdmin
                && (e.Action.StartsWith("role.") || e.Action.StartsWith("user.") || e.Action.StartsWith("rolemapping.") || e.Action.StartsWith("identityprovider.")
                    || e.Action.StartsWith("structure.") || e.Action.StartsWith("organisation.") || e.Action.StartsWith("tenant.") || e.Action.StartsWith("config.")
                    || e.Action.StartsWith("category.ownership") || e.Action.StartsWith("setup.") || e.Action.StartsWith("onboarding.") || e.Action.StartsWith("authorisation.")
                    || e.Action.StartsWith("approval.matrix.") || e.Action.StartsWith("approval.delegation.") || e.Action.StartsWith("approval.simulated"))
                && (adminAllOrgs || (e.OrganisationId != null && adminOrgList.Contains(e.OrganisationId.Value)))));

        if (!string.IsNullOrWhiteSpace(f.Action)) { var a = f.Action.Trim(); query = query.Where(e => e.Action.StartsWith(a)); }
        if (!string.IsNullOrWhiteSpace(f.EntityType)) { var t = f.EntityType.Trim(); query = query.Where(e => e.EntityType == t); }
        if (!string.IsNullOrWhiteSpace(f.Actor)) { var who = f.Actor.Trim(); query = query.Where(e => e.ActorId == who); }
        if (f.FromUtc is { } from) query = query.Where(e => e.OccurredUtc >= from);
        if (f.ToUtc is { } to) query = query.Where(e => e.OccurredUtc < to);
        if (f.OrganisationId is { } org) query = query.Where(e => e.OrganisationId == org);
        if (f.MigrationBatchId is { } batch) query = query.Where(e => e.MigrationBatchId == batch);
        return query;
    }

    private static AuditEventDto ToDto(AuditEvent e, bool redact) => new(e.Sequence, e.OccurredUtc, e.ActorType, e.ActorId, e.Action, e.EntityType, e.EntityId, e.Outcome,
        e.OrganisationId, e.Reason, redact ? null : e.OldValuesJson, redact ? null : e.NewValuesJson, e.PreviousHash, e.Hash, e.MigrationBatchId);

    public async Task<AuditEvidence> ExportAsync(AuditFilter f, CancellationToken ct = default)
    {
        if ((await access.GrantsAsync("audit.export", ct)).Count == 0) throw new ForbiddenException("You cannot export audit evidence.");
        var query = await VisibleAsync(f with { Page = 1, PageSize = 200 }, ct);
        var events = await query.OrderBy(e => e.Sequence).Take(5000).ToListAsync(ct);
        var dtos = events.Select(e => ToDto(e, redact: false)).ToList();
        var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = false });
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var chain = await verifier.VerifyAsync(ct);
        var subject = await access.GetSubjectAsync(ct);
        var evidence = new AuditEvidence(clock.UtcNow, subject?.UserId.ToString() ?? "unknown", subject?.TenantId ?? Guid.Empty, dtos.Count,
            dtos.FirstOrDefault()?.Sequence, dtos.LastOrDefault()?.Sequence, chain.IsValid, chain.EventsChecked, sha, json);
        await uow.ExecuteAsync(c => { audit.Record(new AuditEntry("audit.exported", "AuditEvidence", sha, Reason: $"{dtos.Count} events",
            NewValuesJson: JsonSerializer.Serialize(new { count = dtos.Count, sha256 = sha }))); return Task.CompletedTask; }, ct);
        return evidence;
    }
}
