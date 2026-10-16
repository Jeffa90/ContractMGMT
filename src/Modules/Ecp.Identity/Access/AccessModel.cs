using Ecp.Identity.Domain;
using Ecp.SharedKernel;

namespace Ecp.Identity.Access;

/// <summary>A role assignment as the evaluator sees it (no persistence concerns).</summary>
public sealed record AssignmentView(
    Guid Id, string RoleCode, ViewerKind? ViewerKind, ScopeSet Scope, string? ApproverGroupKey, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, bool PrivilegedAccess = false)
{
    public bool IsActiveAt(DateTime utc) => EffectiveFromUtc <= utc && (EffectiveToUtc is null || utc < EffectiveToUtc);
}

/// <summary>
/// The authenticated person and their assignments. During a support or emergency elevation (<see cref="ElevationId"/> set) the subject carries only the permissions the elevation allows
/// (<see cref="PermissionAllowlist"/>); every decision for such a subject is bounded by that list on top of the usual scope intersection.
/// </summary>
public sealed record AccessSubject(Guid UserId, Guid TenantId, UserStatus Status, IReadOnlyList<AssignmentView> Assignments,
    Guid? ElevationId = null, IReadOnlySet<string>? PermissionAllowlist = null)
{
    public bool PermitsPermission(string permission) => PermissionAllowlist is null || PermissionAllowlist.Contains(permission);
}

/// <summary>Which scope dimensions apply to a resource type. A dimension that does not apply to a resource never narrows access to it.</summary>
[Flags]
public enum ScopeDimensions
{
    None = 0,
    Organisation = 1,
    BusinessUnit = 2,
    Division = 4,
    Site = 8,
    Category = 16,
    Classification = 32,
    /// <summary>Contracts, documents and anything classified by the same attributes as a contract.</summary>
    Contract = Organisation | BusinessUnit | Division | Site | Category | Classification,
    /// <summary>Records scoped only by organisation (suppliers, categories, users).</summary>
    OrganisationOnly = Organisation,
}

/// <summary>
/// What the evaluator needs to know about the thing being accessed. Built by the owning module from the stored record (never from request
/// input). Relationship facts (active category owners, assigned approvers) are supplied by the module that owns them.
/// </summary>
public sealed record ResourceDescriptor(
    string Type,
    Guid TenantId,
    Guid? OrganisationId = null,
    Guid? BusinessUnitId = null,
    Guid? DivisionId = null,
    Guid? SiteId = null,
    Guid? CategoryId = null,
    IReadOnlyList<Guid>? CategoryAncestry = null,
    string? Classification = null,
    bool IsLocked = false,
    IReadOnlySet<Guid>? ActiveCategoryOwnerIds = null,
    IReadOnlySet<string>? PendingApprovalGroupKeys = null,
    Guid? SubmittedByUserId = null,
    bool IsTenantLevelDefinition = false,
    bool EmergencyUnlockAuthorised = false,
    ScopeDimensions Dimensions = ScopeDimensions.Contract,
    string? Id = null)
{
    public IReadOnlyList<Guid> CategoryPath => CategoryAncestry ?? (CategoryId is { } c ? [c] : []);
}

public enum DenyKind { None, NotFound, Forbidden }

/// <summary>The outcome of an access check. <see cref="Layer"/> is the evaluation layer (1 to 8) that decided a denial.</summary>
public sealed record AccessDecision(bool Allowed, DenyKind Deny, int Layer, string Reason, Guid? AssignmentId = null, string? RoleCode = null)
{
    public static AccessDecision Allow(AssignmentView by) => new(true, DenyKind.None, 0, "Allowed", by.Id, by.RoleCode);
    public static AccessDecision NotFound(int layer, string reason) => new(false, DenyKind.NotFound, layer, reason);
    public static AccessDecision Forbidden(int layer, string reason) => new(false, DenyKind.Forbidden, layer, reason);
}

/// <summary>
/// An assignment's authority for one permission, expressed for list queries: the owning module turns it into a predicate over its own
/// records. Several grants are alternatives (OR); scopes of different assignments are never merged.
/// </summary>
public sealed record AccessGrant(
    Guid AssignmentId, string RoleCode,
    IReadOnlyList<Guid>? OrganisationIds,       // null = every organisation in the tenant
    IReadOnlyList<Guid> BusinessUnitIds, IReadOnlyList<Guid> DivisionIds, IReadOnlyList<Guid> SiteIds,
    IReadOnlyList<Guid>? CategoryIds,           // null = all categories (or category scope ignored for this permission)
    IReadOnlyList<string> AllowedClassifications,
    GrantRelationship Relationship,
    string? ApproverGroupKey = null);

/// <summary>Extra condition a grant carries for list queries.</summary>
public enum GrantRelationship
{
    /// <summary>Scopes alone decide.</summary>
    None,
    /// <summary>Only records in categories the user is currently assigned (edit-type permissions of Category Owner).</summary>
    ActiveCategoryOwnership,
    /// <summary>Only records with a pending approval for the assignment's approver group. The owning module supplies which records those are; with none pending, nothing is listed.</summary>
    AssignedApproval,
    /// <summary>Granted only where organisation policy has authorised it; not listable.</summary>
    PolicyAuthorised,
    /// <summary>Only tenant-level definitions (for example tenant-wide configuration defaults).</summary>
    TenantLevelDefinition,
}
