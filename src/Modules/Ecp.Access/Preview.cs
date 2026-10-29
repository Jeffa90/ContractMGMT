using System.Text.Json;
using Ecp.Access.Persistence;
using Ecp.Access.Domain;
using Ecp.Audit;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Access;

/// <summary>A preview the host may run a request under (already re-validated).</summary>
public sealed record PreviewGrant(Guid PreviewId, Guid PreviewerUserId, Guid TargetUserId, string TargetName, string PreviewerName, DateTime ExpiresUtc);

/// <summary>Whether the signed-in administrator may preview a person, and if not, why in words.</summary>
public sealed record PreviewEligibility(bool Allowed, string? Why);

/// <summary>
/// Test/Demo-only "Preview as this user" (docs/UX_REDESIGN.md section 7A). The previewer sees the application exactly as the person would, using the person's real access; the session is read-only,
/// audited, time-limited and never changes the person's account, identity or credentials. Every rule is enforced here, not in the page.
/// </summary>
public interface IPreviewService
{
    /// <summary>False in Production, where preview does not exist.</summary>
    bool Available { get; }
    TimeSpan Lifetime { get; }
    Task<PreviewEligibility> CanPreviewAsync(Guid targetUserId, CancellationToken ct = default);

    /// <summary>Starts a preview of the person for the signed-in administrator. Needs a recent sign-in and a reason.</summary>
    Task<PreviewGrant> StartAsync(Guid targetUserId, string reason, CancellationToken ct = default);

    /// <summary>Trusted, for the host: re-validates the preview a session names. Null when it is over, expired, not for this previewer, or no longer allowed.</summary>
    Task<PreviewGrant?> ResolveAsync(Guid previewId, Guid previewerUserId, CancellationToken ct = default);

    /// <summary>Trusted, for the host: ends a preview and records it.</summary>
    Task EndAsync(Guid previewId, string reason, CancellationToken ct = default);

    /// <summary>Trusted, for the host: a write was refused during the preview (method and route pattern only).</summary>
    Task RecordRefusedWriteAsync(Guid previewId, string method, string route, CancellationToken ct = default);
}

