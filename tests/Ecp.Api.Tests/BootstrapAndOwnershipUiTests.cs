using System.Net;
using System.Text.RegularExpressions;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// UX-4 and UX-6 over HTTP: an administrator who cannot yet create contracts can give themselves full Test-mode access (explained first, audited, removable), self-grant stays refused everywhere else, and category
/// owners are chosen in a form from everyone the administrator may manage, with the owner role given in the same save.
/// </summary>
[Collection("Api")]
public sealed class BootstrapAndOwnershipUiTests
{
    private const string OrgAdmin = "orgadmin@a.example.test";

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task An_administrator_with_no_operational_access_is_guided_to_full_test_mode_access_which_is_explained_audited_and_removable()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(OrgAdmin, followRedirects: false);
        var home = await BodyAsync(admin, "/");
        Assert.DoesNotContain("href=\"/Contracts/Create\"", home);
        Assert.Contains("Set up my access", home);                                              // the first thing the first administrator is pointed to

        var page = await BodyAsync(admin, "/Access/Setup");
        Assert.Contains("Give me full Test-mode access", page);
        Assert.Contains("TEST ONLY", page);
        Assert.Contains("This will give you", page);                                           // exactly what, before applying
        Assert.Contains("This will not give you", page);
        Assert.Contains("System Administrator", page);                                         // ... including what it never grants
        Assert.DoesNotContain("ProcurementAdministrator", page);                               // words, not role codes
        var org = Regex.Match(page, "name=\"OrganisationId\" value=\"([0-9a-f-]{36})\"").Groups[1].Value;
        Assert.NotEmpty(org);

        var give = await DemoApp.PostFormAsync(admin, "/Access/Setup", new Dictionary<string, string> { ["OrganisationId"] = org, ["Reason"] = "Trying the product" }, "/Access/Setup?handler=Give");
        Assert.Equal(HttpStatusCode.Redirect, give.StatusCode);

        var after = await BodyAsync(admin, "/");
        Assert.Contains("href=\"/Contracts/Create\"", after);                                  // + New contract now works
        var mine = await BodyAsync(admin, "/Access");
        Assert.Contains("Create contracts", mine);
        Assert.Contains("Test-mode access", mine);                                             // says where it came from

        var (tenant, adminId) = await app.FindUserAsync(OrgAdmin);
        var actions = await app.WithTenantAsync(tenant, async sp =>
            (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(sp.GetRequiredService<Ecp.Audit.IAuditEventSource>().Events.Where(e => e.Action == "access.test_bootstrap"))).Count);
        Assert.Equal(1, actions);
        var roles = await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IRoleAssignmentService>().ListForUserAsync(adminId)).Select(a => a.RoleCode).ToList());
        Assert.DoesNotContain(RoleCodes.SystemAdministrator, roles);
        Assert.DoesNotContain(RoleCodes.TenantAdministrator, roles);

        var again = await BodyAsync(admin, "/Access/Setup");
        Assert.Contains("Remove Test-mode access", again);
        var end = await DemoApp.PostFormAsync(admin, "/Access/Setup", new Dictionary<string, string> { ["OrganisationId"] = org, ["Reason"] = "Finished" }, "/Access/Setup?handler=End");
        Assert.Equal(HttpStatusCode.Redirect, end.StatusCode);
        Assert.DoesNotContain("href=\"/Contracts/Create\"", await BodyAsync(admin, "/"));
    }

    [Fact]
    public async Task Nobody_else_can_use_the_test_shortcut_and_self_grant_stays_refused_in_the_wizard()
    {
        using var app = DemoApp.Start();
        using var owner = await app.SignInAsync("catowner.packaging@a.example.test", followRedirects: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/Access/Setup")).StatusCode);

        using var admin = await app.SignInAsync(OrgAdmin, followRedirects: false);
        var (_, adminId) = await app.FindUserAsync(OrgAdmin);
        var org = Regex.Match(await BodyAsync(admin, "/Access/Setup"), "name=\"OrganisationId\" value=\"([0-9a-f-]{36})\"").Groups[1].Value;
        var review = await BodyAsync(admin, $"/Admin/People/Access?Input.UserId={adminId}&Input.OrganisationId={org}&Input.Profiles=contract-manager&Input.Ownership=All&Step=7");
        Assert.Contains("This cannot be saved yet", review);
        Assert.Contains("give yourself access", review);                                       // the same rule, in words
    }

    [Fact]
    public async Task Category_owners_are_chosen_in_a_form_from_everyone_and_the_owner_role_is_given_in_the_same_save()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(OrgAdmin, followRedirects: false);
        var (tenant, _) = await app.FindUserAsync(OrgAdmin);
        var email = $"owner.{Guid.NewGuid():N}"[..20] + "@a.example.test";
        var person = await app.WithTenantAsync(tenant, sp => sp.GetRequiredService<IUserService>().InviteAsync("Owen Owner", email));

        var page = await BodyAsync(admin, "/Admin/Categories");
        Assert.Contains("Choose owners", page);
        Assert.Contains("Owen Owner", page);                                                   // not only people who already hold the role
        Assert.Contains("will be given the owner role", page);
        Assert.DoesNotContain("viewerKind", page, StringComparison.OrdinalIgnoreCase);
        var org = Regex.Match(page, "name=\"OrganisationId\" value=\"([0-9a-f-]{36})\" form=\"bulk\"").Groups[1].Value;
        var categoryIds = Regex.Matches(page, "name=\"CategoryIds\" value=\"([0-9a-f-]{36})\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(org); Assert.NotEmpty(categoryIds);
        var chosen = categoryIds.Take(1).ToList();

        // Review first: what will happen, including that the person gains the owner role.
        var fields = new List<KeyValuePair<string, string>> { new("OrganisationId", org), new("UserId", person.ToString()), new("Role", "primary") };
        fields.AddRange(chosen.Select(c => new KeyValuePair<string, string>("CategoryIds", c)));
        var plan = await DemoApp.PostFormAsync(admin, "/Admin/Categories", fields, "/Admin/Categories?handler=Plan");
        var planHtml = await plan.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        Assert.Contains("Check before you save", planHtml);
        Assert.Contains("will also be given", planHtml);

        var save = await DemoApp.PostFormAsync(admin, "/Admin/Categories", fields, "/Admin/Categories?handler=SetOwner");
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        var after = await BodyAsync(admin, "/Admin/Categories");
        Assert.Contains("Owen Owner", Regex.Match(after, "<tbody>.*</tbody>", RegexOptions.Singleline).Value);
        var held = await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IRoleAssignmentService>().ListForUserAsync(person)).Select(a => a.RoleCode).ToList());
        Assert.Contains(RoleCodes.CategoryOwner, held);                                        // the role came with the ownership
        // A backup cannot be the primary: asking for it is refused in words.
        var backup = new List<KeyValuePair<string, string>> { new("OrganisationId", org), new("UserId", person.ToString()), new("Role", "backup"), new("CategoryIds", chosen[0]) };
        var refused = await DemoApp.PostFormAsync(admin, "/Admin/Categories", backup, "/Admin/Categories?handler=SetOwner");
        Assert.Contains("already the primary owner", await refused.Content.ReadAsStringAsync());
    }
}
