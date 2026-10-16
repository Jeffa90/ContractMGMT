using Ecp.SharedKernel;

namespace Ecp.Approvals.Domain;

/// <summary>
/// An approver's own authority handed to another member of the same approver group (or its backup group) for a bounded period. It is never transitive: the delegate acts only for the person who delegated,
/// and the delegation is void when that person stops holding the authority. Every use is recorded as "on behalf of".
/// </summary>
public sealed class ApprovalDelegation : ITenantOwned
{
    private ApprovalDelegation() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string GroupKey { get; private set; } = "";
    public Guid FromUserId { get; private set; }
    public string FromName { get; private set; } = "";
    public Guid ToUserId { get; private set; }
    public string ToName { get; private set; } = "";
    public DateTime ValidFromUtc { get; private set; }
    public DateTime ValidToUtc { get; private set; }
    public string Reason { get; private set; } = "";
    public Guid CreatedByUserId { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public DateTime? RevokedUtc { get; private set; }
    public string? RevokedBy { get; private set; }
    public string? RevokeReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public const int MaxDays = 90;

    public static ApprovalDelegation Create(Guid tenantId, Guid organisationId, string groupKey, Guid from, string fromName, Guid to, string toName, DateTime validFromUtc, DateTime validToUtc, string reason, DateTime nowUtc)
    {
        if (from == to) throw new DomainException("delegation_self", "You cannot delegate to yourself.");
        Guard.Utc(validFromUtc, nameof(validFromUtc)); Guard.Utc(validToUtc, nameof(validToUtc));
        if (validToUtc <= validFromUtc) throw new DomainException("delegation_window", "The delegation must end after it starts.");
        if (validToUtc <= nowUtc) throw new DomainException("delegation_window", "The delegation has already ended.");
        if (validToUtc - validFromUtc > TimeSpan.FromDays(MaxDays)) throw new DomainException("delegation_window", $"A delegation lasts at most {MaxDays} days. Renew it when it ends.");
        return new ApprovalDelegation
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)), GroupKey = Guard.NotBlank(groupKey, "Approver group", 100),
            FromUserId = from, FromName = Guard.NotBlank(fromName, "Delegator", 200), ToUserId = to, ToName = Guard.NotBlank(toName, "Delegate", 200), ValidFromUtc = validFromUtc, ValidToUtc = validToUtc,
            Reason = Guard.NotBlank(reason, "Reason", 500), CreatedByUserId = from, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
    }

    public bool IsActiveAt(DateTime utc) => RevokedUtc is null && utc >= ValidFromUtc && utc < ValidToUtc;
    public bool Overlaps(DateTime fromUtc, DateTime toUtc) => RevokedUtc is null && ValidFromUtc < toUtc && fromUtc < ValidToUtc;

    public void Revoke(string by, string reason, DateTime nowUtc)
    {
        if (RevokedUtc is not null) throw new DomainException("delegation_revoked", "That delegation has already been ended.");
        RevokedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); RevokedBy = Guard.NotBlank(by, "Ended by", 200); RevokeReason = Guard.NotBlank(reason, "Reason", 500);
    }
}

/// <summary>Pure checks over a set of delegations: cycles (A to B to C back to A) and the edges that resolution may use.</summary>
public static class DelegationGraph
{
    public sealed record Edge(Guid From, Guid To);

    /// <summary>True when adding <paramref name="from"/> to <paramref name="to"/> would close a loop among the given edges (the edges must already be limited to the same group and overlapping period).</summary>
    public static bool WouldCreateCycle(IEnumerable<Edge> existing, Guid from, Guid to)
    {
        var edges = existing.ToList();
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(to);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node == from) return true;
            if (!seen.Add(node)) continue;
            foreach (var e in edges.Where(e => e.From == node)) stack.Push(e.To);
        }
        return false;
    }

    /// <summary>The edges that are part of a loop. Resolution ignores them (a loop can only come from inconsistent data) so that nobody gains authority from a circular arrangement.</summary>
    public static IReadOnlySet<Edge> EdgesInCycles(IEnumerable<Edge> edges)
    {
        var all = edges.ToList();
        var result = new HashSet<Edge>();
        foreach (var e in all)
        {
            // An edge is in a cycle if its target can reach its source.
            var others = all.Where(x => !ReferenceEquals(x, e));
            if (WouldCreateCycle(others, e.From, e.To)) result.Add(e);
        }
        return result;
    }
}
