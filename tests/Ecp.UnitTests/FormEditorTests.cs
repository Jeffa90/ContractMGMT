using System.Text.Json;
using System.Text.Json.Nodes;
using Ecp.Approvals.Definitions;
using Ecp.Configuration.Forms;
using Ecp.Lifecycle.Definitions;
using Ecp.Notifications.Policy;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>
/// G-03: the form editors for the workflow, the approval matrix and the notification policy. The strongest claim is the round trip: every document the shipped packs carry, opened in its form and saved without a change,
/// means exactly what it meant before. The rest proves that what is typed can only ever become the shapes the schema lists, that numbers and dates are read strictly, and that rows move up and down.
/// </summary>
public sealed class FormEditorTests
{
    private static readonly IReadOnlyList<FormSchema> Schemas =
        new IFormSchemaSource[] { new WorkflowFormSchemas(), new MatrixFormSchemas(), new NotificationPolicyFormSchemas() }.SelectMany(s => s.Schemas()).ToList();

    private static FormSchema SchemaOf(string kind) => Schemas.Single(s => s.Kind == kind);

    private static IEnumerable<(string Pack, string Kind, string File, string Json)> ShippedDocuments()
    {
        var packs = Path.Combine(RepoRoot.Find(), "templates", "packs");
        foreach (var dir in Directory.EnumerateDirectories(packs).OrderBy(d => d, StringComparer.Ordinal))
        {
            var pack = Path.GetFileName(dir);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "workflows"), "*.json").DefaultIfEmpty()) if (f is not null)
                yield return (pack, Path.GetFileName(f) == "variation.json" ? WorkflowKinds.Variation : WorkflowKinds.ContractLifecycle, f, File.ReadAllText(f));
            if (Directory.Exists(Path.Combine(dir, "matrices")))
                foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "matrices"), "*.json")) yield return (pack, Ecp.Approvals.ApprovalsModule.MatrixKind, f, File.ReadAllText(f));
            if (Directory.Exists(Path.Combine(dir, "notifications")))
                foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "notifications"), "*.json")) yield return (pack, NotificationPolicyKinds.Policy, f, File.ReadAllText(f));
        }
    }

    private static string Meaning(string kind, string json)
    {
        if (kind is WorkflowKinds.ContractLifecycle or WorkflowKinds.Variation)
        {
            var (def, messages) = WorkflowDefinitionParser.Parse(json);
            Assert.Empty(messages);
            return JsonSerializer.Serialize(def);
        }
        if (kind == Ecp.Approvals.ApprovalsModule.MatrixKind)
        {
            var (set, messages) = MatrixParser.Parse(json);
            Assert.Empty(messages);
            return JsonSerializer.Serialize(set);
        }
        var (policy, pm) = NotificationPolicyParser.Parse(json);
        Assert.Empty(pm);
        return JsonSerializer.Serialize(policy);
    }

    [Fact]
    public void Every_schema_covers_every_kind_the_form_editors_serve()
    {
        Assert.Equal(["ApprovalMatrix", "NotificationPolicy", "WorkflowContractLifecycle", "WorkflowVariation"], Schemas.Select(s => s.Kind).OrderBy(k => k, StringComparer.Ordinal));
        foreach (var s in Schemas) { Assert.False(string.IsNullOrWhiteSpace(s.Title)); Assert.NotEmpty(s.Sections); }
    }

    [Fact]
    public void Every_shipped_document_means_the_same_after_a_trip_through_its_form()
    {
        var checkedDocuments = new List<string>();
        foreach (var (pack, kind, file, json) in ShippedDocuments())
        {
            var schema = SchemaOf(kind);
            var original = JsonNode.Parse(json)!.AsObject();
            var (rebuilt, problems) = FormBinder.Bind(schema, FormBinder.Flatten(schema, original));
            Assert.True(problems.Count == 0, $"{pack}/{Path.GetFileName(file)}: {string.Join("; ", problems.Select(p => p.Path + " " + p.Message))}");
            var again = rebuilt.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            Assert.Equal(Meaning(kind, json), Meaning(kind, again));
            // A second trip changes nothing at all: the form is a fixed point.
            var (second, _) = FormBinder.Bind(schema, FormBinder.Flatten(schema, rebuilt));
            Assert.Equal(again, second.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            checkedDocuments.Add($"{pack}:{kind}");
        }
        // The packs carry both workflows for each of three packs, one policy each, and the construction pack's matrices.
        Assert.True(checkedDocuments.Count >= 10, string.Join(", ", checkedDocuments));
        Assert.Contains(checkedDocuments, d => d.EndsWith(":ApprovalMatrix"));
    }

    [Fact]
    public void Typed_numbers_and_dates_are_read_strictly_and_reported_against_the_field()
    {
        var schema = SchemaOf(Ecp.Approvals.ApprovalsModule.MatrixKind);
        var original = JsonNode.Parse(ShippedDocuments().First(d => d.Kind == Ecp.Approvals.ApprovalsModule.MatrixKind).Json)!.AsObject();
        var posted = FormBinder.Flatten(schema, original);
        posted["f.matrices.0.effectiveFrom"] = ["31/01/2027"];
        posted["f.matrices.0.rows.0.priority"] = ["a lot"];
        posted["f.matrices.0.rows.0.when.valueMin"] = ["twelve"];
        var (document, problems) = FormBinder.Bind(schema, posted);
        Assert.Contains(problems, p => p.Path == "matrices.0.effectiveFrom" && p.Message.Contains("year-month-day"));
        Assert.Contains(problems, p => p.Path == "matrices.0.rows.0.priority" && p.Message.Contains("whole number"));
        Assert.Contains(problems, p => p.Path == "matrices.0.rows.0.when.valueMin" && p.Message.Contains("number"));
        // A value that cannot be read is left out, never guessed.
        Assert.Null(document["matrices"]![0]!["effectiveFrom"]);
    }

    [Fact]
    public void What_is_typed_can_only_become_the_shapes_the_schema_lists()
    {
        var schema = SchemaOf(NotificationPolicyKinds.Policy);
        var original = JsonNode.Parse(ShippedDocuments().First(d => d.Kind == NotificationPolicyKinds.Policy).Json)!.AsObject();
        var posted = FormBinder.Flatten(schema, original);
        posted["f.templates.0.body"] = ["{{#each x}} <script>alert(1)</script> {\"a\":1} $(rm -rf /) {% if y %}"];
        posted["f.injected"] = ["{\"evil\":true}"];
        posted["f.reminders.injected"] = ["1"];
        var (document, _) = FormBinder.Bind(schema, posted);
        Assert.Null(document["injected"]);
        Assert.Null(document["reminders"]!["injected"]);
        // The hostile text stays text inside the one field it was typed into; validation rejects it at publication.
        var body = document["templates"]!.AsObject().First().Value!["body"]!.GetValue<string>();
        Assert.Contains("<script>", body);
        var (policy, _) = NotificationPolicyParser.Parse(document.ToJsonString());
        Assert.Contains(NotificationPolicyParser.Validate(policy!, Ecp.SharedKernel.ClassificationScale.Default), m => m.Code is "template_syntax" or "placeholder_unknown");
    }

    [Fact]
    public void Rows_are_added_removed_and_moved_by_buttons_and_stay_in_the_order_shown()
    {
        var schema = SchemaOf(WorkflowKinds.ContractLifecycle);
        var doc = JsonNode.Parse(ShippedDocuments().First(d => d.Kind == WorkflowKinds.ContractLifecycle).Json)!.AsObject();
        var states = doc["states"]!.AsArray();
        var count = states.Count;
        var first = states[0]!["code"]!.GetValue<string>(); var second = states[1]!["code"]!.GetValue<string>();

        Assert.True(FormBinder.Apply(schema, doc, "down|states|0"));
        Assert.Equal([second, first], [states[0]!["code"]!.GetValue<string>(), states[1]!["code"]!.GetValue<string>()]);
        Assert.True(FormBinder.Apply(schema, doc, "up|states|1"));
        Assert.Equal(first, states[0]!["code"]!.GetValue<string>());
        Assert.False(FormBinder.Apply(schema, doc, "up|states|0"));                          // the first row cannot move up
        Assert.False(FormBinder.Apply(schema, doc, $"down|states|{count - 1}"));             // nor the last down
        Assert.True(FormBinder.Apply(schema, doc, "add|states"));
        Assert.Equal(count + 1, doc["states"]!.AsArray().Count);
        Assert.Equal("Open", doc["states"]![count]!["documentPolicy"] is null ? "Open" : doc["states"]![count]!["documentPolicy"]!.GetValue<string>());
        Assert.True(FormBinder.Apply(schema, doc, $"remove|states|{count}"));
        Assert.Equal(count, doc["states"]!.AsArray().Count);
        Assert.False(FormBinder.Apply(schema, doc, "add|settings"));                         // not a list
        Assert.False(FormBinder.Apply(schema, doc, "add|nonsense"));

        // Named rows (messages) move too, and adding one gives it a fresh name.
        var policySchema = SchemaOf(NotificationPolicyKinds.Policy);
        var policy = JsonNode.Parse(ShippedDocuments().First(d => d.Kind == NotificationPolicyKinds.Policy).Json)!.AsObject();
        var names = policy["templates"]!.AsObject().Select(kv => kv.Key).ToList();
        Assert.True(FormBinder.Apply(policySchema, policy, "down|templates|0"));
        Assert.Equal([names[1], names[0]], policy["templates"]!.AsObject().Take(2).Select(kv => kv.Key));
        Assert.True(FormBinder.Apply(policySchema, policy, "add|templates"));
        Assert.Equal(names.Count + 1, policy["templates"]!.AsObject().Count);
        Assert.True(FormBinder.Apply(policySchema, policy, $"remove|templates|{names.Count}"));
        Assert.Equal(names.Skip(2).Prepend(names[0]).Prepend(names[1]), policy["templates"]!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void Validation_paths_land_on_the_field_they_are_about_including_named_rows()
    {
        var schema = SchemaOf(NotificationPolicyKinds.Policy);
        var doc = JsonNode.Parse(ShippedDocuments().First(d => d.Kind == NotificationPolicyKinds.Policy).Json)!.AsObject();
        var name = doc["templates"]!.AsObject().ElementAt(2).Key;
        Assert.Equal("templates.2.subject", FormBinder.FormPathOf(schema, doc, $"$.templates.{name}.subject"));
        Assert.Equal("events.3.recipients", FormBinder.FormPathOf(schema, doc, "$.events[3].recipients"));
        Assert.Equal("reminders.criticalNotice.offsetsDays", FormBinder.FormPathOf(schema, doc, "$.reminders.criticalNotice.offsetsDays"));
        Assert.Equal("events", FormBinder.FormPathOf(schema, doc, "$.events"));
        Assert.Null(FormBinder.FormPathOf(schema, doc, "$"));
        var wf = SchemaOf(WorkflowKinds.ContractLifecycle);
        Assert.Equal("transitions.4.from", FormBinder.FormPathOf(wf, new JsonObject(), "$.transitions[4].from"));
        Assert.Equal("settings.approval.approverGroupKey", FormBinder.FormPathOf(wf, new JsonObject(), "$.settings.approval.approverGroupKey"));
    }
}
