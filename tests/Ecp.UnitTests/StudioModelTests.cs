using Ecp.Metadata;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>The Studio edits the typed model and writes it back through one writer; the impact analysis shows what a draft would change. Both are pure.</summary>
[Trait("Category", "CustomFields")]
public sealed class StudioModelTests
{
    private const string Everything = """
        { "schemaVersion": 1,
          "sections": [ { "key": "commercial", "label": "Commercial", "order": 20 } ],
          "lookups": [ { "name": "regions", "label": "Regions", "members": [ { "code": "NSW", "label": "New South Wales" }, { "code": "QLD", "label": "Queensland", "active": false } ] } ],
          "fields": [
            { "key": "txt", "label": "Text", "type": "TEXT", "section": "commercial", "order": 10, "required": true, "default": "x", "validation": { "minLength": 1, "maxLength": 20, "pattern": "^[a-z]+$", "patternMessage": "letters" }, "searchable": true, "filterable": true, "sortable": true, "reportable": true, "classification": "Confidential", "visibleToRoles": ["Legal"], "editableByRoles": ["Legal"], "visibleInStates": ["Draft"], "editableInStates": ["Draft"], "contractTypes": ["Services"], "effectiveFrom": "2026-01-01", "effectiveTo": "2030-01-01", "usableInApprovals": true },
            { "key": "qty", "label": "Quantity", "type": "INTEGER", "validation": { "min": 0, "max": 99 } },
            { "key": "price", "label": "Price", "type": "CURRENCY", "validation": { "decimals": 2, "currency": "AUD" } },
            { "key": "when", "label": "When", "type": "DATE", "validation": { "minDate": "2020-01-01", "maxDate": "2040-12-31" } },
            { "key": "tier", "label": "Tier", "type": "SINGLE_CHOICE", "choices": [ { "code": "GOLD", "label": "Gold" }, { "code": "SILVER", "label": "Silver", "active": false } ], "default": "GOLD" },
            { "key": "tags", "label": "Tags", "type": "MULTI_CHOICE", "choices": [ { "code": "A", "label": "A" }, { "code": "B", "label": "B" } ], "default": ["A", "B"], "validation": { "minItems": 1, "maxItems": 2 } },
            { "key": "region", "label": "Region", "type": "LOOKUP", "lookup": "regions" },
            { "key": "shown", "label": "Shown", "type": "CALCULATED_DISPLAY", "calculated": { "kind": "Sum", "operands": ["qty", "qty"], "decimals": 1 } },
            { "key": "retired_one", "label": "Retired", "type": "TEXT", "status": "Retired" } ] }
        """;

    private static FieldValidationContext Ctx(CustomFieldSet? previous = null, FieldUsage? usage = null)
        => new(["Public", "Internal", "Confidential", "Restricted", "Legal Privileged"], Ecp.Identity.Domain.RoleCodes.All.ToHashSet(), new HashSet<string> { "Draft", "Effective" }, previous, usage);

    [Fact]
    public void Writing_a_set_and_reading_it_back_gives_the_same_set_and_it_still_validates()
    {
        var (set, parse) = FieldParser.Parse(Everything);
        Assert.Empty(parse);
        var written = FieldWriter.ToJson(set!);
        var (again, parse2) = FieldParser.Parse(written);
        Assert.Empty(parse2);
        Assert.Equal(set!.Fields.Count, again!.Fields.Count);
        for (var i = 0; i < set.Fields.Count; i++) Assert.Equal(System.Text.Json.JsonSerializer.Serialize(set.Fields[i]), System.Text.Json.JsonSerializer.Serialize(again.Fields[i]));
        Assert.Equal(set.Sections, again.Sections);
        Assert.Equal(set.Lookups.Select(l => (l.Name, l.Members.Count)), again.Lookups.Select(l => (l.Name, l.Members.Count)));
        Assert.Equal(written, FieldWriter.ToJson(again));                                          // stable: writing twice changes nothing
        Assert.DoesNotContain(FieldParser.Validate(again, Ctx()), m => m.Severity == Ecp.Configuration.ValidationSeverity.Error);
    }

    [Fact]
    public void Text_typed_into_a_form_is_only_ever_data_in_the_document()
    {
        var hostile = "\"; drop; <script>alert(1)</script> {\"key\": \"x\"}";
        var set = new CustomFieldSet
        {
            Fields = [new FieldDefinition("safe", hostile, hostile, hostile, hostile, FieldTypes.Text, "general", 10, false, [], FieldValidation.None, [], null, null, false, false, false, false, "Internal", [], [], [], [], [],
                FieldStatuses.Active, null, null)]
        };
        var (back, _) = FieldParser.Parse(FieldWriter.ToJson(set));
        Assert.Equal(hostile, back!.Fields[0].Label);
        Assert.Single(back.Fields);
        Assert.Equal("safe", back.Fields[0].Key);
    }

    private static CustomFieldSet Parse(string json) => FieldParser.Parse(json).Set!;

