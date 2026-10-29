using System.Net;
using System.Text.RegularExpressions;
using Ecp.Host.Navigation;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Identity.Access;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// UX-1 (docs/UX_REDESIGN.md sections 3 and 4): the everyday navigation is short, every administration page is grouped under one Administration entry, nothing that existed is lost,
/// and Home puts the person's work first. Visibility is presentation: these tests also prove the pages still refuse a person who may not use them.
/// </summary>
public sealed class NavigationBuilderTests
{
    private static readonly PermissionMatrix Matrix = PermissionMatrix.Instance;

    private static Func<string[], Task<bool>> HoldsAsRole(string role) => permissions => Task.FromResult(permissions.Any(p => Matrix.Of(role, p) != Grant.None));
    private static readonly Func<string[], Task<bool>> HoldsEverything = _ => Task.FromResult(true);

    [Fact]
    public async Task Everyday_navigation_is_short_for_every_role_and_administration_is_one_entry()
    {
        foreach (var role in RoleCodes.All)
        {
            var nav = await NavigationBuilder.BuildAsync(HoldsAsRole(role), _ => true, unread: 0);
            Assert.True(nav.Primary.Count <= 6, $"{role}: {nav.Primary.Count} everyday links");
            Assert.True(nav.Administration.Count <= 9, $"{role}: {nav.Administration.Count} administration groups");
            Assert.Contains(nav.Primary, i => i.Key == "home");
            Assert.DoesNotContain(nav.Primary, i => i.Href.StartsWith("/Admin", StringComparison.Ordinal));      // nothing administrative sits beside everyday work
        }
    }

    [Theory]
    [InlineData(RoleCodes.Viewer)]
    [InlineData(RoleCodes.Legal)]
    [InlineData(RoleCodes.CategoryOwner)]
    public async Task A_person_who_only_does_everyday_work_sees_no_administration(string role)
    {
        var nav = await NavigationBuilder.BuildAsync(HoldsAsRole(role), _ => true, 0);
        // Category owners and viewers use the categories page to look, but that is reached from Reports; it is not an administration entry for them.
        Assert.False(nav.HasAdministration, $"{role} sees: {string.Join(", ", nav.Administration.SelectMany(s => s.Items).Select(i => i.Href))}");
    }

    [Theory]
    [InlineData(RoleCodes.Approver)]
    [InlineData(RoleCodes.BackupApprover)]
    public async Task An_approver_sees_only_the_read_only_approval_matrix_under_administration(string role)
    {
        var nav = await NavigationBuilder.BuildAsync(HoldsAsRole(role), _ => true, 0);
        Assert.Equal(["/Admin/Approvals"], nav.Administration.SelectMany(s => s.Items).Select(i => i.Href));
    }

    [Theory]
    [InlineData(RoleCodes.TenantAdministrator)]
    [InlineData(RoleCodes.OrganisationAdministrator)]
    [InlineData(RoleCodes.SystemAdministrator)]
    [InlineData(RoleCodes.BusinessConfigurationAdministrator)]
    public async Task An_administrator_sees_the_administration_entry(string role)
    {
        var nav = await NavigationBuilder.BuildAsync(HoldsAsRole(role), _ => true, 0);
        Assert.True(nav.HasAdministration, role);
    }

    /// <summary>The 31 navigation entries that existed before the redesign, and where each one is now. Nothing may be lost.</summary>
    private static readonly (string Old, string Now)[] Legacy =
    [
        ("/", "/"), ("/Dashboards", "/Reports"), ("/Contracts", "/Contracts"), ("/Legal", "/Actions"), ("/Approvals", "/Actions"), ("/Variations", "/Actions"), ("/Renewals", "/Actions"),
        ("/Notifications", "bell"), ("/Suppliers", "/Suppliers"), ("/Admin/Categories", "/Admin/Categories"), ("/Admin/Users", "/Admin/Users"), ("/Admin/Organisations", "/Admin/Organisations"),
        ("/Admin/ApproverGroups", "/Admin/ApproverGroups"), ("/Admin/Audit", "/Admin/Audit"), ("/Onboarding", "/Onboarding"), ("/Migration", "/Migration"), ("/Admin/Support", "/Admin/Support"),
        ("/Admin/Elevations", "/Admin/Elevations"), ("/Admin/Notifications", "/Admin/Notifications"), ("/Admin/Approvals", "/Admin/Approvals"), ("/Admin/Reviews", "/Admin/Reviews"),
        ("/Admin/Configuration", "/Admin/Configuration"), ("/Admin/Studio", "/Admin/Studio"), ("/Admin/Health", "/Admin/Health"), ("/Admin/Operations", "/Admin/Operations"),
        ("/Admin/Documents/Scanning", "/Admin/Documents/Scanning"), ("/Admin/Capabilities", "/Admin/Capabilities"), ("/Admin/Terminology", "/Admin/Terminology"),
        ("/Admin/Regional", "/Admin/Regional"), ("/Admin/Packs", "/Admin/Packs"), ("/Admin/Setup", "/Admin/Setup"),
    ];

