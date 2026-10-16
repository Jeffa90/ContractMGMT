using Ecp.Identity.Domain;
using Ecp.SharedKernel;

namespace Ecp.Identity.Access;

/// <summary>
/// Pure, server-side access decision. Layers follow ACCESS_CONTROL_SPEC section 3: authentication, tenant, scope intersection, role permission
/// (from the confirmed matrix), relationship, classification, lifecycle and segregation of duties. No I/O, so every rule is unit-testable.
/// </summary>
public sealed class AccessEvaluator(PermissionMatrix matrix)
{
    /// <summary>Read permissions for which the Category Owner's category scope does not narrow (ACCESS_CONTROL_SPEC 2.2).</summary>
    private static readonly HashSet<string> ReadAllPermissions = ["contracts.read", "documents.download", "suppliers.read"];
    private static readonly HashSet<string> LockSensitive = ["contracts.edit_owned", "documents.upload"];
    private static readonly HashSet<string> SegregatedApprovals = ["approvals.action", "disposition.approve"];

    public static AccessEvaluator Default { get; } = new(PermissionMatrix.Instance);

    public PermissionMatrix Matrix => matrix;

    /// <summary>The relationship rule attached to a role's scoped (S) grants. Unknown combinations are denied.</summary>
    public static GrantRelationship RelationshipOf(string role) => role switch
    {
        RoleCodes.CategoryOwner => GrantRelationship.ActiveCategoryOwnership,
        RoleCodes.Approver or RoleCodes.BackupApprover => GrantRelationship.AssignedApproval,
        RoleCodes.Viewer or RoleCodes.OrganisationAdministrator or RoleCodes.DivisionSiteAdministrator => GrantRelationship.None,
        RoleCodes.TenantAdministrator => GrantRelationship.TenantLevelDefinition,
        _ => GrantRelationship.PolicyAuthorised
    };

    public AccessDecision Evaluate(AccessSubject subject, ClassificationScale scale, DateTime nowUtc, string permission, ResourceDescriptor resource)
    {
        if (!matrix.IsKnown(permission)) throw new ArgumentException($"Unknown permission '{permission}'.", nameof(permission));

        // 1 Authentication / account state
        if (subject.Status != UserStatus.Active)
            return AccessDecision.Forbidden(1, "The account is not active.");

        // 2 Tenant: another tenant's records do not exist for this caller
        if (resource.TenantId != subject.TenantId)
            return AccessDecision.NotFound(2, "The resource is outside the caller's tenant.");

        var active = subject.Assignments.Where(a => a.IsActiveAt(nowUtc)).ToList();
        if (active.Count == 0)
            return AccessDecision.NotFound(3, "The caller has no active role assignment.");

        // 3 and 6: scope intersection and classification decide whether the record exists for the caller at all
        var covering = new List<AssignmentView>();
        var sawAboveCeiling = false;
        foreach (var a in active)
        {
            switch (Cover(a, resource, permission, scale))
            {
                case Coverage.Covered: covering.Add(a); break;
                case Coverage.AboveCeiling: sawAboveCeiling = true; break;
            }
        }
        if (covering.Count == 0)
            return AccessDecision.NotFound(sawAboveCeiling ? 6 : 3, sawAboveCeiling ? "The record is above the caller's classification ceiling." : "The record is outside the caller's assigned scopes.");

        // 4 to 8 are evaluated per assignment; scopes of different assignments are never merged
        AccessDecision? deepest = null;
        foreach (var a in covering)
        {
            var d = EvaluateAssignment(a, subject, permission, resource);
            if (d.Allowed) return d;
            if (deepest is null || d.Layer > deepest.Layer) deepest = d;
        }
        return deepest!;
    }

    private AccessDecision EvaluateAssignment(AssignmentView a, AccessSubject subject, string permission, ResourceDescriptor resource)
    {
        // 4 Permission
        var grant = matrix.Of(a.RoleCode, permission);
        if (grant == Grant.None) return AccessDecision.Forbidden(4, $"The {a.RoleCode} role does not hold {permission}.");

        // 5 Relationship (scoped grants)
        if (grant == Grant.Scoped && !RelationshipHolds(RelationshipOf(a.RoleCode), a, subject, resource))
            return AccessDecision.Forbidden(5, $"{permission} requires a relationship to the record that the caller does not have.");

        // 7 Lifecycle and lock
        if (LockSensitive.Contains(permission) && resource.IsLocked)
            return AccessDecision.Forbidden(7, "The record is locked.");

        // 8 Segregation of duties
        if (SegregatedApprovals.Contains(permission) && resource.SubmittedByUserId == subject.UserId)
            return AccessDecision.Forbidden(8, "A submitter cannot approve their own submission.");

        return AccessDecision.Allow(a);
    }

