using Ecp.SharedKernel;

namespace Ecp.Identity.Domain;

/// <summary>Rules about which scope levels each role may be assigned at, and how a Viewer is shaped (D1; ACCESS_CONTROL_SPEC section 2).</summary>
public static class AssignmentRules
{
    private static readonly Dictionary<string, ScopeLevel[]> AllowedLevels = new()
    {
        [RoleCodes.SystemAdministrator] = [ScopeLevel.Installation],
        [RoleCodes.TenantAdministrator] = [ScopeLevel.Tenant],
        [RoleCodes.OrganisationAdministrator] = [ScopeLevel.Organisation],
        [RoleCodes.DivisionSiteAdministrator] = [ScopeLevel.Organisation],
        [RoleCodes.BusinessConfigurationAdministrator] = [ScopeLevel.Organisation],
        [RoleCodes.ProcurementAdministrator] = [ScopeLevel.Organisation],
        [RoleCodes.CategoryOwner] = [ScopeLevel.Organisation],
        [RoleCodes.Legal] = [ScopeLevel.Organisation],
        [RoleCodes.Approver] = [ScopeLevel.Organisation],
        [RoleCodes.BackupApprover] = [ScopeLevel.Organisation],
        [RoleCodes.Auditor] = [ScopeLevel.Organisation, ScopeLevel.Tenant],
        [RoleCodes.Viewer] = [ScopeLevel.Organisation],
    };

    /// <summary>Throws when the combination of role, viewer kind, scope and approver group is not allowed.</summary>
    public static void Validate(string role, ViewerKind? viewerKind, ScopeSet scope, string? approverGroupKey, ClassificationScale scale, bool explicitHighCeiling = false, bool privilegedAccess = false)
    {
        if (privilegedAccess)
        {
            if (scale.PrivilegedLevels.Count == 0) throw new DomainException("no_privileged_level", "The classification scale has no privileged level, so privileged access cannot be granted.");
            if (RoleCodes.IsAdministrative(role)) throw new DomainException("privileged_not_for_administrators", "Administrative roles never read business data, so privileged access cannot be granted to them.");
        }
        if (!RoleCodes.IsKnown(role)) throw new DomainException("unknown_role", $"Role '{role}' is not recognised.");
        scope.Validate(scale);
        if (!AllowedLevels[role].Contains(scope.Level))
            throw new DomainException("invalid_scope_level", $"Role {role} cannot be assigned at {scope.Level} level.");

        if (role == RoleCodes.DivisionSiteAdministrator && !scope.NarrowsDivision && !scope.NarrowsSite)
            throw new DomainException("scope_required", "[[division|a|cap]]/Site Administrator is limited to assigned [[division|plural|lower]] or [[site|plural|lower]] and must name at least one.");

        if (role is RoleCodes.Approver or RoleCodes.BackupApprover)
            Guard.NotBlank(approverGroupKey, "Approver group", 100);
        else if (!string.IsNullOrWhiteSpace(approverGroupKey))
            throw new DomainException("invalid_approver_group", "An approver group applies only to Approver and Backup Approver roles.");

        if (role == RoleCodes.Viewer) ValidateViewer(viewerKind, scope, scale, explicitHighCeiling);
        else if (viewerKind is not null) throw new DomainException("invalid_viewer_kind", "A viewer kind applies only to the Viewer role.");
    }

