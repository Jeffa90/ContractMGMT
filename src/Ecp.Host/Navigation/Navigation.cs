using Ecp.SharedKernel;

namespace Ecp.Host.Navigation;

/// <summary>
/// One place that says where every page lives in the navigation (docs/UX_REDESIGN.md section 3). Everyday work is a short row of links; every administration page is grouped under one
/// Administration entry. Visibility here is presentation: each page and service still decides, on the server, whether the person may use it.
/// Titles are localisation tokens; the layout expands them with the terminology in force.
/// </summary>
public sealed record NavEntry(string Key, string Href, string Title, string Description, string[] AnyPermission, string? Capability = null, string? Match = null)
{
    public string MatchPrefix => Match ?? Href;
}

public sealed record NavGroup(string Key, string Title, string Description, IReadOnlyList<NavEntry> Entries);

/// <summary>A list of work that needs the person: reached from My Actions.</summary>
public sealed record ActionQueue(string Key, string Href, string Title, string Description, string[] AnyPermission, string? Capability);

public sealed record NavItem(string Key, string Href, string Text, string Match, string? Badge = null);

public sealed record AdminSection(string Key, string Title, string Description, IReadOnlyList<NavItem> Items);

public sealed record NavigationModel(IReadOnlyList<NavItem> Primary, IReadOnlyList<AdminSection> Administration, IReadOnlyList<ActionQueue> Queues, int Unread)
{
    public bool HasAdministration => Administration.Count > 0;
    public bool Shows(string key) => Primary.Any(i => i.Key == key) || Administration.Any(s => s.Items.Any(i => i.Key == key));
}

public static class NavigationCatalogue
{
    public static readonly IReadOnlyList<ActionQueue> Queues =
    [
        new("legal", "/Legal", "[[legal.review|sentence]]", "Submissions waiting for [[reviewer|lower]].", ["legal.review", "legal.respond", "contracts.submit_legal"], CapabilityKeys.LegalReview),
        new("approvals", "/Approvals", "[[approval|plural]]", "Executed [[agreement|plural|lower]] waiting for [[registration.approval|lower]].", ["approvals.action", "contracts.submit_approval"], CapabilityKeys.RegisterAuthorisation),
        new("variations", "/Variations", "[[variation|plural]]", "Open [[variation|plural|lower]] on Effective [[agreement|plural|lower]].", ["variations.create"], CapabilityKeys.Variations),
        new("renewals", "/Renewals", "[[renewal|plural]]", "[[agreement|plural]] being renewed.", ["renewals.create"], CapabilityKeys.Renewals),
        new("reviews", "/Admin/Reviews", "Configuration reviews", "Changes to set-up waiting for a second person to review and publish.",
            ["config.draft", "config.publish", "terminology.draft", "terminology.publish", "capabilities.draft", "capabilities.publish"], null),
    ];

    /// <summary>Everyday links, in order. Documents is not listed: there is no cross-contract document list yet (docs/UX_REDESIGN.md UXG-09).</summary>
    public static readonly IReadOnlyList<NavEntry> Everyday =
    [
        new("home", "/", "Home", "Your work and what to do next.", [], null, "/"),
        new("contracts", "/Contracts", "[[contract|plural]]", "Find, create and manage [[contract|plural|lower]].", ["contracts.read"]),
        new("suppliers", "/Suppliers", "[[supplier|plural]]", "The [[supplier|plural|lower]] you contract with.", ["suppliers.read"]),
        new("reports", "/Reports", "Reports", "Dashboards, saved lists and exports.", ["contracts.read", "audit.read", "records.export"]),
    ];

