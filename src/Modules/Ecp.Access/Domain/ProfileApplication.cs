using Ecp.Persistence;
using Ecp.SharedKernel;

namespace Ecp.Access.Domain;

/// <summary>
/// Display provenance: which guided action produced which grants. It records that "Jeff was given the Contract Manager profile on this date by this person" and lists the ids of the role grants and
/// category-ownership records that action created. Authority never comes from this row: the access evaluator reads the real grants and ownerships only, so deleting every row changes nobody's permissions.
/// </summary>
public sealed class ProfileApplication : ITenantOwned
{
    private ProfileApplication() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid UserId { get; private set; }
    public string ProfileKey { get; private set; } = "";
    public int ProfileVersion { get; private set; }
    /// <summary>How it came about: <c>Guided</c> (the People &amp; Access wizard), <c>Setup</c> (the first-administrator step) or <c>TestBootstrap</c>.</summary>
    public string Origin { get; private set; } = "Guided";
    public string AppliedByLabel { get; private set; } = "";
    public Guid? AppliedByUserId { get; private set; }
    public DateTime AppliedUtc { get; private set; }
    /// <summary>Ids of the role assignments this application created (JSON array of GUIDs).</summary>
    public string GrantIdsJson { get; private set; } = "[]";
    /// <summary>Ids of the category-ownership assignments it created (JSON array of GUIDs).</summary>
    public string OwnershipIdsJson { get; private set; } = "[]";
    public string Summary { get; private set; } = "";
    public string? Reason { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public DateTime? EndedUtc { get; private set; }
    public string? EndedBy { get; private set; }

    public bool IsOpen => EndedUtc is null;

    public static ProfileApplication Record(Guid tenantId, Guid organisationId, Guid userId, string profileKey, int version, string origin, string appliedByLabel, Guid? appliedByUserId,
        DateTime nowUtc, IEnumerable<Guid> grantIds, IEnumerable<Guid> ownershipIds, string summary, string? reason, string correlationId)
        => new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, OrganisationId = organisationId, UserId = userId,
            ProfileKey = Guard.NotBlank(profileKey, nameof(profileKey), 60), ProfileVersion = version, Origin = Guard.NotBlank(origin, nameof(origin), 20),
            AppliedByLabel = Guard.NotBlank(appliedByLabel, nameof(appliedByLabel), 200), AppliedByUserId = appliedByUserId, AppliedUtc = Guard.Utc(nowUtc, nameof(nowUtc)),
            GrantIdsJson = System.Text.Json.JsonSerializer.Serialize(grantIds.ToArray()), OwnershipIdsJson = System.Text.Json.JsonSerializer.Serialize(ownershipIds.ToArray()),
            Summary = Guard.NotBlank(summary, nameof(summary), 300), Reason = string.IsNullOrWhiteSpace(reason) ? null : Guard.NotBlank(reason, nameof(reason), 500),
            CorrelationId = Guard.NotBlank(correlationId, nameof(correlationId), 100),
        };

    public IReadOnlyList<Guid> GrantIds => System.Text.Json.JsonSerializer.Deserialize<Guid[]>(GrantIdsJson) ?? [];
    public IReadOnlyList<Guid> OwnershipIds => System.Text.Json.JsonSerializer.Deserialize<Guid[]>(OwnershipIdsJson) ?? [];

    public void End(string by, DateTime nowUtc)
    {
        if (EndedUtc is not null) return;
        EndedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); EndedBy = Guard.NotBlank(by, nameof(by), 200);
    }
}