    [Fact]
    public async Task Every_pre_redesign_navigation_entry_still_has_a_place()
    {
        Assert.Equal(31, Legacy.Length);
        var nav = await NavigationBuilder.BuildAsync(HoldsEverything, _ => true, 0);
        var placed = nav.Primary.Select(i => i.Href).Concat(nav.Administration.SelectMany(s => s.Items).Select(i => i.Href)).Concat(nav.Queues.Select(q => q.Href)).ToHashSet();
        foreach (var (old, now) in Legacy)
        {
            if (now == "bell") continue;                                    // the notifications indicator is rendered by the layout, not listed
            Assert.True(placed.Contains(now) || (now == "/Actions" && nav.Queues.Any(q => q.Href == old)), $"{old} -> {now} is not reachable from the navigation");
        }
        Assert.Contains(nav.Primary, i => i.Href == "/Actions");
    }

    [Fact]
    public async Task A_capability_that_is_off_removes_its_queue_and_its_menu_entry()
    {
        var on = await NavigationBuilder.BuildAsync(HoldsEverything, _ => true, 0);
        var off = await NavigationBuilder.BuildAsync(HoldsEverything, c => c != Ecp.SharedKernel.CapabilityKeys.Variations && c != Ecp.SharedKernel.CapabilityKeys.RegisterAuthorisation, 0);
        Assert.Contains(on.Queues, q => q.Key == "variations");
        Assert.DoesNotContain(off.Queues, q => q.Key == "variations");
        Assert.DoesNotContain(off.Queues, q => q.Key == "approvals");
        Assert.DoesNotContain(off.Administration.SelectMany(s => s.Items), i => i.Href == "/Admin/Approvals");
    }

    [Fact]
    public async Task Nothing_in_the_catalogue_is_listed_twice_in_the_menu()
    {
        var nav = await NavigationBuilder.BuildAsync(HoldsEverything, _ => true, 0);
        var all = nav.Administration.SelectMany(s => s.Items).Select(i => i.Href).ToList();
        // Configuration reviews is both a menu entry and a My Actions queue by design; no page is listed twice inside the menu.
        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

[Collection("Api")]
[Trait("Category", "Api")]
public sealed class NavigationAndHomeUiTests
{
    private const string TenantAdmin = "tenantadmin@a.example.test";
    private const string ProcAdmin = "procadmin@a.example.test";
    private const string CatOwner = "catowner.packaging@a.example.test";
    private const string Viewer = "viewer.all@a.example.test";

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> UserWithRoleAsync(DemoApp app, string role, bool ownsNothing)
    {
        var (tenant, _) = await app.FindUserAsync(TenantAdmin);
        var email = $"nav.{Guid.NewGuid():N}"[..20] + "@a.example.test";
        await app.WithTenantAsync(tenant, async sp =>
        {
            var org = (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).First().Id;
            var id = await sp.GetRequiredService<IUserService>().CreateActiveTrustedAsync("Nav " + role, email, Ecp.Host.Security.DemoIdentityService.ProviderKey, Ecp.Host.Security.DemoIdentityService.Issuer, email, "test");
            await sp.GetRequiredService<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, null, ScopeSet.ForOrganisations([org], "Internal"), null, EffectiveFromUtc: DateTime.UtcNow.AddDays(-1), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
            return 0;
        });
        _ = ownsNothing;
        return email;
    }

    [Fact]
    public async Task Home_offers_New_contract_only_to_a_person_who_can_start_one_and_otherwise_says_why()
    {
        using var app = DemoApp.Start();
        using var proc = await app.SignInAsync(ProcAdmin, followRedirects: true);
        var procHome = await BodyAsync(proc, "/");
        Assert.Contains("href=\"/Contracts/Create\"", procHome);                       // a Procurement Administrator may start one anywhere
        Assert.DoesNotContain("id=\"cannot-h\"", procHome);

        using var owner = await app.SignInAsync(CatOwner, followRedirects: true);
        Assert.Contains("href=\"/Contracts/Create\"", await BodyAsync(owner, "/"));     // a Category Owner who owns a category may

        // A Category Owner who owns nothing holds the role but cannot start a contract: Home says what is missing instead of offering a button that fails.
        var emptyOwner = await UserWithRoleAsync(app, RoleCodes.CategoryOwner, ownsNothing: true);
        using var empty = await app.SignInAsync(emptyOwner, followRedirects: true);
        var emptyHome = await BodyAsync(empty, "/");
        Assert.DoesNotContain("href=\"/Contracts/Create\"", emptyHome);
        Assert.Contains("do not own any", emptyHome);
        Assert.Contains("href=\"/Access\"", emptyHome);

        using var tenantAdmin = await app.SignInAsync(TenantAdmin, followRedirects: true);
        var adminHome = await BodyAsync(tenantAdmin, "/");
        Assert.DoesNotContain("href=\"/Contracts/Create\"", adminHome);
        Assert.Contains("id=\"cannot-h\"", adminHome);
        Assert.Contains("Setup &amp; administration", adminHome);                       // administrators get their own, quieter card
    }

    [Fact]
    public async Task Home_shows_the_work_cards_to_people_who_do_contract_work_and_the_setup_card_only_to_administrators()
    {
        using var app = DemoApp.Start();
        using var owner = await app.SignInAsync(CatOwner, followRedirects: true);
        var home = await BodyAsync(owner, "/");
        Assert.Contains("Awaiting My Action", home);
        Assert.Contains("Expiring Soon", home);
        Assert.Contains("Reviews Due", home);
        Assert.DoesNotContain("Setup &amp; administration", home);                      // no administration card for a person who administers nothing
        Assert.DoesNotContain("admin-menu", home);

        using var viewer = await app.SignInAsync(Viewer, followRedirects: true);
        Assert.DoesNotContain("href=\"/Contracts/Create\"", await BodyAsync(viewer, "/"));
    }

    [Fact]
    public async Task The_main_menu_has_the_everyday_links_a_notifications_indicator_and_a_single_Administration_menu_for_administrators()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(TenantAdmin, followRedirects: true);
        var html = await BodyAsync(admin, "/");
        var nav = Regex.Match(html, "<nav aria-label=\"Main\">.*?</nav>", RegexOptions.Singleline).Value;
        var topLevel = Regex.Matches(Regex.Replace(nav, "<details.*?</details>", "", RegexOptions.Singleline), "<li><a ").Count;
        Assert.True(topLevel <= 6, $"{topLevel} top-level links");
        Assert.Contains("id=\"admin-menu\"", nav);
        Assert.Contains("<summary>Administration</summary>", nav);
        Assert.Contains("href=\"/Admin\"", nav);
        Assert.Contains("class=\"bell\"", html);
        Assert.Contains("/js/nav.js", html);
        // The old twenty links are inside the one menu, grouped.
        Assert.Contains("Support &amp; emergency access log", nav);
        Assert.DoesNotContain(">Access to this tenant<", nav);
    }

    [Fact]
    public async Task The_Administration_landing_page_lists_only_what_the_person_may_use_and_can_be_searched()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(TenantAdmin, followRedirects: true);
        var landing = await BodyAsync(admin, "/Admin");
        Assert.Contains("People &amp; Access", landing);
        Assert.Contains("href=\"/Admin/Users\"", landing);
        Assert.Contains("href=\"/Admin/Elevations\"", landing);
        static string Main(string html) => Regex.Match(html, "<main.*?</main>", RegexOptions.Singleline).Value;      // the menu in the header lists everything; the page lists the matches
        var found = Main(await BodyAsync(admin, "/Admin?q=terminology"));
        Assert.Contains("href=\"/Admin/Terminology\"", found);
        Assert.DoesNotContain("href=\"/Admin/Audit\"", found);
        var none = Main(await BodyAsync(admin, "/Admin?q=zzzznothing"));
        Assert.Contains("No administration page matches", none);

        // Everyone else is refused: hiding is cosmetic, the page checks.
        using var viewer = await app.SignInAsync(Viewer, followRedirects: true);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin")).StatusCode);
        using var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin")).StatusCode);
    }