    public static readonly IReadOnlyList<NavGroup> Administration =
    [
        new("people", "People & Access", "Who can sign in and what they can do.",
        [
            new("people", "/Admin/People", "People", "Add people, say what each will do, and see what that lets them do.", ["users.manage"], null, "/Admin/People"),
            new("users", "/Admin/Users", "Users and roles (advanced)", "Exact role grants, scopes and classification ceilings.", ["users.manage"], null, "/Admin/Users"),
            new("simulation", "/Admin/Simulation", "Access simulator (advanced)", "Test what a person could do before giving access.", ["users.manage"]),
            new("mappings", "/Admin/People/Mappings", "Identity group mappings (advanced)", "Give access automatically to a group from your single sign-on.", ["rolemappings.manage"]),
        ]),
        new("organisation", "Organisation", "Your [[organisation|lower]], its structure and regional settings.",
        [
            new("organisations", "/Admin/Organisations", "[[organisation|plural]]", "Create an [[organisation|lower]] and follow its set-up.", ["organisations.manage"]),
            new("onboarding", "/Onboarding", "[[organisation]] onboarding", "The checklist that makes an [[organisation|lower]] ready to use.", ["onboarding.run"]),
            new("regional", "/Admin/Regional", "Regional settings", "Locale, time zone, currency and formats.", ["config.draft", "config.history.read"]),
        ]),
        new("contracts", "[[contract|plural]] & Metadata", "[[category|plural]], owners, fields and wording.",
        [
            new("categories", "/Admin/Categories", "[[category|plural]] and owners", "Who owns each [[category|lower]].", ["categories.manage", "categories.assign"]),
            new("terminology", "/Admin/Terminology", "Terminology", "The words your [[organisation|lower]] uses.", ["terminology.read", "terminology.draft"]),
            new("capabilities", "/Admin/Capabilities", "Capabilities", "Which optional steps are switched on.", ["capabilities.read", "capabilities.draft"]),
            new("studio", "/Admin/Studio", "Configuration Studio", "Fields, forms and saved lists.", ["config.draft", "config.history.read"]),
            new("migration", "/Migration", "Data migration", "Load an existing register.", ["migration.stage"]),
        ]),
        new("workflows", "Workflows & Approvals", "How [[contract|plural|lower]] move and who approves them.",
        [
            new("workflow", "/Admin/Configuration", "Workflow configuration", "States, steps and buttons.", ["config.draft", "config.history.read"]),
            new("matrices", "/Admin/Approvals", "[[approval]] matrices", "Who approves what.", ["approvals.matrix.read", "approvals.simulate", "approvals.delegate"], CapabilityKeys.RegisterAuthorisation),
            new("authorities", "/Admin/ApprovalAuthorities", "Approval authorities", "Choose who approves, and who stands in, by name.", ["users.manage"], CapabilityKeys.RegisterAuthorisation, "/Admin/ApprovalAuthorities"),
            new("approvergroups", "/Admin/ApproverGroups", "Approver groups (advanced)", "Register a group by its key, set delegation, retire a group.", ["users.manage"]),
            new("reviews", "/Admin/Reviews", "Configuration reviews", "Changes waiting for a second person.",
                ["config.draft", "config.publish", "terminology.draft", "terminology.publish", "capabilities.draft", "capabilities.publish"]),
        ]),
        new("notifications", "Notifications", "What people are told and how.",
        [
            new("delivery", "/Admin/Notifications", "[[notification]] delivery", "Delivery attempts, failures and retries.", ["notifications.administer"]),
        ]),
        new("packs", "Solution Packs", "Starting configurations and trusted publishers.",
        [
            new("packs", "/Admin/Packs", "[[solution.pack|plural|sentence]]", "Installed packs and who may publish them.", ["packs.read"]),
        ]),
        new("security", "Security & Audit", "Evidence of what happened.",
        [
            new("audit", "/Admin/Audit", "Audit events", "Who did what, and when.", ["audit.read", "audit.read_admin"]),
            new("elevations", "/Admin/Elevations", "Support & emergency access log", "Every time an installation administrator was given access to this tenant.", ["audit.read_admin"]),
            new("scanning", "/Admin/Documents/Scanning", "Document scanning", "Files held, scanned or quarantined.", ["notifications.administer"]),
        ]),
        new("operations", "Operations", "Health, backups, upgrades and installation set-up.",
        [
            new("health", "/Admin/Health", "Health and readiness", "Is the installation ready to use.", ["ops.manage"]),
            new("operations", "/Admin/Operations", "Operations", "Backups, restore, upgrades and evidence.", ["operations.read"]),
            new("support", "/Admin/Support", "Support access", "Request time-limited access to a tenant.", ["support.elevate"]),
            new("setup", "/Admin/Setup", "Installation setup", "Re-open parts of first-run set-up.", ["setup.run"]),
        ]),
    ];
}

/// <summary>Builds the navigation for a person from what they may do. Pure: the permission and capability tests are passed in, so it is easy to test for every role.</summary>
public static class NavigationBuilder
{
    public static async Task<NavigationModel> BuildAsync(Func<string[], Task<bool>> can, Func<string, bool> uses, int unread)
    {
        async Task<bool> Visible(string[] permissions, string? capability) => (capability is null || uses(capability)) && (permissions.Length == 0 || await can(permissions));

        var queues = new List<ActionQueue>();
        foreach (var q in NavigationCatalogue.Queues) if (await Visible(q.AnyPermission, q.Capability)) queues.Add(q);

        var primary = new List<NavItem>();
        foreach (var e in NavigationCatalogue.Everyday)
        {
            if (!await Visible(e.AnyPermission, e.Capability)) continue;
            primary.Add(new NavItem(e.Key, e.Href, e.Title, e.MatchPrefix));
            if (e.Key == "contracts" && queues.Count > 0) primary.Add(new NavItem("actions", "/Actions", "My Actions", "/Actions"));
        }
        // My Actions sits after Contracts even for a person who cannot read contracts (for example a configuration reviewer).
        if (queues.Count > 0 && primary.All(i => i.Key != "actions")) primary.Insert(Math.Min(1, primary.Count), new NavItem("actions", "/Actions", "My Actions", "/Actions"));

        var sections = new List<AdminSection>();
        foreach (var g in NavigationCatalogue.Administration)
        {
            var items = new List<NavItem>();
            foreach (var e in g.Entries) if (await Visible(e.AnyPermission, e.Capability)) items.Add(new NavItem(e.Key, e.Href, e.Title, e.MatchPrefix));
            if (items.Count > 0) sections.Add(new AdminSection(g.Key, g.Title, g.Description, items));
        }
        return new NavigationModel(primary, sections, queues, unread);
    }
}
