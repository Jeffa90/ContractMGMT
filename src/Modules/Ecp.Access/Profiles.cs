using Ecp.Identity.Domain;
using Ecp.SharedKernel;

namespace Ecp.Access;

/// <summary>How much a profile lets a person see by default, in the organisation's own level names.</summary>
public enum CeilingDefault { OrganisationDefault, UpToConfidential }

/// <summary>
/// An access profile is a plain-English name for a recipe made of existing roles (and, for some, owning categories). It is a presentation and composition layer over the security engine: applying one issues
/// ordinary role grants and ownerships through the services that check the delegation rules, and nothing here decides what anyone can do. A profile cannot carry a permission, a new role, or System or
/// Tenant Administrator.
/// </summary>
/// <param name="Key">Stable key stored with the provenance row.</param>
/// <param name="Label">What the person sees, as a terminology string (<c>[[contract]]</c> tokens): the screens expand it with the organisation's own words.</param>
/// <param name="Summary">One sentence on what the profile lets a person do.</param>
/// <param name="RoleCodes">The existing stable role codes the recipe grants, in order.</param>
/// <param name="OwnsCategories">The profile needs ownership of categories to be useful (the role alone holds almost nothing).</param>
/// <param name="NeedsApproverGroup">An approver group must be chosen.</param>
/// <param name="ViewerFromScope">The access choice becomes the Viewer's kind (everything, categories, business units, sites).</param>
/// <param name="WholeOrganisationOnly">An administrator-grade profile: always the whole organisation, never narrowed.</param>
/// <param name="RequiresCapability">The organisation capability that must be on for the profile to be offered.</param>
public sealed record AccessProfile(string Key, string Label, string Summary, IReadOnlyList<string> RoleCodes, bool OwnsCategories, bool NeedsApproverGroup, bool ViewerFromScope,
    bool WholeOrganisationOnly, CeilingDefault Ceiling, string? RequiresCapability)
{
    /// <summary>Raised when the recipe changes, so provenance rows can say which version of the recipe they used.</summary>
    public const int Version = 1;

    public bool Administrative => RoleCodes.Any(RoleCodes_IsAdministrative);
    private static bool RoleCodes_IsAdministrative(string role) => Ecp.Identity.Domain.RoleCodes.IsAdministrative(role);
}

public static class AccessProfiles
{
    public const string ContractManager = "contract-manager";
    public const string ContractAdministrator = "contract-administrator";
    public const string Reviewer = "reviewer";
    public const string ApprovalAuthority = "approval-authority";
    public const string ReadOnly = "read-only";
    public const string OrganisationAdministrator = "organisation-administrator";
    public const string BusinessConfigurationAdministrator = "business-configuration-administrator";
    public const string Auditor = "auditor";

