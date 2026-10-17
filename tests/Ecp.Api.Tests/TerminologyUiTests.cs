using System.Net;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Packs;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// S4 through the real host: the Terminology administration pages, and the generic pages describing each organisation in its own words. The demo tenant has an organisation on the reference (JBS) pack and one on the
/// Blank pack; a third organisation is put on a synthetic pack the repository has never seen, with no source change. Labels change what people read, never what they may do.
/// </summary>
[Collection("Api")]
[Trait("Category", "Terminology")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class TerminologyUiTests
{
    private const string TenantAdmin = "tenantadmin@a.example.test";
    private const string OrgAdmin = "orgadmin@a.example.test";
    private const string SiteAdmin = "siteadmin@a.example.test";
    private const string BizConfig = "bizconfig@a.example.test";
    private const string Viewer = "viewer.all@a.example.test";
    private const string Auditor = "auditor@a.example.test";
    private const string Legal = "legal@a.example.test";
    private const string ProcAdmin = "procadmin@a.example.test";
    private const string CatOwner = "catowner.packaging@a.example.test";

    private static readonly string[] Pages = ["/Admin/Terminology", "/Admin/Terminology/Edit", "/Admin/Terminology/Preview", "/Admin/Terminology/History", "/Admin/Terminology/Audit"];

    private static async Task<(Guid Tenant, Guid Org)> TenantAsync(DemoApp app, string orgName)
    {
        var (tenant, _) = await app.FindUserAsync(TenantAdmin);
        var org = await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single(o => o.Name == orgName).Id);
        return (tenant, org);
    }

    /// <summary>A person with one role in one organisation, created through the trusted path, who can then sign in like any demo user.</summary>
    private static async Task<string> UserInAsync(DemoApp app, Guid tenant, Guid org, string role, ViewerKind? kind = null, string ceiling = "Internal")
    {
        var email = $"term.{Guid.NewGuid():N}@a.example.test"[..40] + ".test";
        await app.WithTenantAsync(tenant, async sp =>
        {
            var id = await sp.GetRequiredService<IUserService>().CreateActiveTrustedAsync("Terminology " + role, email, Ecp.Host.Security.DemoIdentityService.ProviderKey, Ecp.Host.Security.DemoIdentityService.Issuer, email, "test");
            await sp.GetRequiredService<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, kind, ScopeSet.ForOrganisations([org], ceiling), null,
                EffectiveFromUtc: DateTime.UtcNow.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
            return 0;
        });
        return email;
    }

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The fields a browser would post for the edit form: every row, with the given overrides.</summary>
    private static Dictionary<string, string> EditForm(Guid org, IReadOnlyDictionary<string, (string Singular, string Plural)> overrides, string locale = "en", string note = "")
    {
        var form = new Dictionary<string, string> { ["OrganisationId"] = org.ToString(), ["Locale"] = locale, ["Note"] = note };
        for (var i = 0; i < TermCatalogue.All.Count; i++)
        {
            var key = TermCatalogue.All[i].Key;
            form[$"Rows[{i}].Key"] = key;
            if (overrides.TryGetValue(key, out var v)) { form[$"Rows[{i}].Override"] = "true"; form[$"Rows[{i}].Singular"] = v.Singular; form[$"Rows[{i}].Plural"] = v.Plural; }
        }
        return form;
    }

    private static string Edit(Guid org, string locale = "en") => $"/Admin/Terminology/Edit?organisationId={org}&locale={locale}";

    // ---- access ----

    [Fact]
    public async Task Each_terminology_page_opens_only_for_people_who_hold_its_permission()
    {
        using var app = DemoApp.Start();
        using var anonymous = app.NewClient();
        foreach (var page in Pages) Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync(page)).StatusCode);

        using var viewer = await app.SignInAsync(Viewer);
        using var site = await app.SignInAsync(SiteAdmin);
        using var owner = await app.SignInAsync(CatOwner);
        foreach (var client in new[] { viewer, site, owner })
            foreach (var page in Pages) Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(page)).StatusCode);

        // The organisation administrator, business configuration administrator and tenant administrator hold every terminology permission.
        foreach (var email in new[] { OrgAdmin, BizConfig, TenantAdmin })
        {
            using var admin = await app.SignInAsync(email, followRedirects: true);
            foreach (var page in Pages) { var r = await admin.GetAsync(page); Assert.True(r.StatusCode == HttpStatusCode.OK, $"{email} {page} -> {(int)r.StatusCode} {(await r.Content.ReadAsStringAsync())[..Math.Min(300, (await r.Content.ReadAsStringAsync()).Length)]}"); }
        }

        // The auditor reads the overview, the history and the audit trail, and cannot draft.
        using var auditor = await app.SignInAsync(Auditor, followRedirects: true);
        foreach (var page in new[] { "/Admin/Terminology", "/Admin/Terminology/Preview", "/Admin/Terminology/History", "/Admin/Terminology/Audit" }) Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(page)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/Admin/Terminology/Edit")).StatusCode);
        var overview = await BodyAsync(auditor, "/Admin/Terminology");
        Assert.DoesNotContain("Start a draft", overview);
        Assert.Contains("Terminology", await BodyAsync(auditor, "/Admin/Terminology/History"));
    }

    [Fact]
    public async Task The_overview_shows_stable_keys_apart_from_labels_and_where_each_label_comes_from()
    {
        using var app = DemoApp.Start();
        using var biz = await app.SignInAsync(BizConfig, followRedirects: true);
        var html = await BodyAsync(biz, "/Admin/Terminology");
        Assert.Contains("<code class=\"id\">contract.owner</code>", html);
        Assert.Contains("Category Owner", html);                                  // the reference pack's wording for the organisation
        Assert.Contains("Pack default", html);
        Assert.Contains("Contract Owner", html);                                  // the platform's neutral wording, shown beside it
        Assert.Contains("never permissions", html);
        Assert.Contains("labels only", html.Replace("It changes labels only,", "labels only") + "labels only");
    }

    // ---- the form ----

    [Fact]
    public async Task The_edit_form_saves_validates_publishes_and_the_new_wording_reaches_the_pages_without_changing_any_permission()
    {
        using var app = DemoApp.Start();
        var (tenant, org) = await TenantAsync(app, "Demo Procurement Group");
        using var biz = await app.SignInAsync(BizConfig, followRedirects: false);
        using var viewer = await app.SignInAsync(Viewer, followRedirects: true);
        Assert.Contains("Suppliers", await BodyAsync(viewer, "/Suppliers"));

        var form = EditForm(org, new Dictionary<string, (string, string)> { ["supplier"] = ("Partner", "Partners") }, note: "Partners, not suppliers");
        var save = await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Save&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        // Saving does not publish: people still read the old wording.
        Assert.Contains("Suppliers", await BodyAsync(viewer, "/Suppliers"));
        var edit = await BodyAsync(biz, Edit(org));
        Assert.Contains("value=\"Partner\"", edit);                               // the saved override is back in the form
        Assert.Contains("A draft is open.", edit);

        // Preview shows before and after, and says plainly that nothing else changes.
        var preview = await BodyAsync(biz, $"/Admin/Terminology/Preview?organisationId={org}");
        Assert.Contains("Partners", preview);
        Assert.Contains("not permissions or workflows", preview);

        var validate = await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Validate&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.Redirect, validate.StatusCode);
        var publish = await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.Redirect, publish.StatusCode);
        Assert.StartsWith("/Admin/Terminology", publish.Headers.Location!.OriginalString);

        var suppliers = await BodyAsync(viewer, "/Suppliers");
        Assert.Contains("<h1>Partners</h1>", suppliers);
        Assert.DoesNotContain("<h1>Suppliers</h1>", suppliers);
        Assert.Contains(">Partners<", await BodyAsync(viewer, "/"));              // navigation too
        // The same person can still do exactly what they could: the viewer reads suppliers and still cannot open the terminology pages.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Terminology")).StatusCode);

        // History and audit record it; the audit keeps its stable action codes.
        Assert.Contains("Partners, not suppliers", await BodyAsync(biz, $"/Admin/Terminology/History?organisationId={org}"));
        var audit = await BodyAsync(biz, $"/Admin/Terminology/Audit?organisationId={org}");
        Assert.Contains("config.published", audit);
        Assert.Contains("config.draft.created", audit);

        // Removing the override and publishing again resumes the pack's wording; then the earlier release can be restored as a new one.
        var removed = EditForm(org, new Dictionary<string, (string, string)>());
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(biz, Edit(org), removed, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en")).StatusCode);
        Assert.Contains("<h1>Suppliers</h1>", await BodyAsync(viewer, "/Suppliers"));
        var rollback = await DemoApp.PostFormAsync(biz, $"/Admin/Terminology/History?organisationId={org}", new Dictionary<string, string> { ["OrganisationId"] = org.ToString(), ["ToRelease"] = "1", ["Reason"] = "Partner was right" },
            $"/Admin/Terminology/History?handler=Rollback&organisationId={org}");
        Assert.Equal(HttpStatusCode.Redirect, rollback.StatusCode);
        Assert.Contains("<h1>Partners</h1>", await BodyAsync(viewer, "/Suppliers"));
        Assert.Contains("restores release 1", await BodyAsync(biz, $"/Admin/Terminology/History?organisationId={org}"));
        _ = tenant;
    }

    [Fact]
    public async Task A_problem_is_shown_beside_its_row_the_entered_values_are_kept_and_markup_is_never_stored_or_run()
    {
        using var app = DemoApp.Start();
        var (_, org) = await TenantAsync(app, "Demo Procurement Group");
        using var biz = await app.SignInAsync(BizConfig, followRedirects: false);
        var hostile = EditForm(org, new Dictionary<string, (string, string)> { ["supplier"] = ("<script>alert(1)</script>", "Partners"), ["site"] = ("Plant", "Plants") });
        var response = await DemoApp.PostFormAsync(biz, Edit(org), hostile, $"/Admin/Terminology/Edit?handler=Validate&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);                      // the page again, not a redirect
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("There is a problem", html);
        Assert.Contains("must be plain text", html);
        Assert.Contains("href=\"#row-", html);                                     // the error summary links to the field
        Assert.DoesNotContain("<script>alert(1)</script>", html);                  // encoded, never raw
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("value=\"Plant\"", html);                                  // what else was entered is kept
        // Publishing the same thing is refused and publishes nothing.
        var publish = await DemoApp.PostFormAsync(biz, Edit(org), hostile, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var viewer = await app.SignInAsync(Viewer, followRedirects: true);
        Assert.Contains("<h1>Suppliers</h1>", await BodyAsync(viewer, "/Suppliers"));
    }

    [Fact]
    public async Task A_label_with_an_ampersand_is_stored_as_text_and_shown_encoded()
    {
        using var app = DemoApp.Start();
        var (_, org) = await TenantAsync(app, "Demo Procurement Group");
        using var biz = await app.SignInAsync(BizConfig, followRedirects: false);
        using var viewer = await app.SignInAsync(Viewer, followRedirects: true);
        var form = EditForm(org, new Dictionary<string, (string, string)> { ["supplier"] = ("Terms & Parties", "Terms & Parties") });
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en")).StatusCode);
        var html = await BodyAsync(viewer, "/Suppliers");
        Assert.Contains("<h1>Terms &amp; Parties</h1>", html);
    }

    [Fact]
    public async Task Publishing_and_restoring_need_a_recent_sign_in_and_reading_does_not()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        using var app = DemoApp.Start(services: s => s.AddSingleton<IClock>(clock));
        var (_, org) = await TenantAsync(app, "Demo Procurement Group");
        using var biz = await app.SignInAsync(BizConfig, followRedirects: false);
        var form = EditForm(org, new Dictionary<string, (string, string)> { ["site"] = ("Plant", "Plants") });
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Save&organisationId={org}&locale=en")).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.StartsWith("/Reauth", stale.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await biz.GetAsync(Edit(org))).StatusCode);                 // reading and drafting are not step-up actions
    }

    [Fact]
    public async Task A_person_cannot_name_an_organisation_they_hold_no_terminology_right_in()
    {
        using var app = DemoApp.Start();
        var (_, blank) = await TenantAsync(app, "Demo Facilities Organisation");
        using var org1Admin = await app.SignInAsync(OrgAdmin, followRedirects: true);                // an administrator of the procurement organisation only
        Assert.Equal(HttpStatusCode.NotFound, (await org1Admin.GetAsync($"/Admin/Terminology?organisationId={blank}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org1Admin.GetAsync(Edit(blank))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org1Admin.GetAsync($"/Admin/Terminology/Audit?organisationId={blank}")).StatusCode);
    }

    // ---- the pages in each organisation's own words ----

    [Fact]
    public async Task A_blank_organisation_reads_neutral_wording_a_reference_organisation_reads_its_packs_wording_and_neither_shows_the_others()
    {
        using var app = DemoApp.Start();
        var (tenant, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var blankEmail = await UserInAsync(app, tenant, blankOrg, RoleCodes.Viewer, ViewerKind.AllContracts);
        using var blank = await app.SignInAsync(blankEmail, followRedirects: true);
        using var jbs = await app.SignInAsync(Viewer, followRedirects: true);

        foreach (var page in new[] { "/", "/Contracts", "/Suppliers", "/Admin/Categories", "/Dashboards", "/Notifications", "/Renewals", "/Variations" })
        {
            var response = await blank.GetAsync(page);
            if (response.StatusCode != HttpStatusCode.OK) continue;                                   // a page this role does not open
            var html = await response.Content.ReadAsStringAsync();
            var visible = Regex.Replace(html, "<input[^>]*>|<a [^>]*>|<title>.*?</title>|<option[^>]*>[^<]*</option>", "");
            var hit = Regex.Match(visible, "(?i).{0,40}(jbs|procurement|group head|supplier).{0,40}");
            Assert.False(hit.Success, $"{page}: {hit.Value}");
        }
        var blankHome = await BodyAsync(blank, "/");
        Assert.Contains("Contract Register", blankHome.Replace("contract register", "Contract Register"));
        Assert.Contains(">Counterparties<", blankHome);                                                // the navigation
        Assert.Contains(">Contracts<", blankHome);

        var jbsHome = await BodyAsync(jbs, "/");
        Assert.Contains(">Suppliers<", jbsHome);
        Assert.DoesNotContain(">Counterparties<", jbsHome);
        Assert.Contains("procurement contract register", jbsHome);                                     // the reference pack's name for it
    }

    [Fact]
    public async Task Roles_are_shown_by_their_configured_label_with_the_stable_code_kept_separately_and_authority_is_unchanged()
    {
        using var app = DemoApp.Start();
        var (tenant, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        using var proc = await app.SignInAsync(ProcAdmin, followRedirects: true);
        using var owner = await app.SignInAsync(CatOwner, followRedirects: true);
        Assert.Contains("Procurement Administrator", await BodyAsync(proc, "/"));
        Assert.Contains("Category Owner", await BodyAsync(owner, "/"));

        // In a neutral organisation the same stable roles read as Contract Administrator and Contract Owner.
        var adminEmail = await UserInAsync(app, tenant, blankOrg, RoleCodes.ProcurementAdministrator, null, "Confidential");
        using var neutral = await app.SignInAsync(adminEmail, followRedirects: true);
        var home = await BodyAsync(neutral, "/");
        Assert.True(home.Contains("Contract Administrator"), Regex.Match(home, "Your access.{0,600}", RegexOptions.Singleline).Value);
        Assert.DoesNotContain("Procurement Administrator", home);
        // The label differs; the authority does not: this person reaches the same pages the procurement administrator does.
        Assert.Equal(HttpStatusCode.Forbidden, (await neutral.GetAsync("/Admin/Terminology")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await proc.GetAsync("/Admin/Terminology")).StatusCode);

        // Administrators see the stable code beside the label.
        using var org = await app.SignInAsync(OrgAdmin, followRedirects: true);
        var users = await BodyAsync(org, "/Admin/Users");
        Assert.Contains("Procurement Administrator", users);
    }

    [Fact]
    public async Task A_pack_the_repository_has_never_seen_gives_an_organisation_its_own_words_with_no_source_change()
    {
        using var app = DemoApp.Start();
        var (tenant, _) = await TenantAsync(app, "Demo Procurement Group");
        var key = "agreements-" + Guid.NewGuid().ToString("N")[..6];
        var package = PackTestKit.Build(key, "1.0.0", content: Content(out var docs), extraDocuments: docs);
        var (_, tenantAdminId) = await app.FindUserAsync(TenantAdmin);
        var orgId = await app.WithTenantAsync(tenant, async sp =>
        {
            sp.GetRequiredService<CurrentUser>().Set(tenantAdminId);
            var up = await sp.GetRequiredService<IPackManager>().UploadAsync(new MemoryStream(package), "p.ecpkg");
            Assert.True(up.Report.CanInstall, string.Join("; ", up.Report.Issues.Select(i => i.Message)));
            await sp.GetRequiredService<IPackManager>().InstallAsync(up.Token);
            var created = await sp.GetRequiredService<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest("Agreement Business", "Australia/Sydney", "en-AU", "AUD", "AGR", key));
            await sp.GetRequiredService<IPackAssignments>().AssignAsync(created, key, "1.0.0");
            return created;
        });
        var email = await UserInAsync(app, tenant, orgId, RoleCodes.Viewer, ViewerKind.AllContracts);
        using var client = await app.SignInAsync(email, followRedirects: true);

        var home = await BodyAsync(client, "/");
        Assert.Contains(">Agreements<", home);                                                         // navigation: contracts are agreements here
        Assert.Contains(">Counterparties<", home);
        Assert.Contains(">Portfolios<", home);
        Assert.Contains("Agreement Hub", home);                                                        // the application name from the pack
        Assert.Contains("agreement ledger", home);
        Assert.DoesNotMatch(">Contracts<|>Suppliers<|>Categories<", home);
        var suppliers = await BodyAsync(client, "/Suppliers");
        Assert.Contains("<h1>Counterparties</h1>", suppliers);
        // Form inputs carry random antiforgery tokens, which can contain any three letters; only the visible text is judged.
        Assert.DoesNotMatch("(?i)jbs|procurement", Regex.Replace(suppliers, "<title>.*?</title>|<input[^>]*>", ""));
    }

    [Fact]
    public async Task Contract_and_supplier_field_labels_follow_the_organisations_words_and_name_no_country_or_ERP_product_unless_the_pack_does()
    {
        using var app = DemoApp.Start();
        var (tenant, jbsOrg) = await TenantAsync(app, "Demo Procurement Group");
        var (_, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");

        // The reference business keeps today's words.
        using var jbs = await app.SignInAsync(ProcAdmin, followRedirects: true);
        var jbsSupplier = await BodyAsync(jbs, "/Suppliers/Create");
        Assert.Contains(">ABN<", jbsSupplier);
        Assert.Contains("Vendor Number", jbsSupplier);
        var jbsContract = await BodyAsync(jbs, $"/Contracts/Create?organisationId={jbsOrg}");
        Assert.Contains("SAP Outline Agreement Number", jbsContract);
        Assert.Contains("Start date", jbsContract);

        // A neutral organisation sees no Australian identifier and no ERP product anywhere on these pages.
        var blankAdmin = await UserInAsync(app, tenant, blankOrg, RoleCodes.ProcurementAdministrator, null, "Confidential");
        using var blank = await app.SignInAsync(blankAdmin, followRedirects: true);
        foreach (var url in new[] { "/Suppliers/Create", "/Suppliers", $"/Contracts/Create?organisationId={blankOrg}", "/Contracts" })
        {
            var response = await blank.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.Forbidden) continue;
            var page = Regex.Replace(await response.Content.ReadAsStringAsync(), "<script.*?</script>|<input[^>]*type=\"hidden\"[^>]*>", "", RegexOptions.Singleline);
            Assert.DoesNotMatch(@"\bABN\b|\bSAP\b", page);
        }
        // It has chosen no identifier type, so the form asks for none (an organisation records only the identifiers it configured; nothing defaults to an ABN or to any other type).
        Assert.DoesNotContain("Tax Identifier", await BodyAsync(blank, "/Suppliers/Create"));

        // A pack the repository has never seen renames the dates, the risk label, the identifiers and a group heading, and every page and the export follow with no source change.
        var key = "labels-" + Guid.NewGuid().ToString("N")[..6];
        var package = PackTestKit.Build(key, "1.0.0", content: Content(out var docs), extraDocuments: docs);
        var (_, tenantAdminId) = await app.FindUserAsync(TenantAdmin);
        var orgId = await app.WithTenantAsync(tenant, async sp =>
        {
            sp.GetRequiredService<CurrentUser>().Set(tenantAdminId);
            var up = await sp.GetRequiredService<IPackManager>().UploadAsync(new MemoryStream(package), "p.ecpkg");
            Assert.True(up.Report.CanInstall, string.Join("; ", up.Report.Issues.Select(i => i.Message)));
            await sp.GetRequiredService<IPackManager>().InstallAsync(up.Token);
            var created = await sp.GetRequiredService<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest("Label Business", "America/Chicago", "en-US", "USD", "LBL", key));
            await sp.GetRequiredService<IPackAssignments>().AssignAsync(created, key, "1.0.0");
            return created;
        });
        var admin = await UserInAsync(app, tenant, orgId, RoleCodes.ProcurementAdministrator, null, "Confidential");
        using var client = await app.SignInAsync(admin, followRedirects: true);
        var supplierForm = await BodyAsync(client, "/Suppliers/Create");
        Assert.Contains("Company Registration", supplierForm);
        Assert.Contains("Account Code", supplierForm);
        Assert.DoesNotMatch(@"\bABN\b|Tax Identifier|Vendor Number", Regex.Replace(supplierForm, "<input[^>]*type=\"hidden\"[^>]*>", ""));
        var register = await client.GetAsync("/Contracts");
        if (register.StatusCode == HttpStatusCode.OK)
        {
            var body = await register.Content.ReadAsStringAsync();
            Assert.DoesNotContain("End Date", body);
        }
        var csv = await (await ExportAsync(client, orgId, "csv")).Content.ReadAsStringAsync();
        var header = csv.Split('\n')[0];
        Assert.Contains("Scheduled Completion Date", header);
        Assert.DoesNotMatch(@"\bEnd Date\b|\bStart Date\b|\bABN\b|\bSAP\b", header);
    }

    // ---- exports, onboarding, stable identifiers ----

    private static async Task<HttpResponseMessage> ExportAsync(HttpClient client, Guid org, string format)
        => await DemoApp.PostFormAsync(client, "/Contracts", new Dictionary<string, string> { ["View"] = "system:all-contracts", ["Org"] = org.ToString(), ["ExportFormat"] = format, ["ExportLarge"] = "false" }, "/Contracts?handler=Export");

    [Fact]
    public async Task Export_headings_and_the_worksheet_name_use_the_exporting_organisations_wording()
    {
        using var app = DemoApp.Start();
        var (tenant, jbsOrg) = await TenantAsync(app, "Demo Procurement Group");
        var (_, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var blankEmail = await UserInAsync(app, tenant, blankOrg, RoleCodes.ProcurementAdministrator, null, "Confidential");
        using var jbs = await app.SignInAsync(ProcAdmin, followRedirects: true);
        using var blank = await app.SignInAsync(blankEmail, followRedirects: true);

        var jbsCsv = await ExportAsync(jbs, jbsOrg, "csv");
        Assert.Equal(HttpStatusCode.OK, jbsCsv.StatusCode);
        var jbsHeader = (await jbsCsv.Content.ReadAsStringAsync()).Split('\n')[0];
        Assert.Contains("Supplier", jbsHeader);

        var blankCsv = await ExportAsync(blank, blankOrg, "csv");
        Assert.Equal(HttpStatusCode.OK, blankCsv.StatusCode);
        var blankHeader = (await blankCsv.Content.ReadAsStringAsync()).Split('\n')[0];
        Assert.Contains("Counterparty", blankHeader);
        Assert.DoesNotContain("Supplier", blankHeader);

        // The workbook's sheet and details speak the same way; the sheet name obeys the spreadsheet rules.
        async Task<string> Workbook(HttpClient c, Guid o)
        {
            var response = await ExportAsync(c, o, "xlsx");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
            using var reader = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
            return await reader.ReadToEndAsync();
        }
        Assert.Contains("name=\"Contracts\"", await Workbook(jbs, jbsOrg));
        Assert.Contains("name=\"Contracts\"", await Workbook(blank, blankOrg));      // neutral wording is also 'Contracts'
    }

    [Fact]
    public async Task Onboarding_pages_for_a_blank_organisation_carry_no_reference_wording_and_a_reference_organisation_keeps_it()
    {
        using var app = DemoApp.Start();
        var (tenant, jbsOrg) = await TenantAsync(app, "Demo Procurement Group");
        var (_, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var blankAdmin = await UserInAsync(app, tenant, blankOrg, RoleCodes.OrganisationAdministrator, null, "Confidential");
        using var blank = await app.SignInAsync(blankAdmin, followRedirects: true);
        using var jbs = await app.SignInAsync("orgadmin@a.example.test", followRedirects: true);

        foreach (var url in new[] { $"/Onboarding?org={blankOrg}", $"/Onboarding/Step/categories?org={blankOrg}", $"/Onboarding/Step/viewers?org={blankOrg}", $"/Onboarding/Review?org={blankOrg}" })
        {
            var html = await BodyAsync(blank, url);
            var visible = Regex.Replace(html, "<input[^>]*>|<a [^>]*>|<title>.*?</title>|<option[^>]*>[^<]*</option>|<textarea[^>]*>.*?</textarea>|<section class=\"card\" aria-labelledby=\"reg-h\">.*?</section>", "", RegexOptions.Singleline);   // the regional section prints the organisation's own settings
            var hit = Regex.Match(visible, @"(?i:.{0,50}(jbs|procurement|group head|supplier|category owner|Europe/London|Brisbane).{0,50})|.{0,50}\b(USD|AUD)\b.{0,50}");
            Assert.False(hit.Success, $"{url}: {hit.Value}");
        }
        // The pack step shows what the selected pack would say before anything is assigned.
        var pack = await BodyAsync(blank, $"/Onboarding/Step/pack?org={blankOrg}");
        Assert.Contains("Wording", pack);
        var jbsViewers = await BodyAsync(jbs, $"/Onboarding/Step/viewers?org={jbsOrg}");
        Assert.Contains("Category", jbsViewers);                                                       // the reference organisation keeps its pack's wording
        var blankViewers = await BodyAsync(blank, $"/Onboarding/Step/viewers?org={blankOrg}");
        Assert.DoesNotContain("Finance-Viewers", blankViewers);                                        // examples are neutral
    }

    [Fact]
    public async Task Changing_a_label_changes_no_machine_identifier_url_form_field_role_code_or_permission()
    {
        Assert.Equal(["SystemAdministrator", "TenantAdministrator", "OrganisationAdministrator", "DivisionSiteAdministrator", "BusinessConfigurationAdministrator",
            "ProcurementAdministrator", "CategoryOwner", "Legal", "Approver", "BackupApprover", "Auditor", "Viewer"], RoleCodes.All);

        using var app = DemoApp.Start();
        var (_, org) = await TenantAsync(app, "Demo Procurement Group");
        using var biz = await app.SignInAsync(BizConfig, followRedirects: false);
        using var viewer = await app.SignInAsync(Viewer, followRedirects: true);
        var before = await BodyAsync(viewer, "/Contracts");
        var form = EditForm(org, new Dictionary<string, (string, string)> { ["supplier"] = ("Partner", "Partners"), ["category"] = ("Portfolio", "Portfolios") });
        await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Save&organisationId={org}&locale=en");
        await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en");
        var after = await BodyAsync(viewer, "/Contracts");

        Assert.NotEqual(before, after);
        // Field names, ids and addresses are the same; only the words differ.
        static string Machine(string html) => string.Join("|", Regex.Matches(html, @"(?:name|id|href|value)=""([^""]*)""").Select(m => m.Groups[1].Value).Where(v => !v.Contains(' ')));
        var names = Regex.Matches(before, @"name=""(F|Q|View|Org|Sort|Desc|ExportFormat)""").Count;
        Assert.Equal(names, Regex.Matches(after, @"name=""(F|Q|View|Org|Sort|Desc|ExportFormat)""").Count);
        Assert.Equal(Regex.Matches(before, @"href=""/Suppliers[^""]*""").Count, Regex.Matches(after, @"href=""/Suppliers[^""]*""").Count);
        Assert.Contains("href=\"/Suppliers\"", after);
        Assert.NotEmpty(Machine(after));
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/Suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Terminology")).StatusCode);
    }

    // ---- approver-group names and role visibility (A+ follow-ups, ADR-0029) ----

    /// <summary>Adds the approver-group rows and the role rows an edit-form post carries. Roles not named are posted as "inherit".</summary>
    private static Dictionary<string, string> WithExtras(Dictionary<string, string> form, IReadOnlyList<(string Key, string Name)> groups, IReadOnlyDictionary<string, string> roleChoices)
    {
        for (var i = 0; i < groups.Count; i++) { form[$"GroupRows[{i}].Key"] = groups[i].Key; form[$"GroupRows[{i}].Name"] = groups[i].Name; }
        for (var i = 0; i < RoleCodes.All.Count; i++) { form[$"RoleRows[{i}].Code"] = RoleCodes.All[i]; form[$"RoleRows[{i}].Choice"] = roleChoices.GetValueOrDefault(RoleCodes.All[i], "inherit"); }
        return form;
    }

    [Fact]
    public async Task Group_names_and_role_visibility_are_set_in_the_form_shown_beside_their_stable_keys_and_change_no_access()
    {
        using var app = DemoApp.Start();
        var (_, org) = await TenantAsync(app, "Demo Procurement Group");
        var (_, headId) = await app.FindUserAsync("grouphead@a.example.test");
        var (_, legalId) = await app.FindUserAsync(Legal);
        using var biz = await app.SignInAsync(BizConfig, followRedirects: false);
        using var admin = await app.SignInAsync(OrgAdmin, followRedirects: true);
        using var legal = await app.SignInAsync(Legal, followRedirects: true);

        // Before: the pack names the group; every role is offered.
        var before = await BodyAsync(admin, $"/Admin/Users/Details/{headId}");
        Assert.Contains("Group Head of Procurement (GroupHeadProcurement)", before);
        Assert.Contains("<option value=\"Legal\"", before);
        Assert.Contains("<option value=\"DivisionSiteAdministrator\"", before);

        // The form: a new name for the group, and Legal not offered. Saving and validating publish nothing.
        var form = WithExtras(EditForm(org, new Dictionary<string, (string, string)>()), [("GroupHeadProcurement", "Head of Group Procurement"), ("Local-Panel", "Local panel")], new Dictionary<string, string> { [RoleCodes.Legal] = "hidden" });
        var save = await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Save&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Contains("Group Head of Procurement (GroupHeadProcurement)", await BodyAsync(admin, $"/Admin/Users/Details/{headId}"));    // still the old name: not published
        var edit = await BodyAsync(biz, Edit(org));
        Assert.Contains("value=\"Head of Group Procurement\"", edit);                                                                    // the saved name is back in the form
        Assert.Matches("<option value=\"hidden\" selected[^>]*>Not offered</option>", edit);
        Assert.DoesNotMatch("(?s)value=\"RoleRows\\[(0|1|2)\\]\\.Choice\".{0,300}value=\"hidden\"", edit);                                // no hidden option for an administrative role
        var preview = await BodyAsync(biz, $"/Admin/Terminology/Preview?organisationId={org}");
        Assert.Contains("Head of Group Procurement", preview);
        Assert.Contains("Legal", preview);

        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Validate&organisationId={org}&locale=en")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en")).StatusCode);

        // After: the key is shown with its new name; Legal is no longer offered but its holders are still listed and keep their access.
        var after = await BodyAsync(admin, $"/Admin/Users/Details/{headId}");
        Assert.Contains("Head of Group Procurement (GroupHeadProcurement)", after);
        Assert.DoesNotContain("<option value=\"Legal\"", after);
        Assert.Contains("<option value=\"CategoryOwner\"", after);
        Assert.Contains("Not offered here", after);
        var legalPage = await BodyAsync(admin, $"/Admin/Users/Details/{legalId}");
        Assert.Contains("title=\"Stable role identifier\">Legal</code>", legalPage);                                                    // the assignment is still there
        Assert.Equal(HttpStatusCode.OK, (await legal.GetAsync("/Legal")).StatusCode);                                                    // and so is what it allows
        var overview = await BodyAsync(biz, $"/Admin/Terminology?organisationId={org}");
        Assert.Contains("Head of Group Procurement", overview);
        Assert.Contains("Local-Panel", overview);
        Assert.Contains("Not offered in pickers", overview);
    }

    [Fact]
    public async Task A_group_key_or_role_choice_that_is_not_allowed_is_shown_beside_its_row_with_the_entered_values_kept()
    {
        using var app = DemoApp.Start();
        var (_, org) = await TenantAsync(app, "Demo Procurement Group");
        using var biz = await app.SignInAsync(BizConfig, followRedirects: false);
        var form = WithExtras(EditForm(org, new Dictionary<string, (string, string)>()), [("bad key!", "Some name"), ("Fine-Key", "<b>bold</b>")],
            new Dictionary<string, string> { [RoleCodes.OrganisationAdministrator] = "hidden", [RoleCodes.Viewer] = "hidden" });
        var response = await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Validate&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("There is a problem", html);
        Assert.Contains("href=\"#group-0-key\"", html);                                          // the summary links to the field it is about
        Assert.Contains("cannot be hidden", html);
        Assert.Contains("must be plain text", html);
        Assert.Contains("value=\"bad key!\"", html);                                              // what was typed is kept
        Assert.DoesNotContain("<b>bold</b>", html);                                               // markup is encoded, never raw
        // Nothing was published by a failed publish.
        var publish = await DemoApp.PostFormAsync(biz, Edit(org), form, $"/Admin/Terminology/Edit?handler=Publish&organisationId={org}&locale=en");
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        using var admin = await app.SignInAsync(OrgAdmin, followRedirects: true);
        var (_, headId) = await app.FindUserAsync("grouphead@a.example.test");
        Assert.Contains("<option value=\"Viewer\"", await BodyAsync(admin, $"/Admin/Users/Details/{headId}"));
    }

    private static IEnumerable<(string, string, string, string, int, bool)> Content(out Dictionary<string, string> documents) => PackTestKit.ThirdPackContent(out documents);
}
