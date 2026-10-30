using System.Net;
using System.Text.RegularExpressions;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>UX-9: approval authorities are chosen by name. The page is for people who manage people, shows names (a machine key only under Advanced), and asks for a confirmed review before it changes anything.</summary>
[Collection("Api")]
public sealed class ApprovalAuthoritiesUiTests
{
    private const string OrgAdmin = "orgadmin@a.example.test";

    [Fact]
    public async Task The_page_lists_authorities_by_name_and_shows_the_machine_key_only_when_asked()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(OrgAdmin);
        var page = await admin.GetStringAsync("/Admin/ApprovalAuthorities");
        var text = WebUtility.HtmlDecode(Regex.Replace(page, "<[^>]+>|\\s+", " "));
        Assert.Contains("Approval authorities", text);
        Assert.Contains("Group Head of Procurement", text);                  // the name people read
        Assert.DoesNotContain("GroupHeadProcurement", text);                  // never the key
        Assert.DoesNotContain("Machine key", text);
        var advanced = WebUtility.HtmlDecode(Regex.Replace(await admin.GetStringAsync("/Admin/ApprovalAuthorities?advanced=true"), "<[^>]+>|\\s+", " "));
        Assert.Contains("Machine key", advanced);
        Assert.Contains("GroupHeadProcurement", advanced);
    }

    [Theory]
    [InlineData("viewer.all@a.example.test")]
    [InlineData("catowner.packaging@a.example.test")]
    public async Task Only_a_person_who_manages_people_may_open_it(string email)
    {
        using var app = DemoApp.Start();
        using var client = await app.SignInAsync(email);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Admin/ApprovalAuthorities")).StatusCode);
        using var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync("/Admin/ApprovalAuthorities")).StatusCode);
    }

    [Fact]
    public async Task It_is_in_the_administration_menu_under_workflows_and_approvals_and_the_advanced_group_page_is_still_reachable()
    {
        using var app = DemoApp.Start();
        using var admin = await app.SignInAsync(OrgAdmin);
        var landing = await admin.GetStringAsync("/Admin");
        Assert.Contains("/Admin/ApprovalAuthorities", landing);
        Assert.Contains("Approver groups (advanced)", landing);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/Admin/ApproverGroups")).StatusCode);
    }
}
