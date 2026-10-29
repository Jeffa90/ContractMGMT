using System.Net;
using System.Text.RegularExpressions;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Setup;
using Ecp.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The manual acceptance scenario of the owner's final review, automated end to end over HTTP on a freshly set-up Test-mode installation. The administrator never types or sees a role code, a permission
/// code, a viewer kind, a group key, a scope intersection, CSV, JSON or an identity-provider claim. Where the product cannot do a step by guided screens, the test records it as a UX gap (in the
/// <c>Gaps</c> list printed in the failure message) instead of reaching into an Advanced page.
/// </summary>
[Collection("Api")]
public sealed class ManualAcceptanceScenarioTests
{
    private const string AdminEmail = "first.admin@acceptance.example.invalid";
    private static readonly string[] Jargon = ["CategoryOwner", "ProcurementAdministrator", "OrganisationAdministrator", "viewerKind", "ViewerKind", "groups claim", "contracts.create", "users.manage", "roles.delegate", "groupKey"];

    private static Dictionary<string, string?> Uninstalled(string dir) => new()
    {
        ["Ecp:Mode"] = "Test", ["Ecp:DemoTenantResolution"] = "true", ["Ecp:DemoSeed"] = "false",
        ["Ecp:DataDirectory"] = dir, ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "ecp.db")}",
        ["Database:ConnectionStringRef"] = "", ["Database:AutoMigrate"] = "true",
    };

    private static async Task SetUpAsync(DemoApp app, Dictionary<string, string> admin)
    {
        using var setup = app.NewClient();
        var token = await app.Factory.Services.GetRequiredService<ISetupService>().IssueTokenAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(setup, "/Setup", new Dictionary<string, string> { ["token"] = token })).StatusCode);
        var steps = new (string Id, Dictionary<string, string> Fields)[]
        {
            ("mode", new() { ["deployment"] = "Local", ["tenancyMode"] = "SingleOrganisation", ["acknowledge"] = "true" }), ("database", new()), ("storage", new()),
            ("tenant", new() { ["tenantName"] = "Acceptance Tenant" }), ("pack", new() { ["packId"] = "jbs-procurement" }), ("identity", new() { ["kind"] = "Demo" }),
            ("admin", admin),
            ("organisation", new() { ["organisationName"] = "Acceptance Organisation", ["culture"] = "en-AU", ["timeZone"] = "Australia/Brisbane", ["currency"] = "AUD", ["referencePrefix"] = "ACC" }),
            ("branding", new() { ["productName"] = "Acceptance Platform", ["accentColour"] = "#14532d", ["footerText"] = "Synthetic" }),
            ("classification", new() { ["levels"] = "Public\nInternal\nConfidential\nRestricted\nLegal Privileged", ["defaultLevel"] = "Internal" }),
            ("notifications", new() { ["provider"] = "Mock", ["from"] = "platform@acceptance.example.invalid", ["testRecipient"] = AdminEmail }),
        };
        foreach (var (id, fields) in steps)
        {
            var r = await DemoApp.PostFormAsync(setup, $"/Setup/Step/{id}", fields);
            var body = await r.Content.ReadAsStringAsync();
            Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK && !body.Contains("This step was not completed"), $"set-up step {id} was not completed");
        }
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(setup, "/Setup/Step/demo", new Dictionary<string, string>(), "/Setup/Step/demo?handler=Skip")).StatusCode);
        var ack = new Dictionary<string, string> { ["ack.R-NOT-01"] = "Mock mail is acceptable for this synthetic installation.", ["ack.R-OPS-01"] = "No scanner is connected; acceptable for this synthetic installation." };
        await DemoApp.PostFormAsync(setup, "/Setup/Step/readiness", ack);
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(setup, "/Setup/Step/complete", new Dictionary<string, string>())).StatusCode);
    }

    private static async Task<string> PageAsync(HttpClient client, string url, List<string> seen)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync();
        foreach (var word in Jargon) Assert.DoesNotContain(word, html);
        seen.Add(url);
        return html;
    }

    private static string Snip(string html)
    {
        var main = Regex.Replace(Regex.Match(html, "<main.*?</main>", RegexOptions.Singleline).Value, "<[^>]+>|\\s+", " ");
        return main[..Math.Min(900, main.Length)];
    }

    private static async Task<string> StepAsync(HttpClient client, string handler, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var response = await DemoApp.PostFormAsync(client, "/Admin/People/Access", fields, $"/Admin/People/Access?handler={handler}");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, $"{handler} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }


    /// <summary>What the product cannot do by guided screens in this build, recorded rather than worked around. The test fails if the list changes, so the documentation is kept honest.</summary>
    private static readonly string[] KnownGaps =
    [
        "A new organisation has no categories, so + New contract is not offered until one is added and owned (ownership chosen at set-up waits for categories and is not applied automatically).",
        "Approver groups are created on a page that asks for a 'Group key'; no guided path creates the first group, so approval authorities cannot be added until an administrator types one.",
        "Publishing the set-up is refused until approval rules exist: the reference pack ships none and the only way to author them is the approval-matrix form editor under Workflows & Approvals.",
    ];

    private sealed record WizardResult(string? PersonUrl, string Review, List<string> Gaps);

    /// <summary>Drives the Add person wizard one question at a time, as a browser would, with only the answers a business administrator gives.</summary>
    private static async Task<WizardResult> WizardAsync(HttpClient client, string org, string name, string email, bool allowDemo, string[] profiles, string scope, string[] scopeIds, string ownership, string[] owned, string ceiling, bool approverGroup)
    {
        var gaps = new List<string>();
        var fields = new List<KeyValuePair<string, string>> { new("Input.DisplayName", name), new("Input.Email", email), new("Input.OrganisationId", org), new("Input.Scope", scope), new("Input.Ownership", ownership), new("Input.Ceiling", ceiling) };
        if (allowDemo) fields.Add(new("Input.AllowDemo", "true"));
        fields.AddRange(profiles.Select(p => new KeyValuePair<string, string>("Input.Profiles", p)));
        fields.AddRange(scopeIds.Select(i => new KeyValuePair<string, string>("Input.ScopeIds", i)));
        fields.AddRange(owned.Select(i => new KeyValuePair<string, string>("Input.OwnedIds", i)));
        string review = "";
        for (var step = 1; step <= 6; step++)
        {
            if (step == 4 && !profiles.Any(p => p is "contract-manager" or "contract-administrator")) continue;
            if (step == 6 && !approverGroup) continue;
            if (step == 6)
            {
                var groupPage = review;
                var key = Regex.Match(groupPage, "<option value=\"([^\"]+)\"").Groups[1].Value;
                if (key.Length == 0) { gaps.Add("no approver group is offered: " + Snip(groupPage)); return new(null, review, gaps); }
                fields.Add(new("Input.ApproverGroup", key));
            }
            var posted = fields.Append(new("Step", step.ToString())).ToList();
            review = await StepAsync(client, "Next", posted);
        }
        if (!review.Contains("Check before you save")) { gaps.Add($"{name}: the wizard did not reach the review: " + Snip(review)); return new(null, review, gaps); }
        var saved = await DemoApp.PostFormAsync(client, "/Admin/People/Access", fields.Append(new("Step", "7")), "/Admin/People/Access?handler=Save");
        if (saved.StatusCode != HttpStatusCode.Redirect) { gaps.Add($"{name}: could not be saved: " + Snip(await saved.Content.ReadAsStringAsync())); return new(null, review, gaps); }
        return new(saved.Headers.Location!.ToString(), review, gaps);
    }

    [Fact]
    public async Task A_fresh_Test_mode_organisation_an_initial_administrator_and_a_synthetic_Jeffrey_end_to_end()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ecp-acceptance-" + Guid.NewGuid().ToString("N"))).FullName;
        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(dir)) s[k] = v; });
        var pages = new List<string>();

        // A and B. A fresh Test-mode organisation; the initial administrator chooses what they will do (the boxes as a browser posts them when ticked).
        await SetUpAsync(app, new Dictionary<string, string>
            { ["displayName"] = "First Administrator", ["email"] = AdminEmail, ["installationAdmin"] = "true", ["doManage"] = "true", ["doOwn"] = "true" });

        using var admin = await app.SignInAsync(AdminEmail);
        var (tenantId, adminId) = await app.FindUserAsync(AdminEmail);

        // C and D. What access results, in plain English, and saving it. In this build the choice is saved by set-up itself; the review of the result is on My access.
        var mine = await PageAsync(admin, "/Access", pages);
        Assert.Contains("Create contracts", mine);

        // E. Home shows the work cards. A new organisation has no category yet, so + New contract appears once there is one to own: the administrator adds the first category and becomes its owner by guided screens.
        var home0 = await PageAsync(admin, "/", pages);
        var gaps = new List<string>();
        if (!home0.Contains("href=\"/Contracts/Create\"")) gaps.Add("A new organisation has no categories, so + New contract is not offered until one is added and owned (ownership chosen at set-up waits for categories and is not applied automatically).");
        var orgId = await app.WithTenantAsync(tenantId, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single().Id);
        Assert.Contains("Open Administration", home0);

        var cats = await PageAsync(admin, "/Admin/Categories", pages);
        var created = new List<string>();
        foreach (var (code, name) in new[] { ("PACKAGING", "Packaging"), ("ENERGY", "Energy") })
        {
            var r = await DemoApp.PostFormAsync(admin, "/Admin/Categories", new Dictionary<string, string> { ["OrganisationId"] = orgId.ToString(), ["Code"] = code, ["Name"] = name, ["ParentCategoryId"] = "" }, "/Admin/Categories?handler=Create");
            Assert.True(r.StatusCode == HttpStatusCode.Redirect, $"adding {name}: {(int)r.StatusCode}");
            created.Add(name);
        }
        cats = await PageAsync(admin, "/Admin/Categories", pages);
        var allIds = Regex.Matches(cats, "name=\"CategoryIds\" value=\"([0-9a-f-]{36})\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(2, allIds.Count);

        // The administrator makes themself the owner of every category from the ownership screen (assign all), reviewing first.
        var fields = new List<KeyValuePair<string, string>> { new("OrganisationId", orgId.ToString()), new("UserId", adminId.ToString()), new("Role", "primary") };
        var planAll = await DemoApp.PostFormAsync(admin, "/Admin/Categories", fields, "/Admin/Categories?handler=PlanAll");
        var planHtml = await planAll.Content.ReadAsStringAsync();
        Assert.True(planAll.StatusCode == HttpStatusCode.OK && planHtml.Contains("Check before you save"), "plan: " + Snip(planHtml));
        var saveFields = new List<KeyValuePair<string, string>>(fields);
        saveFields.AddRange(allIds.Select(c => new KeyValuePair<string, string>("CategoryIds", c)));
        var saved = await DemoApp.PostFormAsync(admin, "/Admin/Categories", saveFields, "/Admin/Categories?handler=SetOwner");
        Assert.True(saved.StatusCode == HttpStatusCode.Redirect, "owner save: " + (int)saved.StatusCode + " " + Snip(await saved.Content.ReadAsStringAsync()));

        // The organisation's starting configuration (from its pack) waits for an administrator to review and publish it; until then the work cards cannot be built. The Set-up checklist lists what
        // is outstanding. Approvers are people, so a synthetic approver is added by the same wizard; the pack's workflow, notification and approval rules are each published from their page.
        var org = orgId.ToString();
        var checklist = await PageAsync(admin, $"/Onboarding?org={org}", pages);
        Assert.Contains("Required", checklist);

        // F (first of two people). A synthetic approver, through the wizard: Approval Authority, whole organisation, Internal.
        var group = await DemoApp.PostFormAsync(admin, "/Admin/ApproverGroups", new Dictionary<string, string> { ["Key"] = "GROUP-HEAD", ["Description"] = "Group heads", ["OrganisationId"] = org, ["AllowDelegation"] = "true" }, "/Admin/ApproverGroups?handler=Create");
        Assert.Equal(HttpStatusCode.Redirect, group.StatusCode);
        gaps.Add("Approver groups are created on a page that asks for a 'Group key'; no guided path creates the first group, so approval authorities cannot be added until an administrator types one.");
        var approverEmail = "pat.approver@acceptance.example.invalid";
        var approverPage = await WizardAsync(admin, org, "Pat Approver", approverEmail, allowDemo: true, ["approval-authority"], "WholeOrganisation", [], "None", [], "Internal", approverGroup: true);
        gaps.AddRange(approverPage.Gaps);

        var validate = await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={org}", new Dictionary<string, string> { ["Org"] = org }, $"/Onboarding/Review?handler=Validate&org={org}");
        var publish = await DemoApp.PostFormAsync(admin, $"/Onboarding/Review?org={org}", new Dictionary<string, string> { ["Org"] = org, ["Note"] = "First release" }, $"/Onboarding/Review?handler=Publish&org={org}");
        if (publish.StatusCode != HttpStatusCode.Redirect)
        {
            // The readiness gate is doing its job: the pack asks for reviewed approval rules and the JBS reference pack ships none. Reported as a gap, not bypassed.
            Assert.Contains("Not ready to publish", await publish.Content.ReadAsStringAsync());
            gaps.Add("Publishing the set-up is refused until approval rules exist: the reference pack ships none and the only way to author them is the approval-matrix form editor under Workflows & Approvals.");
        }

        // E. Home now shows + New contract, My Contracts, Reviews Due, Expiring Soon and (in the navigation) My Actions.
        var home = await PageAsync(admin, "/", pages);
        Assert.Contains("href=\"/Contracts/Create\"", home);
        if (!(home.Contains("My Contracts") && home.Contains("Reviews Due") && home.Contains("Expiring Soon"))) gaps.Add("cards missing: " + string.Join(" | ", Regex.Matches(home, "<h2[^>]*>(.*?)</h2>", RegexOptions.Singleline).Select(m => Regex.Replace(m.Groups[1].Value, "<[^>]+>|\\s+", " "))));
        Assert.Contains("href=\"/Actions\"", home);

        // Regional settings (identifier and address formats) are published from their page; empty fields adopt the pack's proposal.
        var regional = await DemoApp.PostFormAsync(admin, $"/Admin/Regional?organisationId={org}", new Dictionary<string, string> { ["OrganisationId"] = org, ["Note"] = "Reviewed at set-up" }, $"/Admin/Regional?handler=Publish&organisationId={org}");
        if (regional.StatusCode != HttpStatusCode.Redirect) gaps.Add("regional settings not published: " + Snip(await regional.Content.ReadAsStringAsync()));

        // The first supplier and a contract in each category, by the administrator through the ordinary screens.
        var supplierForm = new Dictionary<string, string> { ["OrganisationId"] = org, ["Name"] = "Acceptance Supplier Pty Ltd" };
        // The form asks for the tax identifier it needs (the field name is the form's own); a format-valid synthetic value is typed.
        if (Regex.Match(await admin.GetStringAsync("/Suppliers/Create"), "name=\"(Identifier\\[[^\"]+\\])\"").Groups[1] is { Success: true } idField) supplierForm[idField.Value] = "51824753556";
        var supplier = await DemoApp.PostFormAsync(admin, "/Suppliers/Create", supplierForm);
        if (supplier.StatusCode != HttpStatusCode.Redirect) gaps.Add("supplier not added: " + Snip(await supplier.Content.ReadAsStringAsync()));
        var supplierId = supplier.Headers.Location is { } loc ? Regex.Match(loc.ToString(), "[0-9a-f]{8}-[0-9a-f-]{27}").Value : "";
        var byCode = await app.WithTenantAsync(tenantId, async sp => (await sp.GetRequiredService<Ecp.Contracts.ICategoryAdministration>().ListAllAsync()).ToDictionary(c => c.Code, c => c.Id.ToString()));
        var packagingId = byCode.GetValueOrDefault("PACKAGING"); var energyId = byCode.GetValueOrDefault("ENERGY");
        Assert.True(packagingId is not null && energyId is not null && packagingId != energyId, "category ids: " + string.Join(",", allIds));

        var adminEnergy = await DemoApp.PostFormAsync(admin, "/Contracts/Create", new Dictionary<string, string>
            { ["OrganisationId"] = org, ["Title"] = "Energy supply (acceptance)", ["SupplierId"] = supplierId, ["CategoryId"] = energyId!, ["Classification"] = "Internal" });
        if (adminEnergy.StatusCode != HttpStatusCode.Redirect) gaps.Add("administrator could not create an Energy contract: " + Snip(await adminEnergy.Content.ReadAsStringAsync()));
        var energyContractUrl = adminEnergy.Headers.Location?.ToString() ?? "";

        // F (second person). Jeffrey: Contract Manager, this organisation, selected categories (Packaging), Internal ceiling, demonstration sign-in allowed.
        var jeffreyEmail = "jeffrey@acceptance.example.invalid";
        var jeffrey = await WizardAsync(admin, org, "Jeffrey", jeffreyEmail, allowDemo: true, ["contract-manager"], "Categories", [packagingId!], "Selected", [packagingId!], "Internal", approverGroup: false);
        gaps.AddRange(jeffrey.Gaps);
        Assert.True(jeffrey.PersonUrl is not null, "Jeffrey was not saved: " + string.Join("; ", jeffrey.Gaps));
        Assert.Contains("Check before you save", jeffrey.Review);
        Assert.Contains("Jeffrey", jeffrey.Review);

        // I. Jeffrey is on the Demonstration sign-in page.
        using var anonymous = app.NewClient();
        var signInPage = await anonymous.GetStringAsync("/SignIn");
        Assert.Contains("Jeffrey", signInPage);

        // J and K. Preview as Jeffrey.
        var personUrl = jeffrey.PersonUrl!;
        var preview = await DemoApp.PostFormAsync(admin, personUrl, new Dictionary<string, string> { ["reason"] = "Check what Jeffrey sees" }, personUrl + "?handler=Preview");
        Assert.Equal(HttpStatusCode.Redirect, preview.StatusCode);
        var previewHome = await PageAsync(admin, "/", pages);
        Assert.Contains("TEST MODE", previewHome); Assert.Contains("Previewing as", previewHome); Assert.Contains("Exit user preview", previewHome);
        Assert.Contains("href=\"/Contracts/Create\"", previewHome);
        Assert.DoesNotContain("admin-menu", previewHome);
        Assert.DoesNotContain("Open Administration", previewHome);
        var register = await PageAsync(admin, "/Contracts", pages);
        Assert.Contains("Energy supply (acceptance)", register);                                 // a Category Owner reads every contract in the organisation within the ceiling (confirmed decision); editing is limited to owned categories
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/Admin/People")).StatusCode);
        if (energyContractUrl.Length > 0) Assert.NotEqual(HttpStatusCode.OK, (await admin.GetAsync(energyContractUrl.Replace("/Details/", "/Edit/"))).StatusCode);
        var write = await DemoApp.PostFormAsync(admin, "/Suppliers/Create", new Dictionary<string, string> { ["OrganisationId"] = org, ["Name"] = "Written in preview" });
        Assert.True(write.StatusCode != HttpStatusCode.Redirect, "a write in preview was accepted");

        // L. Exit preview with the banner's own button.
        var bannerForm = Regex.Match(await PageAsync(admin, "/", pages), "<form[^>]*action=\"/Account/ExitPreview\"[^>]*>.*?</form>", RegexOptions.Singleline).Value;
        var exit = await admin.PostAsync("/Account/ExitPreview", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = DemoApp.ExtractToken(bannerForm)! }));
        Assert.Equal(HttpStatusCode.Redirect, exit.StatusCode);
        Assert.DoesNotContain("Previewing as", await PageAsync(admin, "/", pages));

        // M. Sign out, then sign in as Jeffrey through demonstration sign-in.
        var signOut = await DemoApp.PostFormAsync(admin, "/", new Dictionary<string, string>(), "/SignOut");
        Assert.True(signOut.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, "sign-out " + (int)signOut.StatusCode);
        using var jeffreyClient = await app.SignInAsync(jeffreyEmail);

        // N. Jeffrey creates a contract in Packaging.
        var okHome = await PageAsync(jeffreyClient, "/", pages);
        Assert.Contains("href=\"/Contracts/Create\"", okHome);
        Assert.DoesNotContain("Open Administration", okHome);
        var created1 = await DemoApp.PostFormAsync(jeffreyClient, "/Contracts/Create", new Dictionary<string, string>
            { ["OrganisationId"] = org, ["Title"] = "Packaging film (Jeffrey)", ["SupplierId"] = supplierId, ["CategoryId"] = packagingId!, ["Classification"] = "Internal" });
        if (created1.StatusCode != HttpStatusCode.Redirect) gaps.Add("Jeffrey could not create a Packaging contract: " + Snip(await created1.Content.ReadAsStringAsync()));

        // O. Jeffrey cannot create or edit outside his category.
        var created2 = await DemoApp.PostFormAsync(jeffreyClient, "/Contracts/Create", new Dictionary<string, string>
            { ["OrganisationId"] = org, ["Title"] = "Energy (Jeffrey, should be refused)", ["SupplierId"] = supplierId, ["CategoryId"] = energyId!, ["Classification"] = "Internal" });
        Assert.NotEqual(HttpStatusCode.Redirect, created2.StatusCode);
        if (energyContractUrl.Length > 0)
        {
            var edit = await jeffreyClient.GetAsync(energyContractUrl.Replace("/Details/", "/Edit/"));
            Assert.NotEqual(HttpStatusCode.OK, edit.StatusCode);
        }

        Assert.True(gaps.SequenceEqual(KnownGaps), "GAPS:\n" + string.Join("\n", gaps));
    }
}
