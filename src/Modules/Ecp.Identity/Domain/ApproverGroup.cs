using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Identity.Domain;

/// <summary>
/// An approver group the organisation has registered: a stable key that approval matrices and workflows name, a description, whether its members may delegate, and whether it is in use. Who is in the group is not stored here:
/// membership is the approver and backup-approver role assignments that carry the group's key (so effective dates, scope and last-administrator rules stay in one place). A key used by assignments but not registered here is
/// shown as unregistered, never hidden.
/// </summary>
public sealed class ApproverGroup : ITenantOwned
{
    private static readonly Regex KeyPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);

    private ApproverGroup() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    /// <summary>The stable key, as approval matrices and workflows name it. Never renamed.</summary>
    public string Key { get; private set; } = "";
    public string Description { get; private set; } = "";
    /// <summary>The organisation this group belongs to, or null when the same group serves several organisations.</summary>
    public Guid? OrganisationId { get; private set; }
    public bool IsActive { get; private set; }
    /// <summary>Whether members may delegate their approvals to someone else (a delegation is still limited by the matrix and the delegation rules).</summary>
    public bool AllowDelegation { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public string CreatedBy { get; private set; } = "";
    public DateTime? RetiredUtc { get; private set; }
    public string? RetiredReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static string NormaliseKey(string? key)
    {
        var k = Guard.NotBlank(key, "Group key", 100).Trim();
        if (!KeyPattern.IsMatch(k)) throw new DomainException("invalid_group_key", "A group key starts with a letter or digit and then uses letters, digits, full stops, hyphens and underscores only (up to 100 characters). It cannot be renamed later.");
        return k;
    }

    public static ApproverGroup Create(Guid tenantId, string key, string? description, Guid? organisationId, bool allowDelegation, string createdBy, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), Key = NormaliseKey(key), Description = Guard.OptionalBounded(description, "Description", 300) ?? "",
        OrganisationId = organisationId, IsActive = true, AllowDelegation = allowDelegation, CreatedBy = Guard.NotBlank(createdBy, "Created by", 200), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
    };

    public void Update(string? description, bool allowDelegation) { Description = Guard.OptionalBounded(description, "Description", 300) ?? ""; AllowDelegation = allowDelegation; }

    public void Retire(string reason, DateTime nowUtc) { IsActive = false; RetiredUtc = Guard.Utc(nowUtc, nameof(nowUtc)); RetiredReason = Guard.NotBlank(reason, "Reason", 500); }
    public void Reactivate() { IsActive = true; RetiredUtc = null; RetiredReason = null; }
}
