namespace Ecp.SharedKernel;

/// <summary>One term the platform knows: a stable key and its business-neutral wording. Keys are machine identifiers and never change; wording is data that packs and organisations replace.</summary>
public sealed record TermDefinition(string Key, string Group, string Singular, string Plural, string Description, bool Required = false, string? Short = null, string? Help = null);

/// <summary>
/// The platform's terminology catalogue: every key a generic page, message or export may ask for, with the <b>neutral</b> wording used when neither the organisation nor its solution pack says otherwise.
/// Nothing here names a business. The neutral wording is the last data layer; the stable key itself is only ever shown as a diagnostic when a key is not in this catalogue.
/// </summary>
public static class TermCatalogue
{
    private static readonly TermDefinition[] Definitions =
    [
        // Application and contracts
        new("application.name", "Application", "Enterprise Contract Platform", "Enterprise Contract Platform", "The name of the application shown in the header and in messages.", true),
        new("contract", "Contracts", "Contract", "Contracts", "The record this application manages.", true),
        new("contract.register", "Contracts", "Contract Register", "Contract Registers", "The searchable list of contracts a person may see.", true),
        new("contract.reference", "Contracts", "Contract Reference", "Contract References", "The reference number assigned to a contract.", true),
        new("contract.type", "Contracts", "Contract Type", "Contract Types", "The kind of contract, chosen from a list the organisation configures.", true),
        new("contract.portfolio", "Contracts", "Contract Portfolio", "Contract Portfolios", "A grouping of contracts that share an owner.", true),
        new("agreement", "Contracts", "Contract", "Contracts", "The legal instrument itself, where a page distinguishes it from the record that manages it."),
        new("registered.date", "Contracts", "Registered Date", "Registered Dates", "The date a contract was registered (stamped when registration is approved)."),
        new("executed.document", "Documents", "Executed Document", "Executed Documents", "The signed copy of a contract.", true),
        new("document", "Documents", "Document", "Documents", "A file attached to a contract.", true),
        // People and roles
        new("contract.owner", "Roles", "Contract Owner", "Contract Owners", "The person responsible for a contract or a portfolio of contracts.", true),
        new("contract.administrator", "Roles", "Contract Administrator", "Contract Administrators", "The person who administers contracts across an organisation.", true),
        new("reviewer", "Roles", "Reviewer", "Reviewers", "A person who reviews a contract before it is signed or registered.", true),
        new("review.team", "Roles", "Review Team", "Review Teams", "The group that carries out reviews, where a page names the group rather than a person."),
        new("primary.approver", "Roles", "Approval Authority", "Approval Authorities", "The person who approves registration of an executed contract.", true),
        new("backup.approver", "Roles", "Backup Approver", "Backup Approvers", "A person who may approve in place of the primary approver.", true),
        new("role.system.administrator", "Roles", "System Administrator", "System Administrators", "Looks after the installation; has no access to contract data."),
        new("role.tenant.administrator", "Roles", "Tenant Administrator", "Tenant Administrators", "Administers organisations and tenant-wide settings."),
        new("role.organisation.administrator", "Roles", "Organisation Administrator", "Organisation Administrators", "Administers one organisation."),
        new("role.division.site.administrator", "Roles", "Division or Site Administrator", "Division or Site Administrators", "Administers a division or site."),
        new("role.configuration.administrator", "Roles", "Business Configuration Administrator", "Business Configuration Administrators", "Configures fields, workflows, terminology and similar settings."),
        new("role.auditor", "Roles", "Auditor", "Auditors", "Reads audit evidence."),
        new("role.viewer", "Roles", "Viewer", "Viewers", "Reads within the scope granted."),
        // Counterparties and classification of work
        new("counterparty", "Parties", "Counterparty", "Counterparties", "The other party to a contract.", true),
        new("supplier", "Parties", "Counterparty", "Counterparties", "The organisation that supplies goods or services under a contract.", true),
        new("category", "Parties", "Category", "Categories", "A grouping of contracts, usually with an owner.", true),
        new("subcategory", "Parties", "Subcategory", "Subcategories", "A grouping inside a category.", true),
        // Workflow
        new("legal.review", "Workflow", "Review", "Reviews", "The review a contract goes through before signature.", true),
        new("approval", "Workflow", "Approval", "Approvals", "A decision that lets work continue.", true),
        new("registration.approval", "Workflow", "Registration Approval", "Registration Approvals", "The approval that registers an executed contract and makes it effective.", true),
        new("external.execution", "Workflow", "External Execution", "External Executions", "Signature outside the platform.", true),
        new("variation", "Workflow", "Variation", "Variations", "A controlled change to an effective contract.", true),
        new("renewal", "Workflow", "Renewal", "Renewals", "A successor contract that replaces one that is ending.", true),
        new("notification", "Workflow", "Notification", "Notifications", "A message about work waiting or a date approaching.", true),
        // Contract field labels: the words on the forms, details, register columns, exports, dashboards and reminders. Optional: the neutral wording below applies until a pack or an organisation sets its own.
        new("supplier.number", "Fields", "Vendor Number", "Vendor Number", "The supplier's identifier in the organisation's purchasing or finance system."),
        new("supplier.tax.id", "Fields", "Tax Identifier", "Tax Identifier", "The supplier's national business or tax identifier. The format check built in today is eleven digits."),
        new("erp.reference", "Fields", "ERP Reference", "ERP Reference", "The reference of the contract in the organisation's purchasing or finance system."),
        new("field.description", "Fields", "Description", "Description", "The free-text description of a contract."),
        new("field.owner", "Fields", "Owner", "Owner", "The person who owns a contract."),
        new("field.status", "Fields", "Status", "Status", "Where a contract is in its lifecycle."),
        new("field.classification", "Fields", "Data Classification", "Data Classification", "How sensitive the contract's information is."),
        new("field.risk.level", "Fields", "Risk Level", "Risk Level", "The assessed risk of a contract."),
        new("field.function", "Fields", "Function", "Function", "The business function a contract serves."),
        new("field.currency", "Fields", "Currency", "Currency", "The currency of a contract's value."),
        new("field.estimated.value", "Fields", "Estimated Value", "Estimated Value", "The estimated value of a contract."),
        new("field.extension.type", "Fields", "Extension Type", "Extension Type", "How a contract can be extended."),
        new("field.evergreen", "Fields", "Evergreen", "Evergreen", "A contract that renews without an end date."),
        new("field.notice.period", "Fields", "Notice Period", "Notice Period", "How much notice must be given to end or renew a contract."),
        new("field.critical.notice", "Fields", "Critical Notice", "Critical Notice", "A flag for a contract whose notice deadline must not be missed."),
        new("field.start.date", "Fields", "Start Date", "Start Date", "The date a contract starts."),
        new("field.effective.date", "Fields", "Effective Date", "Effective Date", "The date a contract takes effect."),
        new("field.review.date", "Fields", "Review Date", "Review Date", "The date a contract is due for review."),
        new("field.end.date", "Fields", "End Date", "End Date", "The date a contract ends."),
        new("field.locked", "Fields", "Locked", "Locked", "Whether a contract's details are locked against change."),
        new("field.created", "Fields", "Created", "Created", "When a record was created."),
        new("field.updated", "Fields", "Updated", "Updated", "When a record was last changed."),
        new("field.group.identity", "Field groups", "Identity", "Identity", "The heading that groups the identity fields of a contract."),
        new("field.group.ownership", "Field groups", "Ownership", "Ownership", "The heading that groups the ownership fields of a contract."),
        new("field.group.lifecycle", "Field groups", "Lifecycle", "Lifecycle", "The heading that groups the lifecycle fields of a contract."),
        new("field.group.governance", "Field groups", "Governance", "Governance", "The heading that groups the governance fields of a contract."),
        new("field.group.classification", "Field groups", "Classification", "Classification", "The heading that groups the classification fields of a contract."),
        new("field.group.structure", "Field groups", "Structure", "Structure", "The heading that groups the structure fields of a contract."),
        new("field.group.commercial", "Field groups", "Commercial", "Commercial", "The heading that groups the commercial fields of a contract."),
        new("field.group.dates", "Field groups", "Dates", "Dates", "The heading that groups the dates fields of a contract."),
        new("field.group.audit", "Field groups", "Audit", "Audit", "The heading that groups the audit fields of a contract."),
        // Organisation structure
        new("organisation", "Structure", "Organisation", "Organisations", "A legal or operating organisation inside the tenant.", true),
        new("legal.entity", "Structure", "Legal Entity", "Legal Entities", "A company or other legal person.", true),
        new("business.unit", "Structure", "Business Unit", "Business Units", "A unit of the business.", true),
        new("division", "Structure", "Division", "Divisions", "A division of the organisation.", true),
        new("site", "Structure", "Site", "Sites", "A place where the business operates.", true),
        // Administration
        new("solution.pack", "Administration", "Solution Pack", "Solution Packs", "A ready-made starting configuration for a kind of business.", true),
        new("migration.project", "Administration", "Migration Project", "Migration Projects", "A planned load of records from a previous system.", true),
        new("migration.batch", "Administration", "Migration Batch", "Migration Batches", "One load inside a migration project.", true),
    ];

