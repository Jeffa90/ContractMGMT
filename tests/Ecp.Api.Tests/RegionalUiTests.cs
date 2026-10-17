using System.Net;
using System.Text.RegularExpressions;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// S10 through the real host: the Regional settings page, the supplier form (identifier types and address fields by organisation), business dates in organisation formats, and the machine-or-business export choice.
/// The demo tenant has an organisation on the reference (JBS) pack and one on the Blank pack; the Blank organisation then chooses a German profile with no source change.
/// </summary>
[Collection("Api")]
[Trait("Category", "Regional")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class RegionalUiTests
{
    private const string TenantAdmin = "tenantadmin@a.example.test";
    private const string OrgAdmin = "orgadmin@a.example.test";
    private const string Viewer = "viewer.all@a.example.test";
    private const string SiteAdmin = "siteadmin@a.example.test";
    private const string CatOwner = "catowner.packaging@a.example.test";
    private const string BizConfig = "bizconfig@a.example.test";
    private const string ProcAdmin = "procadmin@a.example.test";

    private static async Task<(Guid Tenant, Guid Org)> TenantAsync(DemoApp app, string orgName)
    {
        var (tenant, _) = await app.FindUserAsync(TenantAdmin);
        var org = await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single(o => o.Name == orgName).Id);
        return (tenant, org);
    }

    private static async Task<string> UserInAsync(DemoApp app, Guid tenant, Guid org, string role, string ceiling = "Internal")
    {
        var email = $"reg.{Guid.NewGuid():N}@a.example.test"[..40] + ".test";
        await app.WithTenantAsync(tenant, async sp =>
        {
            var id = await sp.GetRequiredService<IUserService>().CreateActiveTrustedAsync("Regional " + role, email, Ecp.Host.Security.DemoIdentityService.ProviderKey, Ecp.Host.Security.DemoIdentityService.Issuer, email, "test");
            await sp.GetRequiredService<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, null, ScopeSet.ForOrganisations([org], ceiling), null,
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

    private static string Visible(string html) => Regex.Replace(Regex.Replace(html, "<script.*?</script>|<style.*?</style>", "", RegexOptions.Singleline), "<[^>]+>", " ");

    private static Task<HttpResponseMessage> Post(HttpClient c, Guid org, string handler, Dictionary<string, string> form)
        => DemoApp.PostFormAsync(c, $"/Admin/Regional?organisationId={org}", form, $"/Admin/Regional?handler={handler}&organisationId={org}");

    private static Dictionary<string, string> GermanForm(Guid org, string zone = "Europe/Berlin")
    {
        var form = new Dictionary<string, string>
        {
            ["OrganisationId"] = org.ToString(), ["Country"] = "DE", ["Locale"] = "de-DE", ["SupportedLocales"] = "", ["Currency"] = "EUR", ["SupportedCurrencies"] = "", ["TimeZone"] = zone, ["TimeDisplay"] = "24h", ["PhoneRegion"] = "",
            ["OwnIdentifiers"] = "true", ["Identifiers[0].Key"] = "de.vat", ["Identifiers[0].Label"] = "USt-IdNr", ["Identifiers[0].Country"] = "DE", ["Identifiers[0].Rule"] = "alphanumeric", ["Identifiers[0].Length"] = "11",
            ["Identifiers[0].Checksum"] = "none", ["Identifiers[0].Classification"] = "Internal", ["Identifiers[0].Unique"] = "true",
            ["OwnAddress"] = "true"
        };
        var keys = new[] { "line1", "line2", "locality", "region", "postal", "country" };
        for (var i = 0; i < keys.Length; i++) { form[$"AddressRows[{i}].Key"] = keys[i]; form[$"AddressRows[{i}].Used"] = keys[i] == "region" ? "false" : "true"; if (keys[i] is "line1" or "postal") form[$"AddressRows[{i}].Required"] = "true"; }
        return form;
    }

    [Fact]
    public async Task The_regional_page_opens_only_for_people_who_may_draft_or_read_configuration_history()
    {
        using var app = DemoApp.Start();
        using var anonymous = app.NewClient();
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/Admin/Regional")).StatusCode);
        foreach (var email in new[] { Viewer, CatOwner })
        {
            using var c = await app.SignInAsync(email);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/Admin/Regional")).StatusCode);
        }
        // A site administrator holds no configuration right over a whole organisation: the page is not theirs, and it does not say why.
        using (var site = await app.SignInAsync(SiteAdmin)) Assert.NotEqual(HttpStatusCode.OK, (await site.GetAsync("/Admin/Regional")).StatusCode);
        foreach (var email in new[] { OrgAdmin, BizConfig })
        {
            using var c = await app.SignInAsync(email, followRedirects: true);
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Admin/Regional")).StatusCode);
        }
    }

    [Fact]
    public async Task The_page_shows_what_is_in_force_where_each_value_comes_from_and_a_preview_in_those_formats()
    {
        using var app = DemoApp.Start();
        var (_, jbsOrg) = await TenantAsync(app, "Demo Procurement Group");
        using var admin = await app.SignInAsync(OrgAdmin, followRedirects: true);
        var html = await BodyAsync(admin, $"/Admin/Regional?organisationId={jbsOrg}");
        var text = Visible(html);
        Assert.Contains("Regional settings", text);
        Assert.Contains("In force for", text);
        Assert.Contains("Demo Procurement Group", text);
        Assert.Contains("jbs-procurement", text);                       // the pack that proposes the identifier types
        Assert.Contains("ABN", text);                                   // the reference pack's identifier label
        Assert.Matches(@"\b16/10/2026\b|\b10/16/2026\b|\b16\.10\.2026\b|\b2026-10-16\b", text);   // a sample date in whatever the organisation's locale writes
        Assert.Contains("Offset from UTC in January and July", text);
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}(:\d{2})? UTC", text);                  // no fixed ISO-plus-UTC timestamps
        Assert.Contains("nothing is looked up in a registry", text.Replace("\n", " "), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_blank_organisation_offers_no_identifier_type_and_chooses_its_own_through_the_form()
    {
        using var app = DemoApp.Start();
        var (tenant, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var config = await UserInAsync(app, tenant, blankOrg, RoleCodes.BusinessConfigurationAdministrator);
        var supplierAdmin = await UserInAsync(app, tenant, blankOrg, RoleCodes.ProcurementAdministrator, "Confidential");
        using var cfg = await app.SignInAsync(config);
        using var sup = await app.SignInAsync(supplierAdmin);

        // Before: no identifier field and no Australian wording anywhere on the supplier form.
        var before = Visible(await BodyAsync(sup, "/Suppliers/Create"));
        Assert.DoesNotMatch(@"\bABN\b", before);
        Assert.DoesNotContain("USt-IdNr", before);
        Assert.DoesNotContain("Tax Identifier", before);

        // Save, validate and publish the German profile through the form.
        var form = GermanForm(blankOrg);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(cfg, blankOrg, "Save", form)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(cfg, blankOrg, "Validate", form)).StatusCode);
        var published = await Post(cfg, blankOrg, "Publish", form);
        Assert.Equal(HttpStatusCode.Redirect, published.StatusCode);

        var page = Visible(await BodyAsync(cfg, $"/Admin/Regional?organisationId={blankOrg}"));
        Assert.Contains("de-DE", page);
        Assert.Contains("Europe/Berlin", page);
        Assert.Contains("16.10.2026", page);                            // the preview now writes dates the German way
        var publishedLine = Regex.Match(page, @"Published release.{0,160}").Value;
        Assert.Matches(@"\b\d{1,2}\.\d{1,2}\.\d{4}\b", publishedLine);                // the publication time is shown in the organisation's own format and zone
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", publishedLine);

        // The supplier form now asks for the organisation's own identifier type and address fields, with its own labels, and no ABN.
        var after = await BodyAsync(sup, "/Suppliers/Create");
        var visible = Visible(after);
        Assert.Contains("USt-IdNr", visible);
        Assert.DoesNotMatch(@"\bABN\b", visible);
        Assert.Contains("name=\"Identifier[de.vat]\"", after);
        Assert.Contains("name=\"Address[line1]\"", after);
        Assert.DoesNotContain("name=\"Address[region]\"", after);       // an unused address field is not offered

        // The server applies the organisation's rule: a short value and a missing required address field are refused; a fitting value is accepted.
        var bad = await DemoApp.PostFormAsync(sup, "/Suppliers/Create", new Dictionary<string, string> { ["OrganisationId"] = blankOrg.ToString(), ["Name"] = "Muster GmbH", ["Identifier[de.vat]"] = "DE123", ["Address[line1]"] = "Hauptstrasse 1", ["Address[postal]"] = "10115" });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Exactly 11 characters", await bad.Content.ReadAsStringAsync());
        var missing = await DemoApp.PostFormAsync(sup, "/Suppliers/Create", new Dictionary<string, string> { ["OrganisationId"] = blankOrg.ToString(), ["Name"] = "Muster GmbH", ["Identifier[de.vat]"] = "DE123456789" });
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        var good = await DemoApp.PostFormAsync(sup, "/Suppliers/Create", new Dictionary<string, string> { ["OrganisationId"] = blankOrg.ToString(), ["Name"] = "Muster GmbH", ["Identifier[de.vat]"] = "DE123456789", ["Address[line1]"] = "Hauptstrasse 1", ["Address[postal]"] = "10115", ["Address[locality]"] = "Berlin" });
        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);
        var details = Visible(await BodyAsync(sup, good.Headers.Location!.ToString()));
        Assert.Contains("DE123456789", details);
        Assert.Contains("USt-IdNr", details);
        Assert.DoesNotMatch(@"\bABN\b", details);
        Assert.Contains("Hauptstrasse 1", details);
    }

    [Fact]
    public async Task An_invalid_profile_is_refused_at_publication_and_the_page_keeps_what_was_typed()
    {
        using var app = DemoApp.Start();
        var (tenant, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var config = await UserInAsync(app, tenant, blankOrg, RoleCodes.BusinessConfigurationAdministrator);
        using var cfg = await app.SignInAsync(config);
        var form = GermanForm(blankOrg, zone: "Mars/Olympus_Mons");
        var response = await Post(cfg, blankOrg, "Publish", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);                     // the page again with the problem, not a redirect
        var html = await response.Content.ReadAsStringAsync();
        Assert.True(html.Contains("IANA time zone", StringComparison.Ordinal), Visible(html)[..Math.Min(1500, Visible(html).Length)].Replace("\n", " "));
        Assert.Contains("Mars/Olympus_Mons", html);                               // what was typed is kept
        var effective = Visible(await BodyAsync(cfg, $"/Admin/Regional?organisationId={blankOrg}"));
        Assert.DoesNotContain("Mars/Olympus_Mons", Regex.Match(effective, @"In force for.{0,600}").Value);
    }

    [Fact]
    public async Task Another_organisation_is_not_reachable_by_naming_it()
    {
        using var app = DemoApp.Start();
        var (tenant, blankOrg) = await TenantAsync(app, "Demo Facilities Organisation");
        var (_, jbsOrg) = await TenantAsync(app, "Demo Procurement Group");
        var config = await UserInAsync(app, tenant, blankOrg, RoleCodes.BusinessConfigurationAdministrator);
        using var cfg = await app.SignInAsync(config);
        Assert.Equal(HttpStatusCode.NotFound, (await cfg.GetAsync($"/Admin/Regional?organisationId={jbsOrg}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cfg.GetAsync($"/Admin/Regional?organisationId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task The_register_export_offers_business_and_machine_formats_and_business_pages_carry_no_fixed_utc_timestamps()
    {
        using var app = DemoApp.Start();
        using var proc = await app.SignInAsync(ProcAdmin, followRedirects: true);
        var register = await BodyAsync(proc, "/Contracts");
        Assert.Contains("name=\"ExportStyle\"", register);
        Assert.Contains("value=\"machine\"", register);
        // Pages that print stored instants show them in the organisation's zone, never as an ISO string followed by the word UTC.
        foreach (var url in new[] { "/Admin/Configuration", "/Notifications", "/Admin/Audit" })
        {
            using var admin = await app.SignInAsync(OrgAdmin, followRedirects: true);
            var response = await admin.GetAsync(url);
            if (response.StatusCode != HttpStatusCode.OK) continue;
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}(:\d{2})? UTC", Visible(await response.Content.ReadAsStringAsync()));
        }
    }

    [Fact]
    public async Task Readiness_reports_the_shipped_packs_regional_proposals_as_valid()
    {
        using var app = DemoApp.Start();
        using var client = app.NewClient();
        using var doc = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/health/ready"));
        var check = doc.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "regional");
        Assert.Equal("Healthy", check.GetProperty("status").GetString());
    }
}
