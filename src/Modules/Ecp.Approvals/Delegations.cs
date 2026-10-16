using Ecp.Approvals.Domain;
using Ecp.Approvals.Persistence;
using Ecp.Audit;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Approvals;

internal sealed class ApprovalDelegations(
    ApprovalsDbContext db, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IClock clock, CurrentUser currentUser, IAccessService access, IAccessDirectory directory,
    IApprovalMatrixStore matrices) : IApprovalDelegations
{
    private Guid Me => currentUser.UserId ?? throw new ForbiddenException("A signed-in person is required.");

    private static DelegationInfo ToInfo(ApprovalDelegation d, DateTime now)
        => new(d.Id, d.OrganisationId, d.GroupKey, d.FromUserId, d.FromName, d.ToUserId, d.ToName, d.ValidFromUtc, d.ValidToUtc, d.Reason, d.CreatedUtc, d.RevokedUtc, d.RevokedBy, d.RevokeReason, d.IsActiveAt(now));

    /// <summary>The descriptor the access layer uses to ask "can this person act as an approver of this group in this organisation".</summary>
    private ResourceDescriptor Seat(Guid organisationId, params string?[] groups)
        => new("Contract", tenant.RequireTenantId(), organisationId, Classification: "Public", PendingApprovalGroupKeys: groups.Where(g => g is not null).Select(g => g!).ToHashSet(StringComparer.Ordinal), Dimensions: ScopeDimensions.OrganisationOnly);

    public async Task<Guid> CreateAsync(CreateDelegationRequest r, CancellationToken ct = default)
    {
        var me = Me;
        await access.RequireAsync("approvals.delegate", ApprovalResources.Matrix(tenant.RequireTenantId(), r.OrganisationId), ct);
        var group = Guard.NotBlank(r.GroupKey, "Approver group", 100);
        var now = clock.UtcNow;
        // Only your own authority can be delegated: you must hold the seat now.
        var mine = await directory.ApproversAsync(group, now, ct);
        if (mine.All(a => a.UserId != me)) throw new ForbiddenException("You do not hold an approver or backup approver assignment for that group, so you have nothing to delegate.");
        var seat = await access.CheckSilentAsync("approvals.action", Seat(r.OrganisationId, group), ct);
        if (!seat.Allowed) throw new ForbiddenException("Your assignment does not give you approval authority in that organisation.");

        // The delegate must be a member of the group or of a backup group that a current matrix step names for it.
        var allowedGroups = new HashSet<string>(StringComparer.Ordinal) { group };
        if (await matrices.TryLoadPublishedAsync(r.OrganisationId, ct) is { } loaded)
            foreach (var step in loaded.Set.Matrices.SelectMany(m => m.Rows).SelectMany(x => x.Steps).Where(s => string.Equals(s.ApproverGroup, group, StringComparison.Ordinal) && s.BackupGroup is not null))
                allowedGroups.Add(step.BackupGroup!);
        var members = new List<ApproverInfo>();
        foreach (var g in allowedGroups) members.AddRange(await directory.ApproversAsync(g, r.ValidFromUtc > now ? r.ValidFromUtc : now, ct));
        var to = members.FirstOrDefault(a => a.UserId == r.ToUserId) ?? throw new DomainException("delegate_not_eligible", "You can delegate only to a member of the same approver group or of its backup group.");
        var fromName = await directory.DisplayNameAsync(me, ct) ?? me.ToString();

        var created = ApprovalDelegation.Create(tenant.RequireTenantId(), r.OrganisationId, group, me, fromName, to.UserId, to.DisplayName, r.ValidFromUtc, r.ValidToUtc, r.Reason, now);
        // A loop (A to B, B to C, C to A) would let nobody be accountable: refuse any delegation that closes one among those overlapping the period.
        var overlapping = (await db.Delegations.AsNoTracking().Where(d => d.OrganisationId == r.OrganisationId && d.GroupKey == group && d.RevokedUtc == null && d.ValidToUtc > now).ToListAsync(ct))
            .Where(d => d.Overlaps(created.ValidFromUtc, created.ValidToUtc)).ToList();
        if (DelegationGraph.WouldCreateCycle(overlapping.Select(d => new DelegationGraph.Edge(d.FromUserId, d.ToUserId)), me, to.UserId))
            throw new DomainException("circular_delegation", "That would make a circle of delegations (for example A to B, B to C and C back to A) during the same period. Ask the person to end theirs first, or choose someone else.");
        if (overlapping.Any(d => d.FromUserId == me && d.ToUserId == to.UserId))
            throw new DomainException("delegation_exists", "You have already delegated to this person for part of that period.");

        await uow.ExecuteAsync(async c =>
        {
            db.Delegations.Add(created);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("approval.delegation.created", nameof(ApprovalDelegation), created.Id.ToString(), OrganisationId: r.OrganisationId, Reason: r.Reason,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { group, from = me, to = to.UserId, validFrom = created.ValidFromUtc, validTo = created.ValidToUtc })));
        }, ct);
        return created.Id;
    }

    public async Task RevokeAsync(Guid id, string reason, CancellationToken ct = default)
    {
        reason = Guard.NotBlank(reason, "Reason for ending the delegation", 500);       // every ending is explained, whoever does it
        var d = await db.Delegations.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new ResourceNotFoundException();
        var me = Me;
        if (d.FromUserId != me) await access.RequireAsync("approvals.delegate.administer", ApprovalResources.Matrix(tenant.RequireTenantId(), d.OrganisationId, id.ToString()), ct);
        var name = await directory.DisplayNameAsync(me, ct) ?? me.ToString();
        await uow.ExecuteAsync(async c =>
        {
            d.Revoke(name, reason, clock.UtcNow);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("approval.delegation.revoked", nameof(ApprovalDelegation), id.ToString(), OrganisationId: d.OrganisationId, Reason: reason,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { group = d.GroupKey, from = d.FromUserId, to = d.ToUserId, endedBy = me })));
        }, ct);
    }

    public async Task<IReadOnlyList<DelegationInfo>> MineAsync(CancellationToken ct = default)
    {
        var me = Me; var now = clock.UtcNow;
        var rows = await db.Delegations.AsNoTracking().Where(d => d.FromUserId == me || d.ToUserId == me).OrderByDescending(d => d.ValidToUtc).Take(200).ToListAsync(ct);
        return rows.Select(d => ToInfo(d, now)).ToList();
    }

    public async Task<PagedResult<DelegationInfo>> ListAsync(Guid organisationId, bool activeOnly, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        await access.RequireAsync("approvals.matrix.read", ApprovalResources.Matrix(tenant.RequireTenantId(), organisationId), ct);
        var now = clock.UtcNow;
        var query = db.Delegations.AsNoTracking().Where(d => d.OrganisationId == organisationId);
        if (activeOnly) query = query.Where(d => d.RevokedUtc == null && d.ValidToUtc > now);
        var total = await query.CountAsync(ct);
        var p = Math.Max(1, page); var size = Math.Clamp(pageSize, 1, 200);
        var rows = await query.OrderByDescending(d => d.CreatedUtc).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<DelegationInfo>(rows.Select(d => ToInfo(d, now)).ToList(), total, p, size);
    }

    public async Task<IReadOnlyList<DelegationInfo>> ActiveAsync(Guid organisationId, IReadOnlyCollection<string> groupKeys, DateTime asOfUtc, CancellationToken ct = default)
    {
        var groups = groupKeys.ToArray();
        var rows = await db.Delegations.AsNoTracking().Where(d => d.OrganisationId == organisationId && groups.Contains(d.GroupKey) && d.RevokedUtc == null && d.ValidFromUtc <= asOfUtc && d.ValidToUtc > asOfUtc).ToListAsync(ct);
        return rows.Select(d => ToInfo(d, asOfUtc)).ToList();
    }
}