    [Fact]
    public async Task Administration_pages_show_a_breadcrumb_and_the_support_access_log_says_what_it_is()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(TenantAdmin, followRedirects: true);
        var page = await BodyAsync(admin, "/Admin/Elevations");
        Assert.Contains("class=\"breadcrumb\"", page);
        Assert.Contains("Security &amp; Audit", page);
        Assert.Contains("Support &amp; emergency access log", page);
        Assert.Contains("Every time an installation administrator has been given access to this tenant", page);
    }

    [Fact]
    public async Task My_Actions_and_Reports_open_for_the_people_they_are_meant_for_and_every_old_page_still_opens()
    {
        using var app = DemoApp.Start();
        using var proc = await app.SignInAsync(ProcAdmin, followRedirects: true);
        var actions = await BodyAsync(proc, "/Actions");
        Assert.Contains("Awaiting my action", actions);
        var reports = await BodyAsync(proc, "/Reports");
        Assert.Contains("href=\"/Dashboards\"", reports);
        Assert.Contains("not a reporting tool", reports);

        // The existing URLs keep working for the role that could use them before.
        using var admin = await app.SignInAsync(TenantAdmin, followRedirects: true);
        foreach (var url in new[] { "/Admin/Users", "/Admin/Organisations", "/Admin/ApproverGroups", "/Admin/Audit", "/Admin/Notifications", "/Admin/Reviews",
                     "/Admin/Studio", "/Admin/Capabilities", "/Admin/Terminology", "/Admin/Regional", "/Admin/Packs", "/Admin/Elevations", "/Access" })
        {
            var response = await admin.GetAsync(url);
            // The page still exists. Whether this administrator may use it is the page's own decision (a tenant administrator holds some configuration rights only through an organisation).
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden, $"{url} -> {(int)response.StatusCode}");
        }
    }
}