    private static void ValidateViewer(ViewerKind? kind, ScopeSet scope, ClassificationScale scale, bool explicitHighCeiling)
    {
        if (kind is null) throw new DomainException("viewer_kind_required", "A Viewer assignment must be one of: All [[contract|plural]], [[category]], [[business.unit]], [[site]] or Restricted Viewer.");
        switch (kind)
        {
            case ViewerKind.AllContracts when scope.NarrowsCategory || scope.NarrowsBusinessUnit || scope.NarrowsDivision || scope.NarrowsSite:
                throw new DomainException("invalid_scope", "An All [[contract|plural]] Viewer covers the whole [[organisation|lower]]; use another viewer kind to narrow it.");
            case ViewerKind.Category when !scope.NarrowsCategory:
                throw new DomainException("scope_required", "[[category|a|cap]] Viewer must name at least one [[category|lower]].");
            case ViewerKind.BusinessUnit when !scope.NarrowsBusinessUnit && !scope.NarrowsDivision:
                throw new DomainException("scope_required", "[[business.unit|a|cap]] Viewer must name at least one [[business.unit|lower]] (or [[division|lower]]).");
            case ViewerKind.Site when !scope.NarrowsSite:
                throw new DomainException("scope_required", "[[site|a|cap]] Viewer must name at least one [[site|lower]].");
            case ViewerKind.Restricted:
                if (!scope.NarrowsCategory && !scope.NarrowsBusinessUnit && !scope.NarrowsDivision && !scope.NarrowsSite)
                    throw new DomainException("scope_required", "A Restricted Viewer must be narrowed by [[category|lower]], [[business.unit|lower]], [[division|lower]] or [[site|lower]].");
                if (!explicitHighCeiling && scale.Rank(scope.ClassificationCeiling) > RestrictedCeilingLimit(scale))
                    throw new DomainException("ceiling_too_high", "A Restricted Viewer's classification ceiling may not exceed Confidential unless explicitly set.");
                break;
        }
    }

    /// <summary>The highest rank a Restricted Viewer gets without an explicit decision: "Confidential" if the scale has it, otherwise the second-highest level.</summary>
    public static int RestrictedCeilingLimit(ClassificationScale scale)
    {
        var confidential = scale.Rank("Confidential");
        return confidential >= 0 ? confidential : Math.Max(0, scale.Levels.Count - 2);
    }
}

/// <summary>A role granted to a user with its scope, effective-dated. Revocation ends the assignment (history is retained).</summary>
public sealed class RoleAssignment : ITenantOwned
{
    private RoleAssignment() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string RoleCode { get; private set; } = "";
    public ViewerKind? ViewerKind { get; private set; }
    public ScopeLevel ScopeLevel { get; private set; }
    public IReadOnlyList<Guid> OrganisationIds { get; private set; } = [];
    public IReadOnlyList<Guid> BusinessUnitIds { get; private set; } = [];
    public IReadOnlyList<Guid> DivisionIds { get; private set; } = [];
    public IReadOnlyList<Guid> SiteIds { get; private set; } = [];
    public IReadOnlyList<Guid>? CategoryIds { get; private set; }
    public string ClassificationCeiling { get; private set; } = "";
    public string? ApproverGroupKey { get; private set; }
    /// <summary>An explicit grant of access to records at a privileged classification level (in addition to a ceiling that reaches it). Granted only by a Tenant Administrator.</summary>
    public bool PrivilegedAccess { get; private set; }
    public DateTime EffectiveFromUtc { get; private set; }
    public DateTime? EffectiveToUtc { get; private set; }
    public Guid? GrantedBy { get; private set; }
    public string GrantedByLabel { get; private set; } = "";
    public string? Reason { get; private set; }
    public AssignmentSource Source { get; private set; }
    public Guid? SourceMappingId { get; private set; }
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public ScopeSet Scope => new(ScopeLevel, OrganisationIds, BusinessUnitIds, DivisionIds, SiteIds, CategoryIds, ClassificationCeiling);

