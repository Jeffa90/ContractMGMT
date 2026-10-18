using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.Configuration.Forms;
using Ecp.Identity;
using Ecp.Lifecycle.Definitions;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// G-03 through the real pages: a business administrator changes the workflow, the approval matrices and the notification policy by filling in fields and pressing buttons, reads a preview and an impact analysis, and
/// submits; a second person reviews and publishes it. No page shows or accepts a document, and every page passes the structural accessibility checks.
/// </summary>
[Collection("Api")]
[Trait("Category", "Governance")]
[Trait("Provider", "SqliteNotSqlServerProof")]
public sealed class FormEditorUiTests
{
    private const string Author = "bizconfig@a.example.test";
    private const string Reviewer = "orgadmin@a.example.test";
    private const string Viewer = "viewer.all@a.example.test";

    private static async Task<Guid> OrgAsync(DemoApp app)
    {
        var (tenant, _) = await app.FindUserAsync(Reviewer);
        return await app.WithTenantAsync(tenant, async sp => (await sp.GetRequiredService<IOrganisationStructureService>().ListOrganisationsAsync()).Single(o => o.Name == "Demo Procurement Group").Id);
    }

    private static async Task<string> BodyAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    private static string Page(string kind, Guid org) => $"/Admin/Forms/Edit?kind={kind}&organisationId={org}";
    private static string Handler(string name, string kind, Guid org) => $"/Admin/Forms/Edit?handler={name}&kind={kind}&organisationId={org}";

