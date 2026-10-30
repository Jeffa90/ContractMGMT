using System.Net;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Contracts;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Setup;
using Ecp.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// The owner's manual acceptance journey, automated end to end over HTTP on a freshly set-up Test-mode installation of the JBS reference pack. The administrator never types or sees a role code, a permission
/// code, a viewer kind, a group key, a scope intersection, CSV, JSON or an identity-provider claim: every page of the journey is scanned for them. Where the product cannot do a step by guided screens the
/// step fails here, rather than the test reaching into an Advanced page.
/// </summary>
[Collection("Api")]
public sealed class ManualAcceptanceScenarioTests
{
    private const string AdminEmail = "first.admin@acceptance.example.invalid";
    private static readonly string[] Jargon = ["CategoryOwner", "ProcurementAdministrator", "OrganisationAdministrator", "TenantAdministrator", "viewerKind", "ViewerKind", "groups claim", "contracts.create", "users.manage", "roles.delegate", "groupKey"];
    /// <summary>Machine keys of approval authorities: they may travel in an address or a hidden field, but nobody is shown one (the page text is checked).</summary>
    private static readonly string[] VisibleJargon = ["GroupHeadProcurement", "TEST-APPROVERS", "INITIAL-APPROVERS"];

    private static Dictionary<string, string?> Uninstalled(string dir) => new()
    {
        ["Ecp:Mode"] = "Test", ["Ecp:DemoTenantResolution"] = "true", ["Ecp:DemoSeed"] = "false",
        ["Ecp:DataDirectory"] = dir, ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "ecp.db")}",
        ["Database:ConnectionStringRef"] = "", ["Database:AutoMigrate"] = "true",
    };

    private static async Task<HttpClient> OpenSetupAsync(DemoApp app)
    {
        var setup = app.NewClient();
        var token = await app.Factory.Services.GetRequiredService<ISetupService>().IssueTokenAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(setup, "/Setup", new Dictionary<string, string> { ["token"] = token })).StatusCode);
        return setup;
    }

    private static async Task StepSetupAsync(HttpClient setup, string id, Dictionary<string, string> fields)
    {
        var r = await DemoApp.PostFormAsync(setup, $"/Setup/Step/{id}", fields);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK && !body.Contains("This step was not completed"), $"set-up step {id} was not completed: {Snip(body)}");
    }

    private static async Task<string> PageAsync(HttpClient client, string url, List<string> seen)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        var html = await response.Content.ReadAsStringAsync();
        foreach (var word in Jargon) Assert.DoesNotContain(word, html);
        foreach (var word in VisibleJargon) Assert.DoesNotContain(word, Text(html));
        seen.Add(url);
        return html;
    }

    private static string Snip(string html)
    {
        var main = Regex.Replace(Regex.Match(html, "<main.*?</main>", RegexOptions.Singleline).Value, "<[^>]+>|\\s+", " ");
        return main[..Math.Min(900, main.Length)];
    }

    private static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<(script|style).*?</\\1>", "", RegexOptions.Singleline), "<[^>]+>|\\s+", " "));

    private static async Task<string> StepAsync(HttpClient client, string handler, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var response = await DemoApp.PostFormAsync(client, "/Admin/People/Access", fields, $"/Admin/People/Access?handler={handler}");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect, $"{handler} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private sealed record WizardResult(string? PersonUrl, string Review);

    /// <summary>Drives the Add person wizard one question at a time, as a browser would, with only the answers a business administrator gives. The save needs the confirmation box ticked.</summary>
    private static async Task<WizardResult> WizardAsync(HttpClient client, string org, string name, string email, bool allowDemo, string[] profiles, string scope, string[] scopeIds, string ownership, string[] owned, string ceiling, bool approverGroup)
    {
        var fields = new List<KeyValuePair<string, string>> { new("Input.DisplayName", name), new("Input.Email", email), new("Input.OrganisationId", org), new("Input.Scope", scope), new("Input.Ownership", ownership), new("Input.Ceiling", ceiling) };
        if (allowDemo) fields.Add(new("Input.AllowDemo", "true"));
        fields.AddRange(profiles.Select(p => new KeyValuePair<string, string>("Input.Profiles", p)));
        fields.AddRange(scopeIds.Select(i => new KeyValuePair<string, string>("Input.ScopeIds", i)));
        fields.AddRange(owned.Select(i => new KeyValuePair<string, string>("Input.OwnedIds", i)));
        var review = "";
        for (var step = 1; step <= 6; step++)
        {
            if (step == 3 && profiles.All(p => p is "organisation-administrator")) continue;
            if (step == 4 && !profiles.Any(p => p is "contract-manager" or "contract-administrator")) continue;
            if (step == 6 && !approverGroup) continue;
            if (step == 6)
            {
                var key = Regex.Match(review, "<option value=\"([^\"]+)\"").Groups[1].Value;
                Assert.True(key.Length > 0, "no approval authority is offered by name: " + Snip(review));
                fields.Add(new("Input.ApproverGroup", key));
            }
            review = await StepAsync(client, "Next", fields.Append(new("Step", step.ToString())).ToList());
        }
        Assert.Contains("Review access", review);
        var saved = await DemoApp.PostFormAsync(client, "/Admin/People/Access", fields.Append(new("Step", "7")).Append(new("Input.Confirm", "true")), "/Admin/People/Access?handler=Save");
        Assert.True(saved.StatusCode == HttpStatusCode.Redirect, $"{name}: could not be saved: " + Snip(await saved.Content.ReadAsStringAsync()));
        return new(saved.Headers.Location!.ToString(), review);
    }

    [Fact]
    public async Task A_fresh_Test_mode_organisation_an_initial_administrator_and_a_synthetic_Jeffrey_end_to_end()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ecp-acceptance-" + Guid.NewGuid().ToString("N"))).FullName;
        using var app = DemoApp.Start(s => { foreach (var (k, v) in Uninstalled(dir)) s[k] = v; });
        var pages = new List<string>();

        // Create the organisation and configure the initial administrator (Administer, Create and manage, Own all current and future). Then REVIEW what that gives, and only then confirm.
        using var setup = await OpenSetupAsync(app);
        await StepSetupAsync(setup, "mode", new() { ["deployment"] = "Local", ["tenancyMode"] = "SingleOrganisation", ["acknowledge"] = "true" });
        await StepSetupAsync(setup, "database", new()); await StepSetupAsync(setup, "storage", new());
        await StepSetupAsync(setup, "tenant", new() { ["tenantName"] = "JBS TEST" });
        await StepSetupAsync(setup, "pack", new() { ["packId"] = "jbs-procurement" });
        await StepSetupAsync(setup, "identity", new() { ["kind"] = "Demo" });
        await StepSetupAsync(setup, "admin", new() { ["displayName"] = "First Administrator", ["email"] = AdminEmail, ["installationAdmin"] = "true", ["doManage"] = "true", ["doOwn"] = "true" });
        await StepSetupAsync(setup, "organisation", new() { ["organisationName"] = "JBS TEST", ["culture"] = "en-AU", ["timeZone"] = "Australia/Brisbane", ["currency"] = "AUD", ["referencePrefix"] = "ACC" });
        await StepSetupAsync(setup, "branding", new() { ["productName"] = "Acceptance Platform", ["accentColour"] = "#14532d", ["footerText"] = "Synthetic" });
        await StepSetupAsync(setup, "classification", new() { ["levels"] = "Public\nInternal\nConfidential\nRestricted\nLegal Privileged", ["defaultLevel"] = "Internal" });
        await StepSetupAsync(setup, "notifications", new() { ["provider"] = "Mock", ["from"] = "platform@acceptance.example.invalid", ["testRecipient"] = AdminEmail });

        var reviewText = Text(await PageAsync(setup, "/Setup/Step/access", pages));
        Assert.Contains("This person will be able to", reviewText);
        Assert.Contains("This person will not be able to", reviewText);
        Assert.Contains("Create contracts", reviewText);
        Assert.Contains("Administer the organisation", reviewText);
        Assert.Contains("Perform formal review", reviewText);
        Assert.Contains("Approve or sign off their own submissions", reviewText);
        Assert.Contains("Every current and future category in JBS TEST", reviewText);
        Assert.Contains("Up to", reviewText);                                                         // the classification ceiling
        Assert.DoesNotContain("Advanced details", reviewText);                                        // no codes anywhere on the set-up review
        var unconfirmed = await DemoApp.PostFormAsync(setup, "/Setup/Step/access", new Dictionary<string, string>());
        Assert.Contains("This step was not completed", await unconfirmed.Content.ReadAsStringAsync());   // nothing is given until confirmed
        await StepSetupAsync(setup, "access", new() { ["confirm"] = "true" });
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(setup, "/Setup/Step/demo", new Dictionary<string, string>(), "/Setup/Step/demo?handler=Skip")).StatusCode);
        await DemoApp.PostFormAsync(setup, "/Setup/Step/readiness", new Dictionary<string, string> { ["ack.R-NOT-01"] = "Mock mail is acceptable for this synthetic installation.", ["ack.R-OPS-01"] = "No scanner is connected; acceptable for this synthetic installation." });
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(setup, "/Setup/Step/complete", new Dictionary<string, string>())).StatusCode);

        // Reach Home. + New contract is there even though no category exists yet: the administrator owns every future one.
        using var admin = await app.SignInAsync(AdminEmail);
        var (tenantId, adminId) = await app.FindUserAsync(AdminEmail);
        var mine = Text(await PageAsync(admin, "/Access", pages));
        Assert.Contains("Create contracts", mine);
        Assert.Contains("every current and future", mine);
        var home0 = await PageAsync(admin, "/", pages);
        Assert.Contains("href=\"/Contracts/Create\"", home0);
        Assert.Contains("href=\"/Actions\"", home0);
        foreach (var card in new[] { "My Contracts", "Reviews Due", "Expiring Soon" }) Assert.Contains(card, home0);
        var orgId = await app.WithTenantAsync(tenantId, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single().Id);
        var org = orgId.ToString();

        // Create categories after set-up. The administrator owns each automatically, with no ownership step.
        foreach (var (code, name) in new[] { ("PACKAGING", "Packaging"), ("ENERGY", "Energy") })
        {
            var r = await DemoApp.PostFormAsync(admin, "/Admin/Categories", new Dictionary<string, string> { ["OrganisationId"] = org, ["Code"] = code, ["Name"] = name, ["ParentCategoryId"] = "" }, "/Admin/Categories?handler=Create");
            Assert.True(r.StatusCode == HttpStatusCode.Redirect, $"adding {name}: {(int)r.StatusCode}");
        }
        var byCode = await app.WithTenantAsync(tenantId, async sp => (await sp.GetRequiredService<ICategoryAdministration>().ListAllAsync()).ToDictionary(c => c.Code, c => c));
        var packagingId = byCode["PACKAGING"].Id.ToString(); var energyId = byCode["ENERGY"].Id.ToString();
        Assert.Contains(byCode["PACKAGING"].Owners, o => o.UserId == adminId);
        Assert.Contains(byCode["ENERGY"].Owners, o => o.UserId == adminId);

        // A supplier, then the administrator's own contract in Energy (so there is something Jeffrey must not be able to edit).
        var supplierForm = new Dictionary<string, string> { ["OrganisationId"] = org, ["Name"] = "Acceptance Supplier Pty Ltd" };
        if (Regex.Match(await admin.GetStringAsync("/Suppliers/Create"), "name=\"(Identifier\\[[^\"]+\\])\"").Groups[1] is { Success: true } idField) supplierForm[idField.Value] = "51824753556";
        var supplier = await DemoApp.PostFormAsync(admin, "/Suppliers/Create", supplierForm);
        Assert.True(supplier.StatusCode == HttpStatusCode.Redirect, "supplier not added: " + Snip(await supplier.Content.ReadAsStringAsync()));
        var supplierId = Regex.Match(supplier.Headers.Location!.ToString(), "[0-9a-f]{8}-[0-9a-f-]{27}").Value;
        var adminEnergy = await DemoApp.PostFormAsync(admin, "/Contracts/Create", new Dictionary<string, string>
            { ["OrganisationId"] = org, ["Title"] = "Energy supply (acceptance)", ["SupplierId"] = supplierId, ["CategoryId"] = energyId, ["Classification"] = "Internal" });
        Assert.True(adminEnergy.StatusCode == HttpStatusCode.Redirect, "the administrator could not create in a category created after set-up: " + Snip(await adminEnergy.Content.ReadAsStringAsync()));
        var energyContractUrl = adminEnergy.Headers.Location!.ToString();

        // Add Jeffrey through People and Access: Contract Manager, selected-category ownership (Packaging only), Internal ceiling, demonstration sign-in. Review, then confirm.
        var jeffreyEmail = "jeffrey@acceptance.example.invalid";
        var jeffrey = await WizardAsync(admin, org, "Jeffrey", jeffreyEmail, allowDemo: true, ["contract-manager"], "Categories", [packagingId], "Selected", [packagingId], "Internal", approverGroup: false);
        var jeffreyReview = Text(jeffrey.Review);
        Assert.Contains("Jeffrey", jeffreyReview);
        Assert.Contains("This person will be able to", jeffreyReview);
        Assert.Contains("This person will not be able to", jeffreyReview);
        Assert.Contains("Packaging", jeffreyReview);
        Assert.DoesNotContain("CategoryOwner", jeffrey.Review);                                    // codes only under Advanced details, which has to be asked for
        var unticked = await DemoApp.PostFormAsync(admin, "/Admin/People/Access", new Dictionary<string, string>
            { ["Input.DisplayName"] = "Nobody Confirmed", ["Input.Email"] = "nobody@acceptance.example.invalid", ["Input.OrganisationId"] = org, ["Input.Profiles"] = "read-only", ["Input.Scope"] = "WholeOrganisation", ["Step"] = "7" }, "/Admin/People/Access?handler=Save");
        Assert.Equal(HttpStatusCode.OK, unticked.StatusCode);                                      // refused: the box was not ticked
        Assert.Null(await app.WithTenantAsync(tenantId, async sp => await sp.GetRequiredService<IUserService>().FindByEmailAsync("nobody@acceptance.example.invalid")));

        // Jeffrey is on the Demonstration sign-in page.
        using var anonymous = app.NewClient();
        Assert.Contains("Jeffrey", await anonymous.GetStringAsync("/SignIn"));

        // Preview as Jeffrey: his navigation, his actions, the banner, writes refused.
        var personUrl = jeffrey.PersonUrl!;
        var preview = await DemoApp.PostFormAsync(admin, personUrl, new Dictionary<string, string> { ["reason"] = "Check what Jeffrey sees" }, personUrl + "?handler=Preview");
        Assert.Equal(HttpStatusCode.Redirect, preview.StatusCode);
        var previewHome = await PageAsync(admin, "/", pages);
        Assert.Contains("TEST MODE", previewHome); Assert.Contains("Previewing as", previewHome); Assert.Contains("Exit user preview", previewHome);
        Assert.Contains("href=\"/Contracts/Create\"", previewHome);
        Assert.DoesNotContain("admin-menu", previewHome);
        Assert.DoesNotContain("Open Administration", previewHome);
        var register = await PageAsync(admin, "/Contracts", pages);
        Assert.Contains("Energy supply (acceptance)", register);                                  // a Category Owner reads every contract in the organisation within the ceiling (confirmed decision)
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/Admin/People")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/Admin/ApprovalAuthorities")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await admin.GetAsync(energyContractUrl.Replace("/Details/", "/Edit/"))).StatusCode);
        var write = await DemoApp.PostFormAsync(admin, "/Suppliers/Create", new Dictionary<string, string> { ["OrganisationId"] = org, ["Name"] = "Written in preview" });
        Assert.True(write.StatusCode != HttpStatusCode.Redirect, "a write in preview was accepted");

        // Exit preview with the banner's own button.
        var bannerForm = Regex.Match(await PageAsync(admin, "/", pages), "<form[^>]*action=\"/Account/ExitPreview\"[^>]*>.*?</form>", RegexOptions.Singleline).Value;
        var exit = await admin.PostAsync("/Account/ExitPreview", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = DemoApp.ExtractToken(bannerForm)! }));
        Assert.Equal(HttpStatusCode.Redirect, exit.StatusCode);
        Assert.DoesNotContain("Previewing as", await PageAsync(admin, "/", pages));

        // Sign out; sign in normally as Jeffrey through demonstration sign-in.
        var signOut = await DemoApp.PostFormAsync(admin, "/", new Dictionary<string, string>(), "/SignOut");
        Assert.True(signOut.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, "sign-out " + (int)signOut.StatusCode);
        using var jeffreyClient = await app.SignInAsync(jeffreyEmail);
        var okHome = await PageAsync(jeffreyClient, "/", pages);
        Assert.Contains("href=\"/Contracts/Create\"", okHome);
        Assert.DoesNotContain("Open Administration", okHome);

        // Jeffrey creates a contract in an authorised category, and is refused outside it.
        var created = await DemoApp.PostFormAsync(jeffreyClient, "/Contracts/Create", new Dictionary<string, string>
            { ["OrganisationId"] = org, ["Title"] = "Packaging film (Jeffrey)", ["SupplierId"] = supplierId, ["CategoryId"] = packagingId, ["Classification"] = "Internal" });
        Assert.True(created.StatusCode == HttpStatusCode.Redirect, "Jeffrey could not create a Packaging contract: " + Snip(await created.Content.ReadAsStringAsync()));
        var refusedCreate = await DemoApp.PostFormAsync(jeffreyClient, "/Contracts/Create", new Dictionary<string, string>
            { ["OrganisationId"] = org, ["Title"] = "Energy (Jeffrey, should be refused)", ["SupplierId"] = supplierId, ["CategoryId"] = energyId, ["Classification"] = "Internal" });
        Assert.NotEqual(HttpStatusCode.Redirect, refusedCreate.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await jeffreyClient.GetAsync(energyContractUrl.Replace("/Details/", "/Edit/"))).StatusCode);

        // Approval authority by names only: the authority the starting configuration names is already listed; the administrator picks people.
        using var admin2 = await app.SignInAsync(AdminEmail);
        var patEmail = "pat.approver@acceptance.example.invalid";
        await WizardAsync(admin2, org, "Pat Approver", patEmail, allowDemo: true, ["read-only"], "WholeOrganisation", [], "None", [], "Internal", approverGroup: false);
        var authorities = await PageAsync(admin2, $"/Admin/ApprovalAuthorities?organisationId={org}", pages);
        var authoritiesText = Text(authorities);
        Assert.Contains("Group Head of Procurement", authoritiesText);                              // the pack's own name for it
        Assert.Contains("you only choose the people", authoritiesText);
        var key = Uri.UnescapeDataString(Regex.Match(authorities, "edit=([^\"&]+)").Groups[1].Value);
        Assert.True(key.Length > 0, "no authority to choose people for: " + Snip(authorities));
        var (_, patId) = await app.FindUserAsync(patEmail);
        var (_, jeffreyId) = await app.FindUserAsync(jeffreyEmail);
        var editPage = $"/Admin/ApprovalAuthorities?organisationId={org}&edit={Uri.EscapeDataString(key)}";
        List<KeyValuePair<string, string>> Choice(bool confirm) => [new("OrganisationId", org), new("Key", key), new("MemberIds", patId.ToString()), new("BackupIds", jeffreyId.ToString()), .. confirm ? [new KeyValuePair<string, string>("Confirm", "true")] : Array.Empty<KeyValuePair<string, string>>()];
        var reviewed = Text(await (await DemoApp.PostFormAsync(admin2, editPage, Choice(false), "/Admin/ApprovalAuthorities?handler=Review")).Content.ReadAsStringAsync());
        Assert.Contains("Pat Approver", reviewed); Assert.Contains("backup", reviewed);
        var notTicked = await DemoApp.PostFormAsync(admin2, editPage, Choice(false), "/Admin/ApprovalAuthorities?handler=Save");
        Assert.Equal(HttpStatusCode.OK, notTicked.StatusCode);                                      // refused until confirmed
        var savedAuthority = await DemoApp.PostFormAsync(admin2, editPage, Choice(true), "/Admin/ApprovalAuthorities?handler=Save");
        Assert.True(savedAuthority.StatusCode == HttpStatusCode.Redirect, "approval authority not saved: " + Snip(await savedAuthority.Content.ReadAsStringAsync()));
        var after = Text(await PageAsync(admin2, $"/Admin/ApprovalAuthorities?organisationId={org}", pages));
        Assert.Contains("Pat Approver", after); Assert.Contains("Jeffrey", after);
        var patCanApprove = await app.WithTenantAsync(tenantId, async sp =>
        {
            var subject = await sp.GetRequiredService<IAccessDirectory>().LoadSubjectAsync(patId);
            var scale = await sp.GetRequiredService<IClassificationScaleProvider>().GetAsync();
            return AccessEvaluator.Default.GrantsFor(subject!, scale, DateTime.UtcNow, "approvals.action").Count > 0;
        });
        Assert.True(patCanApprove, "Pat is in the authority but cannot approve");

        // Onboarding completion: validate and publish; the organisation says it is ready, with the four next steps.
        var validate = await DemoApp.PostFormAsync(admin2, $"/Onboarding/Review?org={org}", new Dictionary<string, string> { ["Org"] = org }, $"/Onboarding/Review?handler=Validate&org={org}");
        Assert.Equal(HttpStatusCode.Redirect, validate.StatusCode);
        var publish = await DemoApp.PostFormAsync(admin2, $"/Onboarding/Review?org={org}", new Dictionary<string, string> { ["Org"] = org, ["Note"] = "First release" }, $"/Onboarding/Review?handler=Publish&org={org}");
        Assert.True(publish.StatusCode == HttpStatusCode.Redirect, "set-up could not be published: " + Snip(await publish.Content.ReadAsStringAsync()));
        var doneText = Text(await PageAsync(admin2, $"/Onboarding/Review?org={org}", pages));
        Assert.Contains("JBS TEST is ready to use", doneText);
        foreach (var action in new[] { "Create first contract", "Add another person", "Review organisation setup", "Go to Home" }) Assert.Contains(action, doneText);
    }
}
