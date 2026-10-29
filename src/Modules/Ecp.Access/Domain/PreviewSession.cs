using Ecp.Persistence;
using Ecp.SharedKernel;

namespace Ecp.Access.Domain;

/// <summary>
/// One Test/Demo "Preview as this user" session: who looked, at whom, why, for how long, and how many writes were refused. The session cookie carries only this row's id; every request re-checks it
/// against the database, so ending it here ends it everywhere. It grants nothing: while it is open the request runs as the previewed person's real access, read-only.
/// </summary>
public sealed class PreviewSession : ITenantOwned
{
    private PreviewSession() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid PreviewerUserId { get; private set; }
    public Guid TargetUserId { get; private set; }
    /// <summary>Organisations of the person previewed when it started (JSON array), for the audit trail.</summary>
    public string OrganisationIdsJson { get; private set; } = "[]";
    public string Reason { get; private set; } = "";
    public DateTime StartedUtc { get; private set; }
    public DateTime ExpiresUtc { get; private set; }
    public DateTime? EndedUtc { get; private set; }
    public string? EndReason { get; private set; }
    public int RefusedWrites { get; private set; }
    public string CorrelationId { get; private set; } = "";

    public bool IsOpenAt(DateTime nowUtc) => EndedUtc is null && ExpiresUtc > nowUtc;

    public static PreviewSession Start(Guid tenantId, Guid previewer, Guid target, IEnumerable<Guid> organisations, string reason, DateTime nowUtc, TimeSpan lifetime, string correlationId)
        => new()
        {
            Id = Guid.NewGuid(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), PreviewerUserId = Guard.NotEmpty(previewer, nameof(previewer)), TargetUserId = Guard.NotEmpty(target, nameof(target)),
            OrganisationIdsJson = System.Text.Json.JsonSerializer.Serialize(organisations.Distinct().ToArray()), Reason = Guard.NotBlank(reason, "Reason", 1000),
            StartedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), ExpiresUtc = nowUtc + lifetime, CorrelationId = Guard.NotBlank(correlationId, nameof(correlationId), 100),
        };

    public void End(DateTime nowUtc, string reason)
    {
        if (EndedUtc is not null) return;
        EndedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); EndReason = Guard.NotBlank(reason, nameof(reason), 100);
    }

    public void CountRefusedWrite() => RefusedWrites++;
}