    public static RoleAssignment Create(Guid tenantId, Guid userId, string role, ViewerKind? viewerKind, ScopeSet scope, string? approverGroupKey,
        DateTime effectiveFromUtc, DateTime? effectiveToUtc, Guid? grantedBy, string grantedByLabel, string? reason, AssignmentSource source,
        Guid? sourceMappingId, ClassificationScale scale, DateTime nowUtc, bool explicitHighCeiling = false, bool privilegedAccess = false)
    {
        AssignmentRules.Validate(role, viewerKind, scope, approverGroupKey, scale, explicitHighCeiling, privilegedAccess);
        Guard.Utc(effectiveFromUtc, nameof(effectiveFromUtc));
        if (effectiveToUtc.HasValue && Guard.Utc(effectiveToUtc.Value, nameof(effectiveToUtc)) <= effectiveFromUtc)
            throw new DomainException("invalid_dates", "The assignment must end after it starts.");
        return new RoleAssignment
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), UserId = Guard.NotEmpty(userId, nameof(userId)), RoleCode = role,
            ViewerKind = viewerKind, ScopeLevel = scope.Level, OrganisationIds = scope.OrganisationIds, BusinessUnitIds = scope.BusinessUnitIds,
            DivisionIds = scope.DivisionIds, SiteIds = scope.SiteIds, CategoryIds = scope.CategoryIds, ClassificationCeiling = scope.ClassificationCeiling,
            ApproverGroupKey = string.IsNullOrWhiteSpace(approverGroupKey) ? null : approverGroupKey.Trim(), PrivilegedAccess = privilegedAccess, EffectiveFromUtc = effectiveFromUtc,
            EffectiveToUtc = effectiveToUtc, GrantedBy = grantedBy, GrantedByLabel = Guard.NotBlank(grantedByLabel, "Granted by", 200),
            Reason = Guard.OptionalBounded(reason, "Reason", 1000), Source = source, SourceMappingId = sourceMappingId, CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
    }

    public bool IsActiveAt(DateTime utc) => EffectiveFromUtc <= utc && (EffectiveToUtc is null || utc < EffectiveToUtc);

    /// <summary>Ends the assignment now. Idempotent: an already ended assignment is unchanged.</summary>
    public void End(DateTime nowUtc)
    {
        Guard.Utc(nowUtc, nameof(nowUtc));
        if (EffectiveToUtc is null || EffectiveToUtc > nowUtc) EffectiveToUtc = nowUtc < EffectiveFromUtc ? EffectiveFromUtc : nowUtc; // ending before it started leaves a zero-length (never active) period
    }
}

/// <summary>
/// Maps an identity-provider group to a role and scope. Applied at sign-in: mapped assignments are created while the user is in the
/// group and ended when the user leaves it. Mappings never grant SystemAdministrator or TenantAdministrator (those are explicit).
/// </summary>
public sealed class GroupRoleMapping : ITenantOwned
{
    private GroupRoleMapping() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string ProviderKey { get; private set; } = "";
    public string GroupKey { get; private set; } = "";
    public string RoleCode { get; private set; } = "";
    public ViewerKind? ViewerKind { get; private set; }
    public ScopeLevel ScopeLevel { get; private set; }
    public IReadOnlyList<Guid> OrganisationIds { get; private set; } = [];
    public IReadOnlyList<Guid> BusinessUnitIds { get; private set; } = [];
    public IReadOnlyList<Guid> DivisionIds { get; private set; } = [];
    public IReadOnlyList<Guid> SiteIds { get; private set; } = [];
    public IReadOnlyList<Guid>? CategoryIds { get; private set; }
    public string ClassificationCeiling { get; private set; } = "";
    public string? ApproverGroupKey { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTime CreatedUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public ScopeSet Scope => new(ScopeLevel, OrganisationIds, BusinessUnitIds, DivisionIds, SiteIds, CategoryIds, ClassificationCeiling);

    public static GroupRoleMapping Create(Guid tenantId, string providerKey, string groupKey, string role, ViewerKind? viewerKind, ScopeSet scope,
        string? approverGroupKey, ClassificationScale scale, DateTime nowUtc)
    {
        if (role is RoleCodes.SystemAdministrator or RoleCodes.TenantAdministrator)
            throw new DomainException("mapping_not_allowed", "Identity-group mappings cannot grant System or Tenant Administrator; assign those explicitly.");
        AssignmentRules.Validate(role, viewerKind, scope, approverGroupKey, scale);
        return new GroupRoleMapping
        {
            Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), ProviderKey = Guard.NotBlank(providerKey, "Provider", 100),
            GroupKey = Guard.NotBlank(groupKey, "Group", 500), RoleCode = role, ViewerKind = viewerKind, ScopeLevel = scope.Level,
            OrganisationIds = scope.OrganisationIds, BusinessUnitIds = scope.BusinessUnitIds, DivisionIds = scope.DivisionIds, SiteIds = scope.SiteIds,
            CategoryIds = scope.CategoryIds, ClassificationCeiling = scope.ClassificationCeiling,
            ApproverGroupKey = string.IsNullOrWhiteSpace(approverGroupKey) ? null : approverGroupKey.Trim(), CreatedUtc = Guard.Utc(nowUtc, nameof(nowUtc))
        };
    }

    public void Deactivate() => IsActive = false;
}