    /// <summary>What the browser would post for the document that is in force, with the identifiers every form carries.</summary>
    private static async Task<(List<KeyValuePair<string, string>> Fields, JsonObject Document, FormSchema Schema)> FormOfAsync(DemoApp app, string kind, Guid org)
    {
        var (tenant, _) = await app.FindUserAsync(Reviewer);
        var (json, schema) = await app.WithTenantAsync(tenant, async sp =>
        {
            var editor = sp.GetRequiredService<IConfigurationFormEditor>();
            var current = await sp.GetRequiredService<IConfigurationReleases>().LatestAsync(kind, org);
            return (current?.ContentJson, editor.SchemaOf(kind));
        });
        Assert.NotNull(json);
        var document = JsonNode.Parse(json!)!.AsObject();
        var fields = FormBinder.Flatten(schema, document).SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string>(kv.Key, v))).ToList();
        fields.Add(new("Kind", kind)); fields.Add(new("OrganisationId", org.ToString()));
        return (fields, document, schema);
    }

    private static List<KeyValuePair<string, string>> With(List<KeyValuePair<string, string>> fields, string key, params string[] values)
    {
        var copy = fields.Where(f => f.Key != key).ToList();
        copy.AddRange(values.Select(v => new KeyValuePair<string, string>(key, v)));
        return copy;
    }

    [Fact]
    public async Task A_workflow_is_changed_with_fields_and_buttons_checked_previewed_submitted_and_published_by_a_second_person()
    {
        using var app = DemoApp.Start();
        app.Factory.Services.GetRequiredService<ConfigurationOptions>().RequireSecondPersonPublish = true;
        var org = await OrgAsync(app);
        var kind = WorkflowKinds.ContractLifecycle;
        using var author = await app.SignInAsync(Author);
        using var reviewer = await app.SignInAsync(Reviewer);

        // The page shows what is in force as fields; it has no document and no way to type one.
        var inForce = await BodyAsync(author, Page(kind, org));
        Accessibility.AssertBasics(inForce, "/Admin/Forms/Edit (in force)");
        Assert.DoesNotContain("<textarea id=\"Definition\"", inForce);
        Assert.DoesNotContain("\"transitions\"", inForce);
        Assert.Contains("Start a draft", inForce);
        Assert.Contains("disabled", inForce);                         // nothing can be changed until a draft is started

        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(author, Page(kind, org), [new("Kind", kind), new("OrganisationId", org.ToString())], Handler("Start", kind, org))).StatusCode);
        var editable = await BodyAsync(author, Page(kind, org));
        Accessibility.AssertBasics(editable, "/Admin/Forms/Edit (draft)");
        Assert.Contains("Move up", editable);
        Assert.Contains("Add stage", editable);
        Assert.Contains("Preview and impact", editable);
        Assert.Contains("What the workflow does", editable);

        // Change the label of the first move, then check the draft: valid, and the preview shows the new wording.
        var (fields, document, _) = await FormOfAsync(app, kind, org);
        var original = document["transitions"]![0]!["display"]!.GetValue<string>();
        var changed = With(fields, "f.transitions.0.display", "Send onwards for review");
        var validated = await DemoApp.PostFormAsync(author, Page(kind, org), changed, Handler("Validate", kind, org));
        Assert.True(validated.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK);
        Assert.Contains("Send onwards for review", await BodyAsync(author, Page(kind, org)));

        // A wrong value is reported beside its field, and an unreadable number is never guessed.
        var broken = With(With(changed, "f.transitions.0.to", ""), "f.settings.unlock.maxMinutes", "soon");
        var refused = await DemoApp.PostFormAsync(author, Page(kind, org), broken, Handler("Validate", kind, org));
        var refusedBody = await refused.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Fix these before you submit", refusedBody);
        Assert.Contains("href=\"#f-settings-unlock-maxMinutes\"", refusedBody);
        Assert.Contains("Enter a whole number", refusedBody);
        Accessibility.AssertBasics(refusedBody, "/Admin/Forms/Edit (problems)");

        // Rows move with buttons and the page redisplays them in the new order without saving.
        var moved = await DemoApp.PostFormAsync(author, Page(kind, org), With(changed, "Op", "down|states|0"), Handler("Row", kind, org));
        var movedBody = await moved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Matches("Stage 2: " + Regex.Escape(document["states"]![0]!["display"]!.GetValue<string>()), movedBody);

        // Submitting does not publish; the author cannot publish their own change; a second person does.
        var submit = await DemoApp.PostFormAsync(author, Page(kind, org), With(changed, "Note", "Clearer button wording"), Handler("Publish", kind, org));
        Assert.Equal(HttpStatusCode.Redirect, submit.StatusCode);
        var queue = await BodyAsync(reviewer, "/Admin/Reviews");
        var id = Regex.Match(queue, "/Admin/Reviews/Details\\?id=([0-9a-f-]{36})").Groups[1].Value;
        Assert.NotEmpty(id);
        var detail = await BodyAsync(reviewer, $"/Admin/Reviews/Details?id={id}");
        Assert.Contains("Contract workflow", detail);
        Assert.DoesNotContain("\"schemaVersion\"", detail);
        Assert.Equal(HttpStatusCode.Redirect, (await DemoApp.PostFormAsync(reviewer, $"/Admin/Reviews/Details?id={id}", new Dictionary<string, string> { ["Id"] = id, ["Note"] = "Agreed" }, $"/Admin/Reviews/Details?handler=Publish&id={id}")).StatusCode);

        // What is now in force has the new wording and nothing else changed.
        var (_, after, _) = await FormOfAsync(app, kind, org);
        Assert.Equal("Send onwards for review", after["transitions"]![0]!["display"]!.GetValue<string>());
        Assert.NotEqual(original, after["transitions"]![0]!["display"]!.GetValue<string>());
        Assert.Equal(document["states"]!.AsArray().Count, after["states"]!.AsArray().Count);
        Assert.Equal(document["transitions"]!.AsArray().Count, after["transitions"]!.AsArray().Count);
    }

    [Fact]
    public async Task The_approval_matrix_and_notification_policy_forms_open_for_every_kind_with_a_preview_and_stay_inside_the_organisation()
    {
        using var app = DemoApp.Start();
        var org = await OrgAsync(app);
        using var author = await app.SignInAsync(Author);
        foreach (var kind in new[] { WorkflowKinds.Variation, "ApprovalMatrix", "NotificationPolicy" })
        {
            var started = await DemoApp.PostFormAsync(author, Page(kind, org), [new("Kind", kind), new("OrganisationId", org.ToString())], Handler("Start", kind, org));
            Assert.True(started.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.OK, kind + " " + (int)started.StatusCode);
            var body = await BodyAsync(author, Page(kind, org));
            Accessibility.AssertBasics(body, "/Admin/Forms/Edit " + kind);
            Assert.Contains("Preview and impact", body);
            Assert.DoesNotContain("<textarea id=\"Definition\"", body);
            Assert.DoesNotContain("\"schemaVersion\"", body);
        }
        var policy = await BodyAsync(author, Page("NotificationPolicy", org));
        Assert.Contains("What would be sent", policy);
        Assert.Contains("REF-000123", policy);                       // the sample contract
        Assert.DoesNotContain("{{contract.reference}}", policy.Split("What would be sent")[1]);   // placeholders are filled in the preview

        // A person with no configuration right sees nothing; an unknown kind is not a page.
        using var viewer = await app.SignInAsync(Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Page("NotificationPolicy", org))).StatusCode);
        var unknown = await author.GetAsync(Page("Terminology", org));
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        Assert.Contains("no form editor", unknownBody);
        Assert.DoesNotContain("Start a draft", unknownBody);
    }

    [Fact]
    public async Task In_production_the_raw_document_is_read_only_and_the_form_is_the_way_to_change_a_draft()
    {
        using var app = DemoApp.Start();
        var org = await OrgAsync(app);
        app.Factory.Services.GetRequiredService<ConfigurationOptions>().AllowRawDocumentEditing = false;
        using var author = await app.SignInAsync(Author);
        var kind = WorkflowKinds.ContractLifecycle;
        await DemoApp.PostFormAsync(author, "/Admin/Configuration?kind=" + kind + "&organisationId=" + org, new Dictionary<string, string> { ["Kind"] = kind, ["OrganisationId"] = org.ToString() }, $"/Admin/Configuration?handler=Start&kind={kind}&organisationId={org}");
        var page = await BodyAsync(author, $"/Admin/Configuration?kind={kind}&organisationId={org}");
        Assert.Contains("read-only diagnostic", page);
        Assert.Contains("Open the form", page);
        Assert.DoesNotContain("name=\"Definition\"", page);
        var draftId = Regex.Match(page, "name=\"draftId\" value=\"([0-9a-f-]{36})\"").Groups[1].Value;
        var save = await DemoApp.PostFormAsync(author, $"/Admin/Configuration?kind={kind}&organisationId={org}", new Dictionary<string, string> { ["draftId"] = draftId, ["Kind"] = kind, ["OrganisationId"] = org.ToString(), ["Definition"] = "{}" }, $"/Admin/Configuration?handler=Save&kind={kind}&organisationId={org}");
        var text = await save.Content.ReadAsStringAsync();
        Assert.Contains("only through the form editor", text);
    }
}
