using System.Net;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.Lifecycle;
using Ecp.Lifecycle.Definitions;
using Ecp.Lifecycle.Domain;
using Ecp.Setup.Onboarding;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// Slice S9 through the real host (SBP-AC-08, 09, 11, 13): Harborline Construction, a synthetic US general contractor, runs on the shipped pack construction-subcontracts inside the same installation and tenant as the reference
/// and Blank demonstration organisations. Pages, notifications, exports and the role picker are rendered as people with different roles and scanned for the reference business's words, the neutral platform words the pack
/// replaces, and the Australian locale. Nothing here changes a table; every setup step goes through the services an administrator's pages call.
/// </summary>
[Collection("Api")]
[Trait("Category", "SecondBusiness")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class SecondBusinessUiTests
{
    private const string TenantAdmin = "tenantadmin@a.example.test";
    private const string Pack = "construction-subcontracts";

    private sealed record Harbor(Guid Tenant, Guid Org, Guid Active, Guid Draft, Guid ChangeOrder, string Manager, string Commercial, string Director, string Viewer, string Admin, string Auditor);

    private static async Task<Guid> UserAsync(IServiceProvider sp, string key, string role, Guid org, string? group = null, string ceiling = "Confidential", Guid? owns = null, ViewerKind? kind = null)
    {
        var email = key + "@hbl.example.test";
        var users = sp.GetRequiredService<IUserService>();
        var id = (await users.FindByEmailAsync(email))?.Id
            ?? await users.CreateActiveTrustedAsync("Harborline " + key, email, Ecp.Host.Security.DemoIdentityService.ProviderKey, Ecp.Host.Security.DemoIdentityService.Issuer, email, "test");
        await sp.GetRequiredService<IRoleAssignmentService>().GrantTrustedAsync(new GrantRequest(id, role, kind, role == RoleCodes.TenantAdministrator ? ScopeSet.Tenant(ceiling) : ScopeSet.ForOrganisations([org], ceiling), group,
            EffectiveFromUtc: DateTime.UtcNow.AddDays(-5), ExplicitHighCeiling: true), AssignmentSource.Demo, "test");
        if (owns is { } category) await sp.GetRequiredService<ICategoryAdministration>().AssignOwnerTrustedAsync(category, id, true, DateTime.UtcNow.AddDays(-30), null, "test", null);
        return id;
    }

    /// <summary>Gives the demonstration tenant a Harborline organisation on the shipped pack: assigned, every organisation-level draft validated and published in order, onboarded, and with a subcontract in several states.</summary>
    private static async Task<Harbor> SetUpAsync(DemoApp app)
    {
        var (tenant, tenantAdminId) = await app.FindUserAsync(TenantAdmin);
        var ctx = await app.WithTenantAsync(tenant, async sp =>
        {
            sp.GetRequiredService<CurrentUser>().Set(tenantAdminId);
            var org = await sp.GetRequiredService<ITenantProvisioningService>().AddOrganisationAsync(new AddOrganisationRequest("Harborline Construction", "America/Chicago", "en-US", "USD", "HBL", Pack));
            var admin = await UserAsync(sp, "hbl.admin", RoleCodes.OrganisationAdministrator, org, ceiling: "Restricted");
            sp.GetRequiredService<CurrentUser>().Set(admin);
            var assigned = await sp.GetRequiredService<IPackAssignments>().AssignAsync(org, Pack, "1.1.0");
            // The scale and branding drafts are tenant-level: in a tenant shared with other businesses they are left unpublished here.
            Assert.All(assigned.Kinds.Where(k => k.Kind != CapabilityKinds.Name), k => Assert.False(k.NeedsAttention, k.Kind + ": " + k.Outcome));
            Assert.Equal(2, assigned.Kinds.Count(k => k.Scope == "tenant"));
            var config = sp.GetRequiredService<IConfigurationService>();
            foreach (var kind in new[] { "CustomField", "Terminology", "ApprovalMatrix", WorkflowKinds.Variation, WorkflowKinds.ContractLifecycle, Ecp.Notifications.Policy.NotificationPolicyKinds.Policy, "ContractListView" })
            {
                var open = await config.FindOpenDraftAsync(kind, org);
                var validated = await config.ValidateAsync(open!.Id);
                Assert.True(validated.IsPublishable, kind + ": " + string.Join("; ", validated.Validation!.Messages.Select(m => m.Code + " " + m.Message)));
                await config.PublishAsync(validated.Id, "Harborline onboarding");
            }
            var defaults = sp.GetRequiredService<ISolutionPackCatalogue>().Find(Pack)!.Defaults;
            var manager = await UserAsync(sp, "hbl.manager", RoleCodes.CategoryOwner, org);
            var doc = new OnboardingDocument(1, null,
                new StructureSection([new("HBL-US", "Harborline Construction LLC")], [new("CENTRAL", "Central Region")], [new("BUILD", "Buildings", "CENTRAL")],
                    [new("PRJ-LAKESIDE", "Lakeside Tower", null, "America/Chicago"), new("PRJ-FOOTHILLS", "Foothills Plant", null, "America/Denver")]),
                [.. defaults.Categories.Select(c => new CategoryEntry(c.Code, c.Name, c.ParentCode))], [.. defaults.ContractTypes.Select(c => new ContractTypeEntry(c.Code, c.Name))],
                [new("CONCRETE", "hbl.manager@hbl.example.test", true), new("EARTHWORKS", "hbl.manager@hbl.example.test", true)], [], []);
            await sp.GetRequiredService<IConfigurationBootstrap>().DraftValidatePublishAsync("OrganisationOnboarding", org, doc.ToJson(), "onboarding-test", "Harborline onboarding");
            var concrete = (await sp.GetRequiredService<ICategoryAdministration>().ListAllAsync()).Single(c => c.OrganisationId == org && c.Code == "CONCRETE").Id;
            var site = (await sp.GetRequiredService<IOrganisationStructureService>().GetAsync(org)).Sites.Single(s => s.Key == "PRJ-LAKESIDE").Id;
            var supplier = await sp.GetRequiredService<ISupplierService>().CreateAsync(new CreateSupplierRequest(org, "Lakeside Concrete LLC", VendorNumber: "SUB-1001"));
            await UserAsync(sp, "hbl.commercial", RoleCodes.ProcurementAdministrator, org);
            var director = await UserAsync(sp, "hbl.director", RoleCodes.Approver, org, "ProjectDirectors");
            await UserAsync(sp, "hbl.viewer", RoleCodes.Viewer, org, ceiling: "Internal", kind: ViewerKind.AllContracts);
            await UserAsync(sp, "hbl.auditor", RoleCodes.Auditor, org);
            _ = director;

            return (org, concrete, site, supplier);
        });
        return await ContinueAsync(app, tenant, ctx);
    }

    private static async Task<Harbor> ContinueAsync(DemoApp app, Guid tenant, (Guid Org, Guid Concrete, Guid Site, Guid Supplier) ctx)
    {
        var (org, concrete, site, supplier) = ctx;
        // Each person acts in a scope of their own: access is judged for one signed-in user per scope.
        Task<T> As<T>(string key, Func<IServiceProvider, Task<T>> work) => app.WithTenantAsync(tenant, async sp =>
        {
            sp.GetRequiredService<CurrentUser>().Set((await sp.GetRequiredService<IUserService>().FindByEmailAsync(key + "@hbl.example.test"))!.Id);
            return await work(sp);
        });
        Task As2(string key, Func<IServiceProvider, Task> work) => As(key, async sp => { await work(sp); return true; });
        var created = new List<Guid>();
        async Task<Guid> New(string title, decimal value)
        {
            var id = await As("hbl.manager", sp => sp.GetRequiredService<ISecuredContractService>().CreateDraftAsync(new CreateContractRequest(org, "HBL-" + (created.Count + 1).ToString("000000"), title, supplier, concrete, CurrencyCode: "USD",
                EstimatedValue: value, RiskLevel: "Low", ContractType: "LUMP_SUM", SiteId: site, EndDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(200)),
                CustomValues: new Dictionary<string, string?> { ["project_number"] = "P-2026-01" })));
            created.Add(id); return id;
        }
        var active = await New("Foundation concrete", 100_000m);
        await As2("hbl.manager", sp => sp.GetRequiredService<ILifecycleService>().TransitionAsync(new TransitionRequest(active, "SubmitForInternalReview")));
        await As2("hbl.commercial", sp => sp.GetRequiredService<ILifecycleService>().TransitionAsync(new TransitionRequest(active, "ReleaseForSignature")));
        await As2("hbl.manager", sp => sp.GetRequiredService<ISecuredContractDocuments>().AddAsync(new AddContractDocumentRequest(active, "FinalAgreement", "Subcontract agreement", "agreement.pdf", "application/pdf", new MemoryStream("agreement"u8.ToArray()))));
        await As2("hbl.manager", sp => sp.GetRequiredService<ISignatureRequestService>().RequestSignatureAsync(SubjectKinds.Contract, active, "mock"));
        await As2("hbl.manager", sp => sp.GetRequiredService<ISignatureRequestService>().SimulateEventAsync(SubjectKinds.Contract, active, SignatureRequestStatus.Completed));
        var request = await As("hbl.manager", sp => sp.GetRequiredService<IRegisterAuthorisationService>().SubmitAsync(SubjectKinds.Contract, active, "Executed copy attached"));
        await As2("hbl.director", sp => sp.GetRequiredService<IRegisterAuthorisationService>().ApproveAsync(request, "Approved"));
        var co = await As("hbl.manager", sp => sp.GetRequiredService<IVariationService>().CreateAsync(active, new VariationInput("Extra rebar", "Synthetic", "Owner directed", null, false, null, null, null,
            new Dictionary<string, string?> { ["estimatedValue"] = "120000", ["cf.change_order_type"] = "VALUE" })));
        var slab = await New("Slab on grade", 50_000m);
        return new Harbor(tenant, org, active, slab, co, "hbl.manager@hbl.example.test", "hbl.commercial@hbl.example.test", "hbl.director@hbl.example.test", "hbl.viewer@hbl.example.test", "hbl.admin@hbl.example.test", "hbl.auditor@hbl.example.test");
    }

    /// <summary>The words a reader sees: scripts, styles, inputs, attributes and code elements (stable identifiers) removed.</summary>
    private static string Visible(string html)
    {
        var text = Regex.Replace(html, "<script.*?</script>|<style.*?</style>|<code.*?</code>|<input[^>]*>|<option[^>]*>[^<]*</option>|<title>.*?</title>", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(Regex.Replace(text, @"\s+", " "));
    }

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The reference business's words and identifiers, the Australian locale, and the neutral platform words the pack replaces. Word-boundary matches, so "Subcontract" never matches "Contract".</summary>
    private static readonly Regex Leaks = new(
        @"\b(JBS|JBSAUS|ABN|SAP|AUD|Brisbane|Sydney|Melbourne|Perth|Australia|Australian|Procurement|Supplier|Suppliers|Counterparty|Counterparties|Category|Categories|Variation|Variations|Contract|Contracts|Agreement|Agreements|Legal Privileged)\b|en-AU|A\$",
        RegexOptions.Compiled | RegexOptions.NonBacktracking);

    [Fact]
    public async Task The_pages_a_person_uses_speak_only_the_organisations_words_in_US_formats_for_every_role()
    {
        using var app = DemoApp.Start();
        var h = await SetUpAsync(app);
        var org = h.Org.ToString();

        var pages = new[]
        {
            "/", "/Contracts", $"/Contracts?org={org}", $"/Contracts/Details/{h.Active}", $"/Contracts/Details/{h.Draft}", $"/Contracts/Lifecycle/{h.Active}", $"/Contracts/Create?organisationId={org}", $"/Contracts/Edit/{h.Draft}",
            "/Suppliers", "/Suppliers/Create", "/Approvals", "/Variations", "/Dashboards", "/Notifications"
        };
        // Neutral platform words the pack replaces must not appear; some are allowed where the platform names its own admin tools (listed with the reason).
        foreach (var email in new[] { h.Manager, h.Commercial, h.Director, h.Viewer })
        {
            using var client = await app.SignInAsync(email, followRedirects: true);
            foreach (var url in pages)
            {
                var response = await client.GetAsync(url);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) continue;                // a page this role does not open
                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{email} {url} -> {(int)response.StatusCode}");
                var text = Visible(await response.Content.ReadAsStringAsync());
                var hit = Leaks.Match(text);
                Assert.False(hit.Success, $"{email} {url}: '{hit.Value}' in …{text[Math.Max(0, hit.Index - 60)..Math.Min(text.Length, hit.Index + 60)]}…");
            }
        }

        // The pack's own words are there.
        using var manager = await app.SignInAsync(h.Manager, followRedirects: true);
        var details = Visible(await BodyAsync(manager, $"/Contracts/Details/{h.Active}"));
        Assert.Contains("Subcontractor", details); Assert.Contains("Trade Package", details); Assert.Contains("Project", details);
        Assert.Contains("Scheduled Completion Date", details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Change Order", Visible(await BodyAsync(manager, $"/Variations/Details/{h.ChangeOrder}")));
        // US formats: dollars with grouping on the contract page.
        Assert.Matches(@"\$?100,000(\.00)?", details);
        // Navigation: no formal review and no extension pages for this organisation.
        var home = Visible(await BodyAsync(manager, "/"));
        Assert.DoesNotContain("Formal Review", home); Assert.DoesNotContain("Extensions", home);
        Assert.Contains("Harborline Subcontract Hub", await BodyAsync(manager, "/"));                       // branding words from the pack's application name
    }

    [Fact]
    public async Task Administration_pages_for_the_organisation_show_its_wording_its_capabilities_its_matrices_and_a_role_picker_without_the_hidden_role()
    {
        using var app = DemoApp.Start();
        var h = await SetUpAsync(app);
        using var admin = await app.SignInAsync(h.Admin, followRedirects: true);
        var org = h.Org.ToString();

        // Capabilities: the pack's defaults and its notes.
        var caps = Visible(await BodyAsync(admin, $"/Admin/Capabilities?organisationId={org}"));
        Assert.Contains("Commercial staff negotiate standard terms", caps);
        Assert.Contains("Change orders are the central event", caps);
        // Approval matrices in force, in plain language, naming the pack's groups.
        var matrices = Visible(await BodyAsync(admin, $"/Admin/Approvals?organisationId={org}"));
        Assert.Contains("Subcontract activation approval", matrices); Assert.Contains("Change order approval", matrices);
        Assert.Contains("Project Directors", matrices);
        Assert.DoesNotMatch(@"\bJBS\b|Procurement|GroupHead", matrices);
        // The simulator and delegation pages open.
        foreach (var url in new[] { $"/Admin/Approvals/Simulate?organisationId={org}", $"/Admin/Approvals/Delegations?organisationId={org}" })
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
        // Terminology: the organisation's published wording and the pack's role visibility.
        var terms = Visible(await BodyAsync(admin, $"/Admin/Terminology?organisationId={org}"));
        Assert.Contains("Subcontract", terms); Assert.Contains("Project Director", terms);
        // The role picker (the grant form of a person's page) offers the roles under the business's own names and not the one it hid; existing grants of the hidden role elsewhere in the tenant still show in lists.
        var (_, managerId) = await app.FindUserAsync(h.Manager);
        var page = await BodyAsync(admin, $"/Admin/Users/Details/{managerId}");
        var picker = Regex.Match(page, "<select id=\"role\".*?</select>", RegexOptions.Singleline).Value;
        Assert.NotEmpty(picker);
        Assert.Contains("Package Manager (CategoryOwner)", WebUtility.HtmlDecode(picker));
        Assert.Contains("Project Director (Approver)", WebUtility.HtmlDecode(picker));
        Assert.DoesNotContain("(Legal)", picker);
        Assert.DoesNotContain("Formal Reviewer", picker);
        // Pack Manager: the pack is in the catalogue as a synthetic, unsigned sample, with its documents listed.
        var packs = Visible(await BodyAsync(admin, "/Admin/Packs"));
        Assert.Contains("Construction Subcontracts (synthetic example)", packs);
        var details = Visible(await BodyAsync(admin, $"/Admin/Packs/Details?key={Pack}&version=1.0.0"));
        Assert.Contains("NOT SIGNED", details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("synthetic", details, StringComparison.OrdinalIgnoreCase);
        // Onboarding opens for the organisation and speaks its words: pack step, structure, categories (trade packages), contract types, owners and the review.
        foreach (var url in new[] { $"/Onboarding?org={org}", $"/Onboarding/Step/pack?org={org}", $"/Onboarding/Step/structure?org={org}", $"/Onboarding/Step/categories?org={org}", $"/Onboarding/Step/types?org={org}", $"/Onboarding/Review?org={org}" })
        {
            var response = await admin.GetAsync(url);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
            var text = Visible(await response.Content.ReadAsStringAsync());
            if (!url.Contains("/Step/pack", StringComparison.Ordinal))                                            // the pack step lists every available pack by name, by design
                Assert.DoesNotMatch(@"\b(ABN|JBS|SAP|Brisbane|Procurement)\b", text);
        }
        var packStep = Visible(await BodyAsync(admin, $"/Onboarding/Step/pack?org={org}"));
        Assert.Contains("Construction Subcontracts", packStep);
        Assert.Contains("Commercial staff negotiate standard terms", packStep);                                // the pack's note on the optional steps it switches off
    }

    [Fact]
    public async Task Exports_headings_and_notifications_for_the_organisation_use_its_words_and_US_dates()
    {
        using var app = DemoApp.Start();
        var h = await SetUpAsync(app);
        using var commercial = await app.SignInAsync(h.Commercial, followRedirects: true);
        async Task<HttpResponseMessage> Export(string format) => await DemoApp.PostFormAsync(commercial, "/Contracts", new Dictionary<string, string>
            { ["View"] = "system:all-contracts", ["Org"] = h.Org.ToString(), ["ExportFormat"] = format, ["ExportLarge"] = "false" }, "/Contracts?handler=Export");
        var csv = await Export("csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var header = (await csv.Content.ReadAsStringAsync()).Split('\n')[0];
        Assert.Contains("Subcontract Number", header); Assert.Contains("Subcontractor", header); Assert.Contains("Trade Package", header);
        Assert.DoesNotMatch(@"\b(Contract|Supplier|Category|Agreement|ABN|SAP)\b", header);
        var xlsx = await Export("xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync()));
        using var workbook = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Contains("name=\"Subcontracts\"", await workbook.ReadToEndAsync());

        // The notification inbox of the package manager carries the approval in the pack's words.
        using var manager = await app.SignInAsync(h.Manager, followRedirects: true);
        await app.WithTenantAsync(h.Tenant, async sp => { await sp.GetRequiredService<Ecp.Notifications.INotificationJobs>().RunAllAsync(); return 0; });
        var inbox = Visible(await BodyAsync(manager, "/Notifications"));
        Assert.DoesNotMatch(@"\b(Variation|Legal|Agreement|Contract)\b", inbox);
    }

    [Fact]
    public async Task Readiness_lists_three_shipped_packs_and_finds_their_capability_defaults_coherent()
    {
        // SBP-AC-03: the readiness checks that read every shipped pack stay Healthy with the third pack in the folder.
        using var app = DemoApp.Start();
        using var client = app.NewClient();
        var response = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var checks = doc.RootElement.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!, c => c);
        Assert.Equal("Healthy", checks["capabilities"].GetProperty("status").GetString());
        Assert.Equal("Healthy", checks["solution-packs"].GetProperty("status").GetString());
        Assert.Contains("3 shipped solution pack", checks["solution-packs"].GetProperty("message").GetString());
    }
}