    private static bool RelationshipHolds(GrantRelationship rule, AssignmentView a, AccessSubject subject, ResourceDescriptor resource) => rule switch
    {
        GrantRelationship.None => true,
        GrantRelationship.ActiveCategoryOwnership => resource.ActiveCategoryOwnerIds?.Contains(subject.UserId) == true,
        GrantRelationship.AssignedApproval => a.ApproverGroupKey is not null && resource.PendingApprovalGroupKeys?.Contains(a.ApproverGroupKey) == true,
        GrantRelationship.TenantLevelDefinition => resource.IsTenantLevelDefinition,
        GrantRelationship.PolicyAuthorised => resource.EmergencyUnlockAuthorised,
        _ => false
    };

    private enum Coverage { Covered, OutOfScope, AboveCeiling }

    private static Coverage Cover(AssignmentView a, ResourceDescriptor r, string permission, ClassificationScale scale)
    {
        var scope = a.Scope;
        if (scope.Level == ScopeLevel.Installation)
            return r.TenantId == SystemTenant.Id ? Coverage.Covered : Coverage.OutOfScope;

        var dims = r.Dimensions;
        if (dims.HasFlag(ScopeDimensions.Organisation) && scope.Level == ScopeLevel.Organisation
            && !(r.OrganisationId is { } org && scope.OrganisationIds.Contains(org)))
            return Coverage.OutOfScope;

        // A narrowed dimension must match. If the resource type does not carry the dimension, a Division/Site Administrator
        // (whose whole authority is the narrowing) is not covered; other roles are not narrowed by a dimension the record lacks.
        var strict = a.RoleCode == RoleCodes.DivisionSiteAdministrator;
        if (scope.NarrowsBusinessUnit && !Matches(dims, ScopeDimensions.BusinessUnit, r.BusinessUnitId, scope.BusinessUnitIds, strict)) return Coverage.OutOfScope;
        if (scope.NarrowsDivision && !Matches(dims, ScopeDimensions.Division, r.DivisionId, scope.DivisionIds, strict)) return Coverage.OutOfScope;
        if (scope.NarrowsSite && !Matches(dims, ScopeDimensions.Site, r.SiteId, scope.SiteIds, strict)) return Coverage.OutOfScope;

        var categoryNarrows = scope.NarrowsCategory && !(a.RoleCode == RoleCodes.CategoryOwner && ReadAllPermissions.Contains(permission));
        if (categoryNarrows)
        {
            if (dims.HasFlag(ScopeDimensions.Category))
            {
                if (!r.CategoryPath.Any(scope.CategoryIds!.Contains)) return Coverage.OutOfScope;
            }
            else if (strict) return Coverage.OutOfScope;
        }

        if (dims.HasFlag(ScopeDimensions.Classification) && !scale.Permits(scope.ClassificationCeiling, r.Classification))
            return Coverage.AboveCeiling;
        return Coverage.Covered;
    }

    private static bool Matches(ScopeDimensions dims, ScopeDimensions dimension, Guid? value, IReadOnlyList<Guid> allowed, bool strict)
    {
        if (!dims.HasFlag(dimension)) return !strict;
        return value is { } v && allowed.Contains(v);
    }

