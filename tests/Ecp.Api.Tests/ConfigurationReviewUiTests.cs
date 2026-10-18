using System.Net;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Identity.Domain;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// G-01 through the real pages: the form page of one person submits a change, a second person reads a plain-language summary in the review queue and publishes it, the author cannot, and nothing in the review pages is a
/// raw document. Production is simulated by requiring a second person, exactly as the host does in Production.
/// </summary>
[Collection("Api")]
[Trait("Category", "Governance")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class ConfigurationReviewUiTests
{
    private const string Author = "bizconfig@a.example.test";
    private const string Reviewer = "orgadmin@a.example.test";
    private const string Viewer = "viewer.all@a.example.test";

    private static async Task<Guid> OrgAsync(DemoApp app)
    {
        var (tenant, _) = await app.FindUserAsync(Reviewer);
        return await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single(o => o.Name == "Demo Procurement Group").Id);
    }

    private static Dictionary<string, string> Form(Guid org, string singular, string plural, string note = "")
    {
        var form = new Dictionary<string, string> { ["OrganisationId"] = org.ToString(), ["Locale"] = "en", ["Note"] = note };
        for (var i = 0; i < TermCatalogue.All.Count; i++)
        {
            var key = TermCatalogue.All[i].Key;
            form[$"Rows[{i}].Key"] = key;
            if (key == "supplier") { form[$"Rows[{i}].Override"] = "true"; form[$"Rows[{i}].Singular"] = singular; form[$"Rows[{i}].Plural"] = plural; }
        }
        return form;
    }

    private static string Edit(Guid org) => $"/Admin/Terminology/Edit?organisationId={org}&locale=en";
    private static string Handler(string name, Guid org) => $"/Admin/Terminology/Edit?handler={name}&organisationId={org}&locale=en";

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task A_change_is_submitted_from_the_form_reviewed_in_plain_words_and_published_by_a_second_person()
    {
        using var app = DemoApp.Start();
        app.Factory.Services.GetRequiredService<ConfigurationOptions>().RequireSecondPersonPublish = true;
        var org = await OrgAsync(app);
        using var author = await app.SignInAsync(Author);
        using var reviewer = await app.SignInAsync(Reviewer);
        using var viewer = await app.SignInAsync(Viewer, followRedirects: true);

        var form = Form(org, "Partner", "Partners", "Partners, not suppliers");
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(author, Edit(org), form, Handler("Validate", org))).StatusCode);
        // The author's button says what will really happen.
        var editPage = await BodyAsync(author, Edit(org));
        Assert.Contains("Submit for review", editPage);
        Assert.DoesNotContain(">Publish<", editPage);

        // Pressing it submits; it does not publish.
        var submit = await DemoApp.PostFormAsync(author, Edit(org), form, Handler("Publish", org));
        Assert.Equal(HttpStatusCode.Redirect, submit.StatusCode);
        var after = await BodyAsync(author, submit.Headers.Location!.OriginalString);
        Assert.Contains("Submitted for review", after);
        Assert.Contains("<h1>Suppliers</h1>", await BodyAsync(viewer, "/Suppliers"));        // nothing is in force yet
        Assert.Contains("Waiting for review", await BodyAsync(author, Edit(org)));

        // The author sees their submission, but not the button that would decide it.
        var authorQueue = await BodyAsync(author, "/Admin/Reviews?show=mine");
        Assert.Contains("Terminology", authorQueue);
        var id = Regex.Match(authorQueue, "/Admin/Reviews/Details\\?id=([0-9a-f-]{36})").Groups[1].Value;
        var authorView = await BodyAsync(author, $"/Admin/Reviews/Details?id={id}");
        Assert.DoesNotContain("Approve and publish", authorView);
        Assert.Contains("someone else", authorView);

        // The second person reads a summary, not a document.
        var queue = await BodyAsync(reviewer, "/Admin/Reviews");
        Assert.Contains("Terminology", queue);
        var detail = await BodyAsync(reviewer, $"/Admin/Reviews/Details?id={id}");
        Assert.Contains("What would change", detail);
        Assert.Contains("Partner", detail);
        Assert.Contains("Approve and publish", detail);
        Assert.DoesNotContain("\"schemaVersion\"", detail);
        Assert.DoesNotContain("<textarea id=\"Definition\"", detail);
        Accessibility.AssertBasics(queue, "/Admin/Reviews");
        Accessibility.AssertBasics(detail, "/Admin/Reviews/Details");

        // Even handed the author's own button, the author's post is refused.
        var refused = await DemoApp.PostFormAsync(author, $"/Admin/Reviews/Details?id={id}", new Dictionary<string, string> { ["Id"] = id, ["Note"] = "mine" }, $"/Admin/Reviews/Details?handler=Publish&id={id}");
        var refusedBody = await refused.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("second person", refusedBody);
        Assert.Contains("<h1>Suppliers</h1>", await BodyAsync(viewer, "/Suppliers"));

        var publish = await DemoApp.PostFormAsync(reviewer, $"/Admin/Reviews/Details?id={id}", new Dictionary<string, string> { ["Id"] = id, ["Note"] = "Agreed" }, $"/Admin/Reviews/Details?handler=Publish&id={id}");
        Assert.Equal(HttpStatusCode.Redirect, publish.StatusCode);
        Assert.Contains("<h1>Partners</h1>", await BodyAsync(viewer, "/Suppliers"));
        Assert.DoesNotContain("Details?id=" + id, await BodyAsync(reviewer, "/Admin/Reviews"));          // no longer waiting
    }

    [Fact]
    public async Task A_reviewer_can_send_a_change_back_or_reject_it_with_a_comment_that_the_author_then_reads()
    {
        using var app = DemoApp.Start();
        app.Factory.Services.GetRequiredService<ConfigurationOptions>().RequireSecondPersonPublish = true;
        var org = await OrgAsync(app);
        using var author = await app.SignInAsync(Author);
        using var reviewer = await app.SignInAsync(Reviewer);
        var form = Form(org, "Vendor", "Vendors");
        await DemoApp.PostFormAsync(author, Edit(org), form, Handler("Validate", org));
        await DemoApp.PostFormAsync(author, Edit(org), form, Handler("Publish", org));
        var id = Regex.Match(await BodyAsync(reviewer, "/Admin/Reviews"), "/Admin/Reviews/Details\\?id=([0-9a-f-]{36})").Groups[1].Value;

        // A comment is required.
        var none = await DemoApp.PostFormAsync(reviewer, $"/Admin/Reviews/Details?id={id}", new Dictionary<string, string> { ["Id"] = id, ["Comment"] = "" }, $"/Admin/Reviews/Details?handler=Return&id={id}");
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        var back = await DemoApp.PostFormAsync(reviewer, $"/Admin/Reviews/Details?id={id}", new Dictionary<string, string> { ["Id"] = id, ["Comment"] = "Please use Partner" }, $"/Admin/Reviews/Details?handler=Return&id={id}");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);

        var returned = await BodyAsync(author, "/Admin/Reviews?show=returned");
        Assert.Contains("Sent back for changes", returned);
        Assert.Contains("Please use Partner", returned);
        var edit = await BodyAsync(author, Edit(org));
        Assert.Contains("A reviewer sent this back", edit);
        Assert.Contains("Submit for review", edit);
    }

    [Fact]
    public async Task People_without_a_configuration_right_cannot_open_the_review_pages_and_the_pages_show_no_other_tenants_work()
    {
        using var app = DemoApp.Start();
        using var viewer = await app.SignInAsync(Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/Admin/Reviews/Details?id=" + Guid.NewGuid())).StatusCode);
        using var reviewer = await app.SignInAsync(Reviewer);
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.GetAsync("/Admin/Reviews/Details?id=" + Guid.NewGuid())).StatusCode);       // an unknown draft is a plain not-found
        Assert.Contains("Nothing here.", await BodyAsync(reviewer, "/Admin/Reviews"));
    }

    [Fact]
    public async Task Rolling_back_from_the_history_goes_through_a_second_person_and_needs_a_recent_sign_in()
    {
        var clock = new FakeClock(DateTime.UtcNow);
        using var app = DemoApp.Start(services: s => s.AddSingleton<IClock>(clock));
        var org = await OrgAsync(app);
        using var author = await app.SignInAsync(Author);
        // Two releases published directly (single-person policy is the Demo default), then the second-person rule is switched on as in Production.
        foreach (var (s, p) in new[] { ("Partner", "Partners"), ("Vendor", "Vendors") })
        {
            var form = Form(org, s, p);
            await DemoApp.PostFormAsync(author, Edit(org), form, Handler("Validate", org));
            Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(author, Edit(org), form, Handler("Publish", org))).StatusCode);
        }
        app.Factory.Services.GetRequiredService<ConfigurationOptions>().RequireSecondPersonPublish = true;
        var history = await BodyAsync(author, $"/Admin/Terminology/History?organisationId={org}");
        Assert.Contains("ToRelease", history);
        var rollback = await DemoApp.PostFormAsync(author, $"/Admin/Terminology/History?organisationId={org}", new Dictionary<string, string> { ["OrganisationId"] = org.ToString(), ["ToRelease"] = "1", ["Reason"] = "Back to Partner" },
            $"/Admin/Terminology/History?handler=Rollback&organisationId={org}");
        Assert.Equal(HttpStatusCode.Redirect, rollback.StatusCode);
        Assert.Contains("Submitted for review", await BodyAsync(author, rollback.Headers.Location!.OriginalString));
        using var reviewer = await app.SignInAsync(Reviewer);
        Assert.Contains("puts back release", await BodyAsync(reviewer, "/Admin/Reviews"));
        clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await DemoApp.PostFormAsync(author, $"/Admin/Terminology/History?organisationId={org}", new Dictionary<string, string> { ["OrganisationId"] = org.ToString(), ["ToRelease"] = "1", ["Reason"] = "again" },
            $"/Admin/Terminology/History?handler=Rollback&organisationId={org}");
        Assert.StartsWith("/Reauth", stale.Headers.Location!.OriginalString);
    }
}