    [Fact]
    public void The_impact_of_a_draft_says_what_is_new_retired_blocked_and_held_by_data()
    {
        var published = Parse(Everything);
        var usage = new FieldUsage(10, new Dictionary<string, int> { ["txt"] = 8, ["tier"] = 5, ["qty"] = 3 }, new Dictionary<string, int> { [FieldUsage.MemberKey("tier", "SILVER")] = 2, [FieldUsage.MemberKey("tier", "GOLD")] = 3 });
        // Nothing changed: nothing to report.
        Assert.Empty(FieldImpact.Analyse(published, published, usage));
        // A new field is information; no published set means everything is new.
        Assert.All(FieldImpact.Analyse(null, published, FieldUsage.None), i => Assert.Equal(ImpactLevel.Info, i.Level));
        var draft = Parse(Everything.Replace("\"key\": \"qty\", \"label\": \"Quantity\", \"type\": \"INTEGER\"", "\"key\": \"qty\", \"label\": \"Quantity\", \"type\": \"TEXT\"")
            .Replace("\"key\": \"txt\", \"label\": \"Text\", \"type\": \"TEXT\"", "\"key\": \"txt\", \"label\": \"Text\", \"type\": \"TEXT\", \"status\": \"Retired\""));
        var impact = FieldImpact.Analyse(published, draft, usage);
        Assert.Contains(impact, i => i.Key == "qty" && i.Level == ImpactLevel.Blocking && i.AffectedValues == 3);           // a type change over data
        Assert.Contains(impact, i => i.Key == "txt" && i.Level == ImpactLevel.Warning && i.AffectedValues == 8);            // retiring keeps the values
        // Removing a published field is blocked; removing a choice that is held is blocked; making it inactive is a warning.
        var removed = new CustomFieldSet { Sections = published.Sections, Lookups = published.Lookups, Fields = published.Fields.Where(f => f.Key != "price").ToList() };
        Assert.Contains(FieldImpact.Analyse(published, removed, usage), i => i.Key == "price" && i.Level == ImpactLevel.Blocking);
        var noGold = Parse(Everything.Replace("{ \"code\": \"GOLD\", \"label\": \"Gold\" }, ", ""));
        Assert.Contains(FieldImpact.Analyse(published, noGold, usage), i => i.Key == "tier" && i.Level == ImpactLevel.Blocking && i.AffectedValues == 3);
        var silverOff = Parse(Everything.Replace("{ \"code\": \"GOLD\", \"label\": \"Gold\" }", "{ \"code\": \"GOLD\", \"label\": \"Gold\", \"active\": false }"));
        Assert.Contains(FieldImpact.Analyse(published, silverOff, usage), i => i.Key == "tier" && i.Level == ImpactLevel.Warning && i.Message.Contains("inactive"));
        // Visibility, classification and approval use are called out because they change who can read stored values.
        var wider = Parse(Everything.Replace("\"classification\": \"Confidential\"", "\"classification\": \"Public\"").Replace("\"usableInApprovals\": true", "\"usableInApprovals\": false"));
        var widerImpact = FieldImpact.Analyse(published, wider, usage);
        Assert.Contains(widerImpact, i => i.Key == "txt" && i.Message.Contains("classification"));
        Assert.Contains(widerImpact, i => i.Key == "txt" && i.Message.Contains("approval"));
    }

    [Fact]
    public void The_validator_refuses_what_the_impact_marks_as_blocking_so_a_page_cannot_publish_it()
    {
        var published = Parse(Everything);
        var usage = new FieldUsage(10, new Dictionary<string, int> { ["qty"] = 3 }, new Dictionary<string, int>());
        var typeChanged = Parse(Everything.Replace("\"key\": \"qty\", \"label\": \"Quantity\", \"type\": \"INTEGER\"", "\"key\": \"qty\", \"label\": \"Quantity\", \"type\": \"TEXT\""));
        Assert.Contains(FieldParser.Validate(typeChanged, Ctx(published, usage)), m => m.Severity == Ecp.Configuration.ValidationSeverity.Error && m.Path.Contains("qty") | m.Message.Contains("type"));
        var removed = new CustomFieldSet { Sections = published.Sections, Lookups = published.Lookups, Fields = published.Fields.Where(f => f.Key != "qty").ToList() };
        Assert.Contains(FieldParser.Validate(removed, Ctx(published, usage)), m => m.Severity == Ecp.Configuration.ValidationSeverity.Error);
    }

    [Fact]
    public void A_state_the_workflow_does_not_have_is_a_warning_when_the_workflow_states_are_known_and_nothing_when_they_are_not()
    {
        var doc = """{ "schemaVersion": 1, "fields": [ { "key": "note", "label": "Note", "type": "TEXT", "visibleInStates": ["NoSuchState"] } ] }""";
        var set = Parse(doc);
        Assert.Contains(FieldParser.Validate(set, Ctx()), m => m.Code == "unknown_state" && m.Severity == Ecp.Configuration.ValidationSeverity.Warning);      // states can change after a field is defined, so it is a warning
        // Without a workflow module to ask, the state name cannot be checked at all.
        var unknown = new FieldValidationContext(["Public", "Internal", "Confidential", "Restricted", "Legal Privileged"], Ecp.Identity.Domain.RoleCodes.All.ToHashSet(), null);
        Assert.DoesNotContain(FieldParser.Validate(set, unknown), m => m.Code == "unknown_state");
    }
}
