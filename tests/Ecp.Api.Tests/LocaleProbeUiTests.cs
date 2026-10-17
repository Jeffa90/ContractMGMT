using System.Net;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The locale probe (S9 readiness, G6): an organisation that is not Australian (en-US, USD, America/Chicago, a second site in America/Los_Angeles) is given contracts and a reminder, and the pages and exports a person
/// uses are rendered and scanned for the reference configuration leaking through: the Australian currency, time zones, day-first dates and the Australian dollar sign.
/// </summary>
[Collection("Api")]
[Trait("Category", "Locale")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class LocaleProbeUiTests
{
    private const string TenantAdmin = "tenantadmin@a.example.test";

    private static readonly string[] OwnerPages =
    [
        "/", "/Contracts", "/Contracts?sort=endDate", "/Suppliers", "/Suppliers/Create", "/Renewals", "/Notifications", "/Dashboards", "/Legal", "/Approvals"
    ];

    private static readonly string[] AdminPages =
    [
        "/Admin/Categories", "/Admin/Users", "/Admin/Notifications", "/Admin/Terminology", "/Admin/Terminology/Edit", "/Admin/Capabilities", "/Admin/Packs", "/Admin/Approvals", "/Admin/Studio",
        "/Admin/Audit", "/Admin/Health", "/Admin/Configuration"
    ];

    // The page's lang attribute names the installation's UI culture (appsettings, en-AU in the reference installation); it is not organisation data and is recorded as an installation setting, not scanned for.

    /// <summary>What must never appear for a US organisation: the reference organisation's currency, zones, the Australian dollar sign and day-first slash dates.</summary>
    private static readonly Regex Leak = new(@"\bAUD\b|Brisbane|Sydney|Melbourne|A\$|AU\$|\b(1[3-9]|2\d|3[01])/\d{2}/\d{4}\b", RegexOptions.Compiled);

    private static async Task<string> UserAsync(DemoApp app, Guid tenant, Guid org, string role, Guid? categoryToOwn = null)
    {
        var email = $"loc.{Guid.NewGuid():N}"[..16] + "@a.example.test";
        await app.WithTenantAsync(tenant, async sp =>
        {
            var id = await sp.GetRequiredService<IUserService>().CreateActiveTrustedAsync("Locale " + role, email, Ecp.Host.Security.DemoIdentityService.ProviderKey, Ecp.Host.Security.DemoIdentityService.Issuer, email, "test");
            await sp.GetRequiredService<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, null, ScopeSet.ForOrganisations([org], "Confidential"), null,
                EffectiveFromUtc: DateTime.UtcNow.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
            if (categoryToOwn is { } category) await sp.GetRequiredService<ICategoryAdministration>().AssignOwnerTrustedAsync(category, id, true, DateTime.UtcNow.AddDays(-30), null, "test", null);
            return 0;
        });
        return email;
    }

    [Fact]
    public async Task A_US_organisation_sees_no_Australian_currency_zone_culture_or_date_order_on_its_pages_or_exports()
    {
        using var app = DemoApp.Start();
        var (tenant, tenantAdminId) = await app.FindUserAsync(TenantAdmin);
        var (org, contractId) = await app.WithTenantAsync(tenant, async sp =>
        {
            sp.GetRequiredService<CurrentUser>().Set(tenantAdminId);
            var orgId = await sp.GetRequiredService<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest("Harborline Probe", "America/Chicago", "en-US", "USD", "HBL", "blank"));
            var structure = sp.GetRequiredService<IOrganisationStructureService>();
            var chicago = await structure.UpsertSiteAsync(orgId, "CHI", "Chicago yard", "America/Chicago");
            await structure.UpsertSiteAsync(orgId, "LAX", "Los Angeles yard", "America/Los_Angeles");
            var category = await sp.GetRequiredService<ICategoryAdministration>().UpsertCategoryAsync(orgId, "GEN", "General", null);
            var supplier = await sp.GetRequiredService<ISupplierService>().CreateAsync(new CreateSupplierRequest(orgId, "Lakeside Concrete LLC", VendorNumber: "V-100"));
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var id = await sp.GetRequiredService<IContractService>().CreateDraftAsync(new CreateContractRequest(orgId, "HBL-000001", "Foundation works", supplier, category, CurrencyCode: "USD", EstimatedValue: 1234567.89m,
                StartDate: today.AddDays(-20), ReviewDate: today.AddDays(30), EndDate: today.AddDays(60), SiteId: chicago, NoticePeriodValue: 30, NoticePeriodUnit: "Days", CriticalNotice: true));
            return (orgId, id);
        });
        var owner = await UserAsync(app, tenant, org, RoleCodes.CategoryOwner, (await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<ICategoryAdministration>().UpsertCategoryAsync(org, "GEN", "General", null)))));
        var admin = await UserAsync(app, tenant, org, RoleCodes.OrganisationAdministrator);

        var leaks = new List<string>();
        var rendered = 0;
        async Task Scan(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"{url} -> {(int)response.StatusCode}");
            if (response.StatusCode != HttpStatusCode.OK) return;
            rendered++;
            var html = Regex.Replace(await response.Content.ReadAsStringAsync(), "<script.*?</script>|<style.*?</style>|<input[^>]*type=\"hidden\"[^>]*>|<meta[^>]*>", "", RegexOptions.Singleline);
            foreach (Match m in Leak.Matches(html)) leaks.Add($"{url}: '{m.Value}'");
        }

        using (var client = await app.SignInAsync(owner, followRedirects: true))
        {
            foreach (var url in OwnerPages) await Scan(client, url);
            foreach (var url in new[] { $"/Contracts/Details/{contractId}", $"/Contracts/Edit/{contractId}", $"/Contracts/Lifecycle/{contractId}", $"/Contracts/Create?organisationId={org}", $"/Contracts/Documents/{contractId}" })
                await Scan(client, url);
        }
        using (var client = await app.SignInAsync(await UserAsync(app, tenant, org, RoleCodes.ProcurementAdministrator), followRedirects: true))
        {
            // The export: amounts and headings.
            var csv = await DemoApp.PostFormAsync(client, "/Contracts", new Dictionary<string, string> { ["View"] = "system:all-contracts", ["Org"] = org.ToString(), ["ExportFormat"] = "csv", ["ExportLarge"] = "false" }, "/Contracts?handler=Export");
            var text = await csv.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            foreach (Match m in Leak.Matches(text)) leaks.Add($"export: '{m.Value}'");
        }
        using (var client = await app.SignInAsync(admin, followRedirects: true))
            foreach (var url in AdminPages) await Scan(client, url);

        Assert.True(rendered >= 15, $"Only {rendered} pages rendered; the probe needs at least 15 to mean anything.");
        Assert.True(leaks.Count == 0, "The reference configuration leaks into a US organisation:\n" + string.Join("\n", leaks.Distinct()));

        // The details page shows the organisation's formatting: US grouping and dollar sign, not Australian.
        using (var client = await app.SignInAsync(owner, followRedirects: true))
        {
            var details = await client.GetStringAsync($"/Contracts/Details/{contractId}");
            Assert.Contains("1,234,567.89", details);
            Assert.DoesNotMatch(@"\bAUD\b", details);
        }
    }
}