internal sealed class PreviewService(
    IPeopleDirectory people, IUserService users, IRoleAssignmentService roles, IAccessDirectory directory, IAccessService access, IUnitOfWork uow, IAuditWriter audit, IClock clock,
    ActorContext actor, ReauthenticationContext reauth, PreviewContext previewContext, ElevationContext elevation, IdentityOptions identity, ITenantContext tenant, AccessDbContext db) : IPreviewService
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(30);

    public bool Available => identity.Mode != EcpMode.Production;
    public TimeSpan Lifetime => DefaultLifetime;

    public async Task<PreviewEligibility> CanPreviewAsync(Guid targetUserId, CancellationToken ct = default)
    {
        var why = await WhyNotAsync(access.CurrentUserId, targetUserId, authorised: true, ct);
        return new PreviewEligibility(why is null, why);
    }

    /// <summary>The single rule set, used when starting and again on every request of the preview.</summary>
    private async Task<string?> WhyNotAsync(Guid? previewerId, Guid targetId, bool authorised, CancellationToken ct)
    {
        if (!Available) return "Preview is available only in Demo and Test.";
        if (previewerId is not { } me) return "Sign in to preview.";
        if (me == targetId) return "You are already you. Preview is for looking at someone else's access.";
        if (elevation.Current is not null) return "Preview is not available during support or emergency access.";
        var target = await users.GetAsync(targetId, ct);
        if (target is null) return "That person could not be found.";
        if (target.Status == UserStatus.Disabled) return $"{target.DisplayName} is disabled, so there is nothing to preview.";
        var previewer = await directory.LoadSubjectAsync(me, ct);
        if (previewer is null) return "Your own account is not active.";
        var now = clock.UtcNow;
        var scale = await access.ClassificationScaleAsync(ct);
        var evaluator = AccessEvaluator.Default;
        if (evaluator.GrantsFor(previewer, scale, now, "users.manage").Count == 0) return "You are not allowed to manage people, so you cannot preview them.";
        var assignments = (await roles.ListForUserAsync(targetId, includeEnded: false, ct)).Where(a => a.IsActive).ToList();
        if (assignments.Any(a => a.Scope.Level == ScopeLevel.Installation)) return $"{target.DisplayName} administers the whole installation, which cannot be previewed.";
        // Containment: you may preview only what you could give. A preview must never show more than the previewer could have granted.
        var beyond = new List<string>();
        foreach (var a in assignments)
        {
            var refused = DelegationPolicy.WhyNot(evaluator.Matrix, previewer.Assignments, now, a.RoleCode, a.Scope, scale);
            if (refused is not null || (a.PrivilegedAccess && !DelegationPolicy.MayGrantPrivilegedAccess(previewer.Assignments, now))) beyond.Add(a.RoleCode);
        }
        _ = authorised;
        if (beyond.Count > 0) return $"{target.DisplayName} has access you are not allowed to give. Ask a more senior administrator to preview them.";
        return null;
    }

    public async Task<PreviewGrant> StartAsync(Guid targetUserId, string reason, CancellationToken ct = default)
    {
        if (!Available) throw new DomainException("test_only", "Preview is available only in Demo and Test.");
        if (previewContext.Active) throw new DomainException("already_previewing", "Exit the current preview before starting another.");
        var why = Guard.NotBlank(reason, "Reason", 1000);
        reauth.Require(clock.UtcNow, StepUpWindow);
        var me = access.CurrentUserId ?? throw new ForbiddenException("Sign in to preview.");
        var person = await people.GetManageableUserAsync(targetUserId, ct);   // not found unless you may manage this person (and the same tenant)
        var refusal = await WhyNotAsync(me, targetUserId, authorised: true, ct);
        if (refusal is not null) throw new DomainException("preview_refused", refusal);

        var tenantId = tenant.RequireTenantId();
        var now = clock.UtcNow;
        var orgs = (await roles.ListForUserAsync(targetUserId, includeEnded: false, ct)).SelectMany(a => a.Scope.OrganisationIds);
        var session = PreviewSession.Start(tenantId, me, targetUserId, orgs, why, now, DefaultLifetime, actor.CorrelationId);
        var previewerName = (await users.GetAsync(me, ct))?.DisplayName ?? "an administrator";
        await uow.ExecuteAsync(async c =>
        {
            db.PreviewSessions.Add(session);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("preview.started", "User", targetUserId.ToString(), Reason: why,
                NewValuesJson: JsonSerializer.Serialize(new { previewId = session.Id, previewer = me, target = targetUserId, tenant = tenantId, organisations = session.OrganisationIdsJson, expires = session.ExpiresUtc })));
        }, ct);
        return new PreviewGrant(session.Id, me, targetUserId, person.DisplayName, previewerName, session.ExpiresUtc);
    }

    public async Task<PreviewGrant?> ResolveAsync(Guid previewId, Guid previewerUserId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var row = await db.PreviewSessions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == previewId, ct);
        if (row is null || row.PreviewerUserId != previewerUserId || !row.IsOpenAt(now) || !Available) return null;
        if (await WhyNotAsync(previewerUserId, row.TargetUserId, authorised: false, ct) is not null) return null;
        var target = await users.GetAsync(row.TargetUserId, ct);
        var previewer = await users.GetAsync(previewerUserId, ct);
        return new PreviewGrant(row.Id, row.PreviewerUserId, row.TargetUserId, target!.DisplayName, previewer?.DisplayName ?? "an administrator", row.ExpiresUtc);
    }

    public async Task EndAsync(Guid previewId, string reason, CancellationToken ct = default)
    {
        using var _ = previewContext.SuspendGuard();
        var row = await db.PreviewSessions.FirstOrDefaultAsync(p => p.Id == previewId, ct);
        if (row is null || row.EndedUtc is not null) return;
        var now = clock.UtcNow;
        await uow.ExecuteAsync(async c =>
        {
            row.End(now, reason);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("preview.ended", "User", row.TargetUserId.ToString(), Reason: reason,
                NewValuesJson: JsonSerializer.Serialize(new { previewId = row.Id, previewer = row.PreviewerUserId, target = row.TargetUserId, seconds = (int)(now - row.StartedUtc).TotalSeconds, refusedWrites = row.RefusedWrites })));
        }, ct);
    }

    public async Task RecordRefusedWriteAsync(Guid previewId, string method, string route, CancellationToken ct = default)
    {
        using var _ = previewContext.SuspendGuard();
        var row = await db.PreviewSessions.FirstOrDefaultAsync(p => p.Id == previewId, ct);
        if (row is null) return;
        await uow.ExecuteAsync(async c =>
        {
            row.CountRefusedWrite();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("preview.write_refused", "User", row.TargetUserId.ToString(),
                NewValuesJson: JsonSerializer.Serialize(new { previewId = row.Id, method, route = route.Length > 200 ? route[..200] : route })));
        }, ct);
    }
}
