using System.Text.Json;
using Ecp.Audit;
using Ecp.Identity.Access;
using Ecp.Identity.Domain;
using Ecp.Identity.Persistence;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Identity;

/// <summary>One place an approver group is relied on: a published approval matrix, a workflow that names it, or an approval that is waiting for it.</summary>
public sealed record GroupUsageLine(string Kind, string Where, int Count);

/// <summary>Lets modules that know where a group is named (approval matrices, workflows, approvals in flight) say so, without the Identity module depending on them.</summary>
public interface IApproverGroupUsageSource
{
    Task<IReadOnlyList<GroupUsageLine>> UsageAsync(string groupKey, CancellationToken ct = default);
}

public sealed record ApproverGroupSummary(string Key, string Description, Guid? OrganisationId, bool IsActive, bool AllowDelegation, bool Registered, int Approvers, int Backups, int InUse, DateTime? CreatedUtc);

public sealed record GroupMember(Guid AssignmentId, Guid UserId, string DisplayName, bool IsBackup, Guid? OrganisationId, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, bool IsActive);

public sealed record ApproverGroupDetail(ApproverGroupSummary Summary, IReadOnlyList<GroupMember> Members, IReadOnlyList<GroupUsageLine> Usage);

/// <summary>A change to a group's membership that has not been made yet: add a person (primary or backup) or end an assignment.</summary>
public sealed record MembershipChange(bool Add, Guid? UserId, bool IsBackup, Guid? AssignmentId, DateTime? EffectiveFromUtc = null, DateTime? EffectiveToUtc = null);

/// <summary>What a membership change would do, shown before it is made: who can approve now, who would afterwards, what relies on the group, and whether the change is refused.</summary>
public sealed record MembershipEffect(IReadOnlyList<string> ApproversBefore, IReadOnlyList<string> ApproversAfter, bool LeavesGroupEmpty, IReadOnlyList<GroupUsageLine> RelyingOnTheGroup, string? Refusal);

/// <summary>
/// Administration of approver groups in forms: register a group, say whether its members may delegate, add and end members (primary and backup, with effective dates), see what relies on the group, and be stopped from emptying
/// or retiring a group that something still relies on. Authorised by <c>users.manage</c> for the group's organisation (the people granted are subject to the same delegation rules as any role grant). The key is permanent;
/// the name people read is the group's display name in terminology.
/// </summary>
public interface IApproverGroupService
{
    Task<IReadOnlyList<ApproverGroupSummary>> ListAsync(CancellationToken ct = default);
    Task<ApproverGroupDetail> GetAsync(string key, CancellationToken ct = default);
    Task<ApproverGroupSummary> CreateAsync(string key, string? description, Guid? organisationId, bool allowDelegation, CancellationToken ct = default);
    Task UpdateAsync(string key, string? description, bool allowDelegation, CancellationToken ct = default);
    /// <summary>Retires a group (it can be given no new members and is not offered for new matrices). Refused while anything relies on it or it has active members.</summary>
    Task RetireAsync(string key, string reason, CancellationToken ct = default);
    Task ReactivateAsync(string key, CancellationToken ct = default);
    Task<MembershipEffect> PreviewAsync(string key, MembershipChange change, CancellationToken ct = default);
    /// <summary>Adds a person to the group in an organisation. Their role is an Approver or Backup Approver assignment carrying the group's key.</summary>
    Task<Guid> AddMemberAsync(string key, Guid userId, bool isBackup, Guid organisationId, string classificationCeiling, DateTime? fromUtc, DateTime? toUtc, string? reason, CancellationToken ct = default);
    Task EndMemberAsync(string key, Guid assignmentId, string reason, CancellationToken ct = default);
}