    /// <summary>Spelling differences for locales whose language is the same but whose spelling is not (the platform's reference spelling is the neutral English above).</summary>
    private static readonly (string Locale, string Key, string Singular, string Plural)[] LocaleVariants =
    [
        ("en-US", "organisation", "Organization", "Organizations"),
    ];

    public static IReadOnlyList<TermDefinition> All { get; } = Definitions;
    public static IReadOnlyDictionary<string, TermDefinition> ByKey { get; } = Definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);
    public static IReadOnlyList<string> RequiredKeys { get; } = Definitions.Where(d => d.Required).Select(d => d.Key).ToList();
    public static IReadOnlyList<(string Locale, string Key, string Singular, string Plural)> Variants => LocaleVariants;

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    /// <summary>The term key that labels a role. The role codes are stable identifiers and are not renamed; the label comes from terminology.</summary>
    public static string KeyForRole(string roleCode) => roleCode switch
    {
        "ProcurementAdministrator" => "contract.administrator",
        "CategoryOwner" => "contract.owner",
        "Legal" => "reviewer",
        "Approver" => "primary.approver",
        "BackupApprover" => "backup.approver",
        "SystemAdministrator" => "role.system.administrator",
        "TenantAdministrator" => "role.tenant.administrator",
        "OrganisationAdministrator" => "role.organisation.administrator",
        "DivisionSiteAdministrator" => "role.division.site.administrator",
        "BusinessConfigurationAdministrator" => "role.configuration.administrator",
        "Auditor" => "role.auditor",
        "Viewer" => "role.viewer",
        _ => ""
    };
}