    /// <summary>The default catalogue, compiled into the product. It uses only existing role codes and is validated by tests against the role matrix and the assignment rules.</summary>
    public static readonly IReadOnlyList<AccessProfile> All =
    [
        new(ContractManager, "[[contract]] Manager", "Create and manage [[contract|plural|lower]] in the [[category|plural|lower]] they own: edit, upload documents, send for review and approval, [[variation|plural|lower]], [[renewal|plural|lower]] and closure.",
            [RoleCodes.CategoryOwner], OwnsCategories: true, NeedsApproverGroup: false, ViewerFromScope: false, WholeOrganisationOnly: false, CeilingDefault.OrganisationDefault, null),
        new(ContractAdministrator, "[[contract]] Administrator", "Everything a [[contract]] Manager does, in any [[category|lower]], plus closing [[contract|plural|lower]], loading data from an old system and exporting records.",
            [RoleCodes.ProcurementAdministrator, RoleCodes.CategoryOwner], OwnsCategories: true, NeedsApproverGroup: false, ViewerFromScope: false, WholeOrganisationOnly: false, CeilingDefault.UpToConfidential, null),
        new(Reviewer, "Reviewer", "Review [[contract|plural|lower]] and give a formal opinion ([[legal.review|lower]]).",
            [RoleCodes.Legal], OwnsCategories: false, NeedsApproverGroup: false, ViewerFromScope: false, WholeOrganisationOnly: false, CeilingDefault.UpToConfidential, CapabilityKeys.LegalReview),
        new(ApprovalAuthority, "Approval Authority", "Approve or reject executed [[contract|plural|lower]] sent to their approver group.",
            [RoleCodes.Approver], OwnsCategories: false, NeedsApproverGroup: true, ViewerFromScope: false, WholeOrganisationOnly: false, CeilingDefault.UpToConfidential, CapabilityKeys.RegisterAuthorisation),
        new(ReadOnly, "Read Only", "See [[contract|plural|lower]] and [[supplier|plural|lower]] and download documents, but change nothing.",
            [RoleCodes.Viewer], OwnsCategories: false, NeedsApproverGroup: false, ViewerFromScope: true, WholeOrganisationOnly: false, CeilingDefault.OrganisationDefault, null),
        new(OrganisationAdministrator, "Organisation Administrator", "Set up the [[organisation|lower]]: people, [[category|plural|lower]], structure and business rules. Does not include working on [[contract|plural|lower]].",
            [RoleCodes.OrganisationAdministrator], OwnsCategories: false, NeedsApproverGroup: false, ViewerFromScope: false, WholeOrganisationOnly: true, CeilingDefault.OrganisationDefault, null),
        new(BusinessConfigurationAdministrator, "Business Configuration Administrator", "Prepare and publish business rules (workflows, wording, [[category|plural|lower]], packs). Cannot give people access.",
            [RoleCodes.BusinessConfigurationAdministrator], OwnsCategories: false, NeedsApproverGroup: false, ViewerFromScope: false, WholeOrganisationOnly: true, CeilingDefault.OrganisationDefault, null),
        new(Auditor, "Auditor", "Read [[contract|plural|lower]], the audit trail and exports, to check how the system is used.",
            [RoleCodes.Auditor], OwnsCategories: false, NeedsApproverGroup: false, ViewerFromScope: false, WholeOrganisationOnly: true, CeilingDefault.UpToConfidential, null),
    ];

    public static AccessProfile? Find(string? key) => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    /// <summary>The ceiling a profile starts with, in the scale's own names. Never above the high threshold: a higher ceiling is always an explicit choice by someone entitled to give it.</summary>
    public static string DefaultCeiling(AccessProfile profile, ClassificationScale scale)
    {
        if (profile.Ceiling == CeilingDefault.UpToConfidential && scale.Levels.Contains("Confidential", StringComparer.OrdinalIgnoreCase)) return scale.Levels.First(l => string.Equals(l, "Confidential", StringComparison.OrdinalIgnoreCase));
        return scale.DefaultLevel;
    }
}

/// <summary>How the access of a person is narrowed in the wizard (step 3). The words are the screen's; the mapping to the engine is below.</summary>
public enum ScopeChoice { WholeOrganisation, Divisions, BusinessUnits, Sites, Categories }

/// <summary>Ownership of categories (step 4).</summary>
public enum OwnershipChoice { None, All, Selected }

/// <summary>One thing the wizard asks the service to do for a person in an organisation. The service turns it into ordinary grants and ownerships.</summary>
public sealed record AccessChange(
    Guid UserId, Guid OrganisationId, IReadOnlyList<string> ProfileKeys, ScopeChoice Scope = ScopeChoice.WholeOrganisation, IReadOnlyList<Guid>? ScopeIds = null,
    string? Ceiling = null, bool PrivilegedAccess = false, OwnershipChoice Ownership = OwnershipChoice.None, IReadOnlyList<Guid>? OwnedCategoryIds = null,
    string? ApproverGroupKey = null, string? Reason = null)
{
    public IReadOnlyList<Guid> ScopeIdList => ScopeIds ?? [];
    public IReadOnlyList<Guid> OwnedCategories => OwnedCategoryIds ?? [];
}
