namespace Ecp.Identity.Domain;

/// <summary>The twelve confirmed roles (D1). Codes match the columns of <c>requirements/02-reference/role-permission-matrix.csv</c>.</summary>
public static class RoleCodes
{
    public const string SystemAdministrator = "SystemAdministrator";
    public const string TenantAdministrator = "TenantAdministrator";
    public const string OrganisationAdministrator = "OrganisationAdministrator";
    public const string DivisionSiteAdministrator = "DivisionSiteAdministrator";
    public const string BusinessConfigurationAdministrator = "BusinessConfigurationAdministrator";
    public const string ProcurementAdministrator = "ProcurementAdministrator";
    public const string CategoryOwner = "CategoryOwner";
    public const string Legal = "Legal";
    public const string Approver = "Approver";
    public const string BackupApprover = "BackupApprover";
    public const string Auditor = "Auditor";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyList<string> All =
    [
        SystemAdministrator, TenantAdministrator, OrganisationAdministrator, DivisionSiteAdministrator, BusinessConfigurationAdministrator,
        ProcurementAdministrator, CategoryOwner, Legal, Approver, BackupApprover, Auditor, Viewer
    ];

    public static bool IsKnown(string? role) => role is not null && All.Contains(role);

    /// <summary>Roles that administer rather than read business data (no implicit access to contract records).</summary>
    public static bool IsAdministrative(string role) => role is SystemAdministrator or TenantAdministrator or OrganisationAdministrator
        or DivisionSiteAdministrator or BusinessConfigurationAdministrator;
}

/// <summary>Viewer is never a global read role: it is created as one of these kinds, each requiring its own scope (ACCESS_CONTROL_SPEC 2.1).</summary>
public enum ViewerKind { AllContracts, Category, BusinessUnit, Site, Restricted }

/// <summary>Where an assignment applies: the whole installation (no tenant data), the tenant, or named organisations.</summary>
public enum ScopeLevel { Installation, Tenant, Organisation }

public enum UserStatus { Invited, Active, Disabled }

public enum AssignmentSource { Manual, GroupMapping, Onboarding, Setup, Demo, TestBootstrap }
