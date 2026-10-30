using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// UX-3a (docs/UX_ACCEPTANCE_CRITERIA.md, People & Access): an administrator adds a person, chooses what they will do in plain words, reviews the result in plain English and saves, with no
/// role code, viewer kind, group claim key or CSV; a synthetic test person can then sign in through demonstration sign-in.
/// </summary>
[Collection("Api")]
public sealed class PeopleAccessUiTests
{
    private const string OrgAdmin = "orgadmin@a.example.test";

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Posts one wizard step the way the browser does: the answers of the step plus everything carried forward.</summary>
    private static async Task<string> StepAsync(HttpClient client, string handler, Dictionary<string, string> fields, params (string Key, string Value)[] more)
    {
        var all = fields.ToList();
        all.AddRange(more.Select(m => new KeyValuePair<string, string>(m.Key, m.Value)));
        var response = await DemoApp.PostFormAsync(client, "/Admin/People/Access", all, $"/Admin/People/Access?handler={handler}");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.Found, $"{handler} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task An_administrator_adds_a_test_person_with_the_manager_profile_and_the_person_can_sign_in_with_the_right_navigation()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(OrgAdmin, followRedirects: false);

        var list = await BodyAsync(admin, "/Admin/People");
        Assert.Contains("Add a person", list);
        var first = await BodyAsync(admin, "/Admin/People/Access");
        Assert.Contains("Allow demonstration sign-in", first);                                   // Demo/Test only
        var org = Regex.Match(first, "name=\"Input.OrganisationId\"[^>]*value=\"([0-9a-f-]{36})\"").Groups[1].Value;
        if (org.Length == 0) org = Regex.Match(first, "<option value=\"([0-9a-f-]{36})\"").Groups[1].Value;
        Assert.NotEmpty(org);

        var email = $"wizard.{Guid.NewGuid():N}"[..20] + "@a.example.test";
        var carried = new Dictionary<string, string> { ["Input.DisplayName"] = "Wizard Wanda", ["Input.Email"] = email, ["Input.AllowDemo"] = "true", ["Input.OrganisationId"] = org };

        var s2 = await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "1" });
        Assert.Contains("What will this person do?", s2);
        Assert.Contains(" Manager", s2);                                                         // the profile, in words
        Assert.DoesNotContain("CategoryOwner", s2);
        Assert.DoesNotContain("viewerKind", s2, StringComparison.OrdinalIgnoreCase);

        var s3 = await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "2" }, ("Input.Profiles", "contract-manager"));
        Assert.Contains("Where can they work?", s3);
        var s4 = await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "3" }, ("Input.Profiles", "contract-manager"), ("Input.Scope", "WholeOrganisation"));
        Assert.Contains("own", s4);
        var s5 = await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "4" }, ("Input.Profiles", "contract-manager"), ("Input.Scope", "WholeOrganisation"), ("Input.Ownership", "All"));
        Assert.Contains("How sensitive may the information be?", s5);
        var review = await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "5" }, ("Input.Profiles", "contract-manager"), ("Input.Scope", "WholeOrganisation"), ("Input.Ownership", "All"), ("Input.Ceiling", "Internal"));
        Assert.Contains("Check before you save", review);
        Assert.Contains("Wizard Wanda", review);
        Assert.Contains("will be able to", review);
        Assert.Contains("Save access", review);
        Assert.DoesNotContain("This cannot be saved yet", review);
        // The review says what the person will and will not be able to do, in words, with the ownership as a standing choice; no role code unless Advanced details is asked for.
        Assert.Contains("This person will be able to", review);
        Assert.Contains("This person will not be able to", review);
        Assert.Contains("Approve or sign off their own submissions", review);
        Assert.Contains("Every current and future", review);
        Assert.DoesNotContain("CategoryOwner", review);
        var advanced = await StepAsync(admin, "Next", new Dictionary<string, string>(carried) { ["Step"] = "7" }, ("Input.Profiles", "contract-manager"), ("Input.Scope", "WholeOrganisation"), ("Input.Ownership", "All"), ("Input.Ceiling", "Internal"), ("Input.Advanced", "true"));
        Assert.Contains("Advanced details", advanced);
        Assert.Contains("CategoryOwner", advanced);

        // Nothing exists until Save.
        var (tenant, _) = await app.FindUserAsync(OrgAdmin);
        Assert.False(await app.WithTenantAsync(tenant, async sp => await sp.GetRequiredService<Ecp.Identity.IUserService>().FindByEmailAsync(email) is not null));

        // The review has to be confirmed: without the tick nothing is created.
        var unconfirmed = await DemoApp.PostFormAsync(admin, "/Admin/People/Access", new Dictionary<string, string>(carried)
        {
            ["Step"] = "7", ["Input.Profiles"] = "contract-manager", ["Input.Scope"] = "WholeOrganisation", ["Input.Ownership"] = "All", ["Input.Ceiling"] = "Internal"
        }, "/Admin/People/Access?handler=Save");
        Assert.Equal(HttpStatusCode.OK, unconfirmed.StatusCode);
        Assert.Contains("tick the box to confirm", await unconfirmed.Content.ReadAsStringAsync());
        Assert.False(await app.WithTenantAsync(tenant, async sp => await sp.GetRequiredService<Ecp.Identity.IUserService>().FindByEmailAsync(email) is not null));

        var save = await DemoApp.PostFormAsync(admin, "/Admin/People/Access", new Dictionary<string, string>(carried)
        {
            ["Step"] = "7", ["Input.Profiles"] = "contract-manager", ["Input.Scope"] = "WholeOrganisation", ["Input.Ownership"] = "All", ["Input.Ceiling"] = "Internal", ["Input.Reason"] = "Joins the team", ["Input.Confirm"] = "true"
        }, "/Admin/People/Access?handler=Save");
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        var person = await BodyAsync(admin, save.Headers.Location!.ToString());
        Assert.Contains("Wizard Wanda", person);
        Assert.Contains("Create", person);

        Assert.Contains("Wizard Wanda", await BodyAsync(admin, "/Admin/People"));
        Assert.Contains("Test person", await BodyAsync(admin, "/Admin/People"));
        Assert.Contains("Wizard Wanda", await app.NewClient().GetStringAsync("/SignIn"));   // listed on the demonstration sign-in page

        using var wanda = await app.SignInAsync(email, followRedirects: true);
        var home = await BodyAsync(wanda, "/");
        Assert.Contains("href=\"/Contracts/Create\"", home);                                    // owns every category: can start a contract
        Assert.DoesNotContain("admin-menu", home);                                               // a manager sees no Administration menu
        var mine = await BodyAsync(wanda, "/Access");
        Assert.Contains("Create contracts", mine);
        Assert.Equal(HttpStatusCode.Forbidden, (await wanda.GetAsync("/Admin/People")).StatusCode);
    }

    [Fact]
    public async Task People_and_Access_is_for_people_managers_only_and_a_refused_grant_is_explained_in_words()
    {
        using var app = DemoApp.Start();
        using var owner = await app.SignInAsync("catowner.packaging@a.example.test", followRedirects: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Admin/People")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Admin/People/Access")).StatusCode);
        using var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/People")).StatusCode);
    }

    [Fact]
    public async Task Preview_as_a_user_shows_that_persons_application_read_only_with_a_permanent_banner_and_an_audited_exit()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(OrgAdmin, followRedirects: false);
        var (tenant, viewerId) = await app.FindUserAsync("viewer.category@a.example.test");

        var person = await BodyAsync(admin, $"/Admin/People/Person/{viewerId}");
        Assert.Contains("Preview as this user", person);
        Assert.Contains("Test only", person);
        var start = await DemoApp.PostFormAsync(admin, $"/Admin/People/Person/{viewerId}", new Dictionary<string, string> { ["reason"] = "Checking the category viewer" }, $"/Admin/People/Person/{viewerId}?handler=Preview");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);

        // The application is now the viewer's: their navigation, their records, none of the administrator's authority.
        var home = await BodyAsync(admin, "/");
        Assert.Contains("TEST MODE", home);
        Assert.Contains("Previewing as", home);
        Assert.Contains("Exit user preview", home);
        Assert.DoesNotContain("admin-menu", home);                                               // the viewer has no Administration menu
        Assert.DoesNotContain("href=\"/Contracts/Create\"", home);
        var register = await BodyAsync(admin, "/Contracts");
        Assert.Contains("Synthetic Packaging Supply Agreement", register);                       // a category viewer sees the packaging category ...
        Assert.DoesNotContain("Synthetic Energy Agreement", register);                           // ... and nothing outside it
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/Admin/People")).StatusCode);   // the administrator's own authority is not inherited
        Assert.Contains("Previewing as", await BodyAsync(admin, "/Access"));                      // the banner is on every page
        Assert.Contains("Create contracts", await BodyAsync(admin, "/Access"));                   // listed as something this person cannot do

        // Read-only: a write is refused with a plain page, and nothing changed.
        var refused = await DemoApp.PostFormAsync(admin, "/Access", new Dictionary<string, string> { ["x"] = "y" }, "/Contracts/Create");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Preview is read-only", await refused.Content.ReadAsStringAsync());

        // Exit returns the administrator to themselves.
        var exit = await DemoApp.PostFormAsync(admin, "/Access", new Dictionary<string, string>(), "/Account/ExitPreview");
        Assert.Equal(HttpStatusCode.Redirect, exit.StatusCode);
        var after = await BodyAsync(admin, "/");
        Assert.DoesNotContain("Previewing as", after);
        Assert.Contains("admin-menu", after);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin/People")).StatusCode);

        // Recorded: started, refused write, ended, with the previewer as the actor.
        var actions = await app.WithTenantAsync(tenant, async sp =>
        {
            var events = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(sp.GetRequiredService<Ecp.Audit.IAuditEventSource>().Events.Where(e => e.Action.StartsWith("preview.")));
            return events.Select(e => (e.Action, e.ActorType)).ToList();
        });
        Assert.Contains(("preview.started", "User"), actions);                                  // started by the administrator as themselves
        Assert.Contains(("preview.write_refused", "Preview"), actions);
        Assert.Contains(("preview.ended", "Preview"), actions);
    }

    [Fact]
    public async Task A_person_who_cannot_manage_people_cannot_start_a_preview_and_a_forged_preview_claim_alone_changes_nothing()
    {
        using var app = DemoApp.Start();
        using var owner = await app.SignInAsync("catowner.packaging@a.example.test", followRedirects: false);
        var (_, adminId) = await app.FindUserAsync(OrgAdmin);
        var token = await DemoApp.TokenAsync(owner, "/Access");
        var response = await owner.PostAsync($"/Admin/People/Person/{adminId}?handler=Preview", new FormUrlEncodedContent(new Dictionary<string, string> { ["reason"] = "x", ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("Previewing as", await BodyAsync(owner, "/"));
    }
}
