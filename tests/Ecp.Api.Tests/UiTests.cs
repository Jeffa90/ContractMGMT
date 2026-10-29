using System.Net;
using System.Text.RegularExpressions;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

[Collection("Api")]
[Trait("Category", "Api")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class UiAccessTests
{
    private static async Task<string> BodyAsync(HttpClient c, string url) => await c.GetStringAsync(url);

    [Fact]
    public async Task Anonymous_visitors_are_sent_to_sign_in_and_see_no_data()
    {
        using var app = DemoApp.Start();
        using var client = app.NewClient();
        var response = await client.GetAsync("/Contracts");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/SignIn", response.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Admin/Users")).StatusCode);
        var signIn = await client.GetAsync("/SignIn");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var html = await signIn.Content.ReadAsStringAsync();
        Assert.Contains("Demonstration identity", html);
        Assert.DoesNotContain("DEMOA1-", html);
    }

    [Fact]
    public async Task A_category_viewer_sees_only_the_packaging_contracts_in_the_register()
    {
        using var app = DemoApp.Start();
        using var client = await app.SignInAsync("viewer.category@a.example.test");
        var home = await BodyAsync(client, "/Access");
        Assert.Contains("Viewer (Category)", home);
        var register = await BodyAsync(client, "/Contracts");
        Assert.Contains("Synthetic Packaging Supply Agreement", register);
        Assert.Contains("General Packaging Terms", register);
        Assert.DoesNotContain("Synthetic Energy Agreement", register);   // another category
        Assert.DoesNotContain("Network Services Agreement", register);
        Assert.DoesNotContain("Facilities Cleaning", register);          // another organisation
        Assert.DoesNotContain("Isolation Test Packaging", register);     // another tenant
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Contracts/Create")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/Users")).StatusCode);
    }

    [Fact]
    public async Task The_restricted_viewer_is_limited_by_site_category_and_ceiling()
    {
        using var app = DemoApp.Start();
        using var client = await app.SignInAsync("viewer.restricted@a.example.test");
        var register = await BodyAsync(client, "/Contracts");
        // Restricted viewer: site A1-1 + ENERGY-ELEC + ceiling Public. The only Energy-Electricity contract is at another site and Confidential.
        Assert.DoesNotContain("Synthetic Energy Agreement", register);
        Assert.Contains("0 contract(s) found", register);
    }

    [Fact]
    public async Task Another_tenants_record_and_an_out_of_scope_record_are_both_plain_not_found()
    {
        using var app = DemoApp.Start();
        var (tenantB, _) = await app.FindUserAsync("tenantadmin@b.example.test");
        var foreignId = await app.WithTenantAsync(tenantB, sp => ContractIdByTitleAsync(sp, "Isolation Test Packaging Agreement"));
        var (tenantA, _) = await app.FindUserAsync("tenantadmin@a.example.test");
        var energyId = await app.WithTenantAsync(tenantA, sp => ContractIdByTitleAsync(sp, "Synthetic Energy Agreement"));

        using var viewer = await app.SignInAsync("viewer.category@a.example.test");
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/Contracts/Details/{foreignId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/Contracts/Details/{energyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/Contracts/Details/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/Suppliers/Details/{Guid.NewGuid()}")).StatusCode);
    }

    private static async Task<Guid> ContractIdByTitleAsync(IServiceProvider sp, string title)
    {
        var db = sp.GetRequiredService<Ecp.Contracts.Persistence.ContractsDbContext>();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Contracts).Where(c => c.ContractTitle == title).Select(c => c.Id));
    }

    [Fact]
    public async Task A_category_owner_edits_an_owned_draft_but_not_another_category_or_an_effective_record()
    {
        using var app = DemoApp.Start();
        var (tenantA, _) = await app.FindUserAsync("tenantadmin@a.example.test");
        var draftId = await app.WithTenantAsync(tenantA, sp => ContractIdByTitleAsync(sp, "Packaging Film Supply (draft)"));
        var energyDraft = await app.WithTenantAsync(tenantA, sp => ContractIdByTitleAsync(sp, "Energy Metering Services (draft)"));
        var restrictedEnergy = await app.WithTenantAsync(tenantA, sp => ContractIdByTitleAsync(sp, "Network Services Agreement (draft)"));
        var effective = await app.WithTenantAsync(tenantA, sp => ContractIdByTitleAsync(sp, "Synthetic Packaging Supply Agreement"));

        using var owner = await app.SignInAsync("catowner.packaging@a.example.test");
        Assert.Contains("Edit details", await BodyAsync(owner, $"/Contracts/Details/{draftId}"));
        Assert.DoesNotContain("Edit details", await BodyAsync(owner, $"/Contracts/Details/{energyDraft}"));   // read-all, edit assigned only
        Assert.DoesNotContain("Edit details", await BodyAsync(owner, $"/Contracts/Details/{effective}"));     // locked
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/Contracts/Edit/{energyDraft}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/Contracts/Edit/{effective}")).StatusCode);
        // A Restricted record is above this owner's Confidential ceiling: invisible, not merely read-only.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/Contracts/Details/{restrictedEnergy}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/Contracts/Edit/{restrictedEnergy}")).StatusCode);

        var save = await DemoApp.PostFormAsync(owner, $"/Contracts/Edit/{draftId}", new Dictionary<string, string> { ["Title"] = "Packaging Film Supply (revised)", ["Description"] = "Edited in a test" });
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Contains("Packaging Film Supply (revised)", await BodyAsync(owner, $"/Contracts/Details/{draftId}"));
        // posting to a record the owner may read but not edit is refused even though the page itself was not offered
        var forbidden = await DemoApp.PostFormAsync(owner, $"/Contracts/Edit/{draftId}", new Dictionary<string, string> { ["Title"] = "x" }, $"/Contracts/Edit/{energyDraft}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Category_owner_energy_cannot_edit_packaging_after_the_assignment_ended()
    {
        using var app = DemoApp.Start();
        var (tenantA, _) = await app.FindUserAsync("tenantadmin@a.example.test");
        var packagingDraft = await app.WithTenantAsync(tenantA, sp => ContractIdByTitleAsync(sp, "Packaging Film Supply (draft)"));
        using var energy = await app.SignInAsync("catowner.energy@a.example.test");
        // The demo data gives this person a Packaging assignment that ended in 2023.
        Assert.Equal(HttpStatusCode.Forbidden, (await energy.GetAsync($"/Contracts/Edit/{packagingDraft}")).StatusCode);
    }

    [Fact]
    public async Task Create_contract_assigns_the_organisation_reference_and_enforces_category_ownership()
    {
        using var app = DemoApp.Start();
        using var owner = await app.SignInAsync("catowner.packaging@a.example.test");
        var page = await BodyAsync(owner, "/Contracts/Create");   // single organisation for this user: the form is shown directly
        Assert.Contains("DEMOA1-", page);
        var categoryId = Regex.Match(page, "<option value=\"([0-9a-f-]{36})\"[^>]*>Packaging</option>").Groups[1].Value;
        var energyId = Regex.Match(page, "<option value=\"([0-9a-f-]{36})\"[^>]*>Energy</option>").Groups[1].Value;
        var supplierId = Regex.Match(page, "<option value=\"([0-9a-f-]{36})\"[^>]*>Southern Cross Demo Supplies").Groups[1].Value;
        var orgId = Regex.Match(page, "name=\"OrganisationId\" value=\"([0-9a-f-]{36})\"").Groups[1].Value;

        Dictionary<string, string> Form(string category) => new()
        {
            ["OrganisationId"] = orgId, ["Title"] = "Created in a browser test", ["SupplierId"] = supplierId, ["CategoryId"] = category,
            ["Classification"] = "Internal", ["CurrencyCode"] = "AUD", ["EstimatedValue"] = "1234.50", ["StartDate"] = "2026-11-01"
        };
        var created = await DemoApp.PostFormAsync(owner, "/Contracts/Create", Form(categoryId));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var details = await BodyAsync(owner, created.Headers.Location!.OriginalString);
        Assert.Contains("Created in a browser test", details);
        Assert.Matches("DEMOA1-0\\d{5}", details);
        Assert.Contains("$1,234.50", details);          // organisation currency symbol, en-AU grouping
        Assert.Contains("1/11/2026", details);          // en-AU day-first date

        var refused = await DemoApp.PostFormAsync(owner, "/Contracts/Create", Form(energyId));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("only in categories you currently own", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Suppliers_follow_the_viewers_visible_contracts()
    {
        using var app = DemoApp.Start();
        using var all = await app.SignInAsync("viewer.all@a.example.test");
        var allHtml = await BodyAsync(all, "/Suppliers");
        Assert.Contains("Southern Cross Demo Supplies", allHtml);
        Assert.Contains("Unused Demo Supplier", allHtml);                 // an All Contracts Viewer sees the organisation's suppliers
        Assert.DoesNotContain("Demo Facilities Services", allHtml);       // other organisation
        using var category = await app.SignInAsync("viewer.category@a.example.test");
        var catHtml = await BodyAsync(category, "/Suppliers");
        Assert.Contains("Southern Cross Demo Supplies", catHtml);
        Assert.DoesNotContain("Demo Energy Markets", catHtml);
        Assert.DoesNotContain("Unused Demo Supplier", catHtml);
    }

    [Fact]
    public async Task Administrators_manage_people_but_cannot_read_contracts()
    {
        using var app = DemoApp.Start();
        using var orgAdmin = await app.SignInAsync("orgadmin@a.example.test");
        var users = await BodyAsync(orgAdmin, "/Admin/Users");
        Assert.Contains("Viewer Category", users);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgAdmin.GetAsync("/Contracts")).StatusCode);   // administers people, holds no contract permission
        Assert.Equal(HttpStatusCode.Forbidden, (await orgAdmin.GetAsync("/Admin/Health")).StatusCode);
    }

    [Fact]
    public async Task The_auditor_reads_events_and_exports_evidence_the_viewer_cannot()
    {
        using var app = DemoApp.Start();
        using var auditor = await app.SignInAsync("auditor@a.example.test");
        var audit = await BodyAsync(auditor, "/Admin/Audit");
        Assert.Contains("contract.created", audit);
        var export = await auditor.GetAsync("/Admin/Audit?handler=Export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/json", export.Content.Headers.ContentType?.MediaType);
        var json = await export.Content.ReadAsStringAsync();
        Assert.Contains("\"TenantChainValid\": true", json);
        Assert.Contains("Sha256OfEvents", json);

        using var viewer = await app.SignInAsync("viewer.all@a.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Audit?handler=Export")).StatusCode);
    }

    [Fact]
    public async Task The_installation_administrator_sees_health_but_no_tenant_data()
    {
        using var app = DemoApp.Start();
        using var sys = await app.SignInAsync("sysadmin@installation.example.test");
        var health = await BodyAsync(sys, "/Admin/Health");
        Assert.Contains("R-DB-01", health);
        Assert.Contains("R-OPS-02", health);
        Assert.Equal(HttpStatusCode.Forbidden, (await sys.GetAsync("/Contracts")).StatusCode);   // the installation role holds no tenant data permission
        Assert.Equal(HttpStatusCode.Forbidden, (await sys.GetAsync("/Admin/Users")).StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_loses_its_session_on_the_next_request()
    {
        using var app = DemoApp.Start();
        using var client = await app.SignInAsync("viewer.all@a.example.test");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Contracts")).StatusCode);
        var (tenant, user) = await app.FindUserAsync("viewer.all@a.example.test");
        await app.WithTenantAsync(tenant, async sp => { await sp.GetRequiredService<IUserService>().DisableAsync(user, "Left the organisation"); return true; });
        var after = await client.GetAsync("/Contracts");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/SignIn", after.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Contracts")).StatusCode);
    }

    [Fact]
    public async Task Access_simulation_explains_decisions_without_changing_anything()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync("tenantadmin@a.example.test");
        var first = await BodyAsync(admin, "/Admin/Simulation");
        var orgId = Regex.Match(first, "<option value=\"([0-9a-f-]{36})\"[^>]*>Demo Procurement Group</option>").Groups[1].Value;
        var page = await BodyAsync(admin, $"/Admin/Simulation?OrganisationId={orgId}");
        var userId = Regex.Match(page, "<option value=\"([0-9a-f-]{36})\"[^>]*>Viewer Category").Groups[1].Value;
        var categoryId = Regex.Match(page, "id=\"CategoryId\".*?<option value=\"([0-9a-f-]{36})\"[^>]*>Packaging</option>", RegexOptions.Singleline).Groups[1].Value;
        var response = await DemoApp.PostFormAsync(admin, "/Admin/Simulation", new Dictionary<string, string>
        { ["UserId"] = userId, ["OrganisationId"] = orgId, ["CategoryId"] = categoryId, ["Classification"] = "Confidential" });
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Result for", html);
        Assert.Contains("Not visible (404)", html);    // Viewer ceiling is Internal; the sample is Confidential
        Assert.True(html.Contains("Layer 6"), $"cat={categoryId} org={orgId} user={userId}; Simulation output: " + Regex.Replace(html[html.IndexOf("Result for", StringComparison.Ordinal)..], "<[^>]+>", " ").Replace("\n", " ")[..700]);
    }
}
