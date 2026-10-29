using Ecp.Persistence;
using Ecp.SharedKernel;

namespace Ecp.Access.Domain;

/// <summary>
/// A standing instruction: "this person owns every current and future category of this organisation". It is a small additive policy record, not an authorisation rule. Category authorisation still reads
/// ordinary ownership assignments; this record is what keeps them in step. When the policy is set the categories that exist get an ownership assignment, and each category created afterwards (by any route)
/// gets one in the same transaction as the category. The record lists the assignments it created, so ending the policy ends exactly those and never touches ownership that someone else gave explicitly.
/// Deleting every row would stop future categories being recognised and change nobody's existing permissions.
/// </summary>
public sealed class CategoryOwnershipPolicy : ITenantOwned
{
    private CategoryOwnershipPolicy() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid UserId { get; private set; }
    /// <summary>Whether the person takes the primary place where a category has no primary owner (otherwise, or where one exists, a backup place).</summary>
    public bool Primary { get; private set; }
    /// <summary>How it came about: <c>Guided</c>, <c>Setup</c>, <c>TestBootstrap</c> or <c>OwnershipScreen</c>.</summary>
    public string Origin { get; private set; } = "Guided";
    /// <summary>The profile application that created it, so ending that application ends this policy too.</summary>
    public Guid? ApplicationId { get; private set; }
    public string CreatedByLabel { get; private set; } = "";
    public Guid? CreatedByUserId { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public string? Reason { get; private set; }
    /// <summary>Ids of the ownership assignments the policy created (JSON array of GUIDs): today's categories when it was set, then each new category.</summary>
    public string AssignmentIdsJson { get; private set; } = "[]";
    public DateTime? EndedUtc { get; private set; }
    public string? EndedBy { get; private set; }
    public string? EndReason { get; private set; }

    public bool IsOpen => EndedUtc is null;
    public IReadOnlyList<Guid> AssignmentIds => System.Text.Json.JsonSerializer.Deserialize<Guid[]>(AssignmentIdsJson) ?? [];

    public static CategoryOwnershipPolicy Create(Guid tenantId, Guid organisationId, Guid userId, bool primary, string origin, Guid? applicationId, string createdByLabel, Guid? createdByUserId, DateTime nowUtc, string? reason)
        => new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, OrganisationId = organisationId, UserId = userId, Primary = primary, Origin = Guard.NotBlank(origin, nameof(origin), 20),
            ApplicationId = applicationId, CreatedByLabel = Guard.NotBlank(createdByLabel, nameof(createdByLabel), 200), CreatedByUserId = createdByUserId, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc)),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : Guard.NotBlank(reason, nameof(reason), 500),
        };

    public void Record(IEnumerable<Guid> assignmentIds)
        => AssignmentIdsJson = System.Text.Json.JsonSerializer.Serialize(AssignmentIds.Concat(assignmentIds).Distinct().ToArray());

    public void Link(Guid applicationId) => ApplicationId = applicationId;

    public void End(string by, string reason, DateTime nowUtc)
    {
        if (EndedUtc is not null) return;
        EndedUtc = Guard.Utc(nowUtc, nameof(nowUtc)); EndedBy = Guard.NotBlank(by, nameof(by), 200); EndReason = Guard.NotBlank(reason, nameof(reason), 500);
    }
}