    /// <summary>The caller's authority for a permission as alternatives for list queries. Installation-level assignments yield none (no tenant data).</summary>
    public IReadOnlyList<AccessGrant> GrantsFor(AccessSubject subject, ClassificationScale scale, DateTime nowUtc, string permission)
    {
        if (!matrix.IsKnown(permission)) throw new ArgumentException($"Unknown permission '{permission}'.", nameof(permission));
        if (subject.Status != UserStatus.Active) return [];
        var grants = new List<AccessGrant>();
        foreach (var a in subject.Assignments.Where(x => x.IsActiveAt(nowUtc)))
        {
            var cell = matrix.Of(a.RoleCode, permission);
            if (cell == Grant.None || a.Scope.Level == ScopeLevel.Installation) continue;
            var scope = a.Scope;
            var categoriesIgnored = a.RoleCode == RoleCodes.CategoryOwner && ReadAllPermissions.Contains(permission);
            grants.Add(new AccessGrant(
                a.Id, a.RoleCode, scope.Level == ScopeLevel.Organisation ? scope.OrganisationIds : null,
                scope.BusinessUnitIds, scope.DivisionIds, scope.SiteIds, categoriesIgnored ? null : scope.CategoryIds,
                scale.AtOrBelow(scope.ClassificationCeiling), cell == Grant.Scoped ? RelationshipOf(a.RoleCode) : GrantRelationship.None));
        }
        return grants;
    }

    /// <summary>True when the caller holds the permission through any active assignment (used for navigation and page-level access; resource checks still apply).</summary>
    public bool HoldsAnywhere(AccessSubject subject, DateTime nowUtc, string permission)
        => subject.Status == UserStatus.Active && matrix.IsKnown(permission)
           && subject.Assignments.Any(a => a.IsActiveAt(nowUtc) && matrix.Of(a.RoleCode, permission) != Grant.None);
}

/// <summary>
/// Who may grant which roles, and within what scope. A delegated administrator can grant only roles and scopes within their own
/// (D1; ACCESS_CONTROL_SPEC section 6). Reversible assumption ASM-12: administrative grantors other than the Tenant Administrator
/// cannot grant the highest classification level; administrators do not read business data, so their own ceiling is not a bound.
/// </summary>
public static class DelegationPolicy
{
    private static readonly Dictionary<string, string[]> Grantable = new()
    {
        [RoleCodes.SystemAdministrator] = [RoleCodes.SystemAdministrator],
        [RoleCodes.TenantAdministrator] = [.. RoleCodes.All.Where(r => r != RoleCodes.SystemAdministrator)],
        [RoleCodes.OrganisationAdministrator] =
        [
            RoleCodes.OrganisationAdministrator, RoleCodes.DivisionSiteAdministrator, RoleCodes.BusinessConfigurationAdministrator,
            RoleCodes.ProcurementAdministrator, RoleCodes.CategoryOwner, RoleCodes.Legal, RoleCodes.Approver, RoleCodes.BackupApprover,
            RoleCodes.Auditor, RoleCodes.Viewer
        ],
        [RoleCodes.DivisionSiteAdministrator] = [RoleCodes.CategoryOwner, RoleCodes.Legal, RoleCodes.Viewer],
    };

    public static IReadOnlyCollection<string> GrantableBy(string grantorRole) => Grantable.TryGetValue(grantorRole, out var g) ? g : [];

    /// <summary>Returns null when allowed, otherwise the reason.</summary>
    public static string? WhyNot(PermissionMatrix matrix, IEnumerable<AssignmentView> grantorAssignments, DateTime nowUtc, string targetRole, ScopeSet target,
        ClassificationScale scale, Func<Guid, IReadOnlyCollection<Guid>>? categoryAncestry = null)
    {
        var any = false;
        foreach (var g in grantorAssignments.Where(a => a.IsActiveAt(nowUtc) && matrix.Of(a.RoleCode, "roles.delegate") != Grant.None))
        {
            any = true;
            if (!GrantableBy(g.RoleCode).Contains(targetRole)) continue;
            var administrative = RoleCodes.IsAdministrative(g.RoleCode);
            if (g.RoleCode == RoleCodes.SystemAdministrator)
            {
                if (target.Level == ScopeLevel.Installation) return null;
                continue;
            }
            if (!g.Scope.Contains(target, scale, categoryAncestry, checkCeiling: !administrative)) continue;
            if (administrative && g.RoleCode != RoleCodes.TenantAdministrator && scale.Levels.Count > 1 && scale.Rank(target.ClassificationCeiling) == scale.Levels.Count - 1)
                continue; // only a Tenant Administrator can grant the highest classification ceiling
            return null;
        }
        return any ? "The role or scope is above what the caller may grant." : "The caller cannot delegate roles.";
    }
}