internal sealed class ApproverGroupService(
    IdentityDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, IAccessService access, CurrentUser currentUser,
    IRoleAssignmentService roles, IEnumerable<IApproverGroupUsageSource> usageSources) : IApproverGroupService
{
    private static bool IsApproverRole(string role) => role is RoleCodes.Approver or RoleCodes.BackupApprover;

    /// <summary>Whether the caller manages people for this organisation (null = any organisation, which only a caller covering every organisation satisfies).</summary>
    private async Task<IReadOnlyList<AccessGrant>> ManagerGrantsAsync(CancellationToken ct)
    {
        var grants = await access.GrantsAsync("users.manage", ct);
        if (grants.Count == 0) throw new ForbiddenException("Managing approver groups needs permission to manage users.");
        return grants;
    }

    private static bool Covers(IReadOnlyList<AccessGrant> grants, Guid? organisationId)
        => grants.Any(g => g.OrganisationIds is null || (organisationId is { } o && g.OrganisationIds.Contains(o)));

    private async Task<string> ActorAsync(CancellationToken ct)
        => currentUser.UserId is { } id ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? id.ToString() : "system";

    private async Task<List<(RoleAssignment A, string Name, UserStatus Status)>> MembersRawAsync(string key, CancellationToken ct)
    {
        var rows = await (from a in db.RoleAssignments.AsNoTracking()
                          join u in db.Users.AsNoTracking() on new { a.TenantId, Id = a.UserId } equals new { u.TenantId, u.Id }
                          where a.ApproverGroupKey == key && (a.RoleCode == RoleCodes.Approver || a.RoleCode == RoleCodes.BackupApprover)
                          select new { a, u.DisplayName, u.Status }).ToListAsync(ct);
        return rows.Select(r => (r.a, r.DisplayName, r.Status)).ToList();
    }

    private bool ActiveNow(RoleAssignment a, UserStatus status) => status == UserStatus.Active && a.IsActiveAt(clock.UtcNow);

    private async Task<IReadOnlyList<GroupUsageLine>> UsageAsync(string key, CancellationToken ct)
    {
        var lines = new List<GroupUsageLine>();
        foreach (var source in usageSources) lines.AddRange(await source.UsageAsync(key, ct));
        return lines.Where(l => l.Count > 0).ToList();
    }

    public async Task<IReadOnlyList<ApproverGroupSummary>> ListAsync(CancellationToken ct = default)
    {
        var grants = await ManagerGrantsAsync(ct);
        var registered = await db.ApproverGroups.AsNoTracking().OrderBy(g => g.Key).ToListAsync(ct);
        var assignments = await (from a in db.RoleAssignments.AsNoTracking()
                                 join u in db.Users.AsNoTracking() on new { a.TenantId, Id = a.UserId } equals new { u.TenantId, u.Id }
                                 where a.ApproverGroupKey != null && (a.RoleCode == RoleCodes.Approver || a.RoleCode == RoleCodes.BackupApprover)
                                 select new { a, u.Status }).ToListAsync(ct);
        var keys = registered.Select(g => g.Key).Concat(assignments.Select(x => x.a.ApproverGroupKey!)).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var result = new List<ApproverGroupSummary>();
        foreach (var key in keys)
        {
            var group = registered.FirstOrDefault(g => g.Key == key);
            var mine = assignments.Where(x => x.a.ApproverGroupKey == key).ToList();
            // A group is visible to a manager of its organisation; an unregistered key, to a manager of any organisation its members are assigned in.
            var visible = group is not null ? group.OrganisationId is null || Covers(grants, group.OrganisationId) : mine.Any(x => x.a.OrganisationIds.Any(o => Covers(grants, o)));
            if (!visible) continue;
            var active = mine.Where(x => ActiveNow(x.a, x.Status)).ToList();
            var inUse = (await UsageAsync(key, ct)).Sum(l => l.Count);
            result.Add(new ApproverGroupSummary(key, group?.Description ?? "", group?.OrganisationId, group?.IsActive ?? true, group?.AllowDelegation ?? true, group is not null,
                active.Count(x => x.a.RoleCode == RoleCodes.Approver), active.Count(x => x.a.RoleCode == RoleCodes.BackupApprover), inUse, group?.CreatedUtc));
        }
        return result;
    }

    public async Task<ApproverGroupDetail> GetAsync(string key, CancellationToken ct = default)
    {
        var k = ApproverGroup.NormaliseKey(key);
        var summary = (await ListAsync(ct)).FirstOrDefault(s => s.Key == k) ?? throw new ResourceNotFoundException();
        var members = (await MembersRawAsync(k, ct)).OrderByDescending(m => ActiveNow(m.A, m.Status)).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.A.EffectiveFromUtc)
            .Select(m => new GroupMember(m.A.Id, m.A.UserId, m.Name, m.A.RoleCode == RoleCodes.BackupApprover, m.A.OrganisationIds.Count == 1 ? m.A.OrganisationIds[0] : null, m.A.EffectiveFromUtc, m.A.EffectiveToUtc, ActiveNow(m.A, m.Status))).ToList();
        return new ApproverGroupDetail(summary, members, await UsageAsync(k, ct));
    }

    public async Task<ApproverGroupSummary> CreateAsync(string key, string? description, Guid? organisationId, bool allowDelegation, CancellationToken ct = default)
    {
        var grants = await ManagerGrantsAsync(ct);
        if (!Covers(grants, organisationId) && !(organisationId is null && grants.Any(g => g.OrganisationIds is null)))
            throw new ForbiddenException("You do not manage people for that organisation.");
        var k = ApproverGroup.NormaliseKey(key);
        var actor = await ActorAsync(ct);
        var group = ApproverGroup.Create(tenant.RequireTenantId(), k, description, organisationId, allowDelegation, actor, clock.UtcNow);
        await uow.ExecuteAsync(async c =>
        {
            db.ApproverGroups.Add(group);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("group_exists", "An approver group with that key is already registered."); }
            audit.Record(new AuditEntry("approver_group.created", nameof(ApproverGroup), group.Id.ToString(), OrganisationId: organisationId,
                NewValuesJson: JsonSerializer.Serialize(new { key = k, allowDelegation, organisationId })));
        }, ct);
        return (await ListAsync(ct)).First(s => s.Key == k);
    }

    private async Task<(ApproverGroup Group, IReadOnlyList<AccessGrant> Grants)> LoadForChangeAsync(string key, CancellationToken ct)
    {
        var grants = await ManagerGrantsAsync(ct);
        var k = ApproverGroup.NormaliseKey(key);
        var group = await db.ApproverGroups.FirstOrDefaultAsync(g => g.Key == k, ct) ?? throw new DomainException("group_not_registered", "That approver group is not registered yet. Register it first.");
        if (group.OrganisationId is not null && !Covers(grants, group.OrganisationId)) throw new ResourceNotFoundException();
        if (group.OrganisationId is null && !grants.Any(g => g.OrganisationIds is null)) throw new ForbiddenException("That group serves several organisations, so only someone who manages people everywhere can change it.");
        return (group, grants);
    }

    public async Task UpdateAsync(string key, string? description, bool allowDelegation, CancellationToken ct = default)
    {
        var (group, _) = await LoadForChangeAsync(key, ct);
        await uow.ExecuteAsync(async c =>
        {
            group.Update(description, allowDelegation);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("approver_group.updated", nameof(ApproverGroup), group.Id.ToString(), OrganisationId: group.OrganisationId, NewValuesJson: JsonSerializer.Serialize(new { key = group.Key, allowDelegation })));
        }, ct);
    }

    public async Task RetireAsync(string key, string reason, CancellationToken ct = default)
    {
        var (group, _) = await LoadForChangeAsync(key, ct);
        var usage = await UsageAsync(group.Key, ct);
        if (usage.Count > 0) throw new DomainException("group_in_use", "This group cannot be retired while it is relied on: " + Describe(usage) + ". Change those first.");
        var active = (await MembersRawAsync(group.Key, ct)).Count(m => ActiveNow(m.A, m.Status));
        if (active > 0) throw new DomainException("group_has_members", $"This group still has {active} active member(s). End their membership first.");
        await uow.ExecuteAsync(async c =>
        {
            group.Retire(reason, clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("approver_group.retired", nameof(ApproverGroup), group.Id.ToString(), OrganisationId: group.OrganisationId, Reason: reason));
        }, ct);
    }

    public async Task ReactivateAsync(string key, CancellationToken ct = default)
    {
        var (group, _) = await LoadForChangeAsync(key, ct);
        await uow.ExecuteAsync(async c =>
        {
            group.Reactivate();
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("approver_group.reactivated", nameof(ApproverGroup), group.Id.ToString(), OrganisationId: group.OrganisationId));
        }, ct);
    }

    private static string Describe(IEnumerable<GroupUsageLine> usage) => string.Join("; ", usage.Select(u => $"{u.Where} ({u.Count})"));

    public async Task<MembershipEffect> PreviewAsync(string key, MembershipChange change, CancellationToken ct = default)
    {
        var grants = await ManagerGrantsAsync(ct);
        var k = ApproverGroup.NormaliseKey(key);
        var members = await MembersRawAsync(k, ct);
        var now = clock.UtcNow;
        var before = members.Where(m => ActiveNow(m.A, m.Status)).Select(m => (m.A.UserId, m.Name)).DistinctBy(x => x.UserId).ToList();
        var after = before.ToList();
        string? refusal = null;
        var usage = await UsageAsync(k, ct);
        if (change.Add)
        {
            var user = change.UserId is { } uid ? await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => new { u.Id, u.DisplayName, u.Status }).FirstOrDefaultAsync(ct) : null;
            if (user is null) refusal = "Choose a person.";
            else if (user.Status != UserStatus.Active) refusal = $"{user.DisplayName} cannot sign in, so cannot approve.";
            else if (change.EffectiveFromUtc is { } f && f > now) { /* starts later: not an approver today */ }
            else if (after.All(x => x.UserId != user.Id)) after.Add((user.Id, user.DisplayName));
        }
        else
        {
            var target = members.FirstOrDefault(m => m.A.Id == change.AssignmentId);
            if (target.A is null) refusal = "That membership was not found in this group.";
            else
            {
                var others = members.Any(m => m.A.Id != target.A.Id && m.A.UserId == target.A.UserId && ActiveNow(m.A, m.Status));
                if (!others) after.RemoveAll(x => x.UserId == target.A.UserId);
            }
        }
        var empty = after.Count == 0;
        if (refusal is null && empty && before.Count > 0 && usage.Count > 0)
            refusal = "That would leave the group with nobody to approve while it is relied on: " + Describe(usage) + ". Add someone else first.";
        _ = grants;
        return new MembershipEffect(before.Select(x => x.Name).ToList(), after.Select(x => x.Name).ToList(), empty, usage, refusal);
    }

    public async Task<Guid> AddMemberAsync(string key, Guid userId, bool isBackup, Guid organisationId, string classificationCeiling, DateTime? fromUtc, DateTime? toUtc, string? reason, CancellationToken ct = default)
    {
        var (group, grants) = await LoadForChangeAsync(key, ct);
        if (!group.IsActive) throw new DomainException("group_retired", "This group is retired. Reactivate it before adding people.");
        if (group.OrganisationId is { } own && own != organisationId) throw new DomainException("wrong_organisation", "This group belongs to another organisation.");
        if (!Covers(grants, organisationId)) throw new ForbiddenException("You do not manage people for that organisation.");
        var role = isBackup ? RoleCodes.BackupApprover : RoleCodes.Approver;
        var existing = (await MembersRawAsync(group.Key, ct)).Any(m => m.A.UserId == userId && m.A.RoleCode == role && m.A.OrganisationIds.Contains(organisationId) && (m.A.EffectiveToUtc is null || m.A.EffectiveToUtc > clock.UtcNow));
        if (existing) throw new DomainException("already_member", "That person already has this membership in the group.");
        var id = await roles.GrantAsync(new GrantRequest(userId, role, null, ScopeSet.ForOrganisations([organisationId], classificationCeiling), group.Key, fromUtc, toUtc, reason), ct);
        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry("approver_group.member_added", nameof(ApproverGroup), group.Id.ToString(), OrganisationId: organisationId, Reason: reason,
                NewValuesJson: JsonSerializer.Serialize(new { key = group.Key, user = userId, backup = isBackup, assignment = id, from = fromUtc, to = toUtc })));
            return Task.CompletedTask;
        }, ct);
        return id;
    }

    public async Task EndMemberAsync(string key, Guid assignmentId, string reason, CancellationToken ct = default)
    {
        var (group, _) = await LoadForChangeAsync(key, ct);
        Guard.NotBlank(reason, "Reason", 1000);
        var members = await MembersRawAsync(group.Key, ct);
        var target = members.FirstOrDefault(m => m.A.Id == assignmentId);
        if (target.A is null) throw new ResourceNotFoundException();
        var effect = await PreviewAsync(group.Key, new MembershipChange(false, null, false, assignmentId), ct);
        if (effect.Refusal is not null) throw new DomainException("group_in_use", effect.Refusal);
        await roles.RevokeAsync(assignmentId, reason, ct);
        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry("approver_group.member_ended", nameof(ApproverGroup), group.Id.ToString(), OrganisationId: target.A.OrganisationIds.Count == 1 ? target.A.OrganisationIds[0] : null, Reason: reason,
                NewValuesJson: JsonSerializer.Serialize(new { key = group.Key, user = target.A.UserId, assignment = assignmentId })));
            return Task.CompletedTask;
        }, ct);
    }
}
