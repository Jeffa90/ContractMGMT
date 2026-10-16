using Ecp.Approvals.Definitions;
using Ecp.Configuration;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

internal static class MatrixJson
{
    public static string Step(string key = "approve", string group = "GroupA", string mode = "FirstResponse", string? backup = null, string extra = "")
        => $"{{ \"key\": \"{key}\", \"display\": \"{key}\", \"approverGroup\": \"{group}\", \"mode\": \"{mode}\"{(backup is null ? "" : $", \"backupGroup\": \"{backup}\"")}{extra} }}";

    public static string Row(string code, string when = "{}", string steps = "", int priority = 100, string kind = "Base", string? effect = null, string flow = "Sequential", string? window = null)
        => $"{{ \"code\": \"{code}\", \"kind\": \"{kind}\"{(effect is null ? "" : $", \"effect\": \"{effect}\"")}, \"priority\": {priority}, \"flow\": \"{flow}\"{(window is null ? "" : ", " + window)}, \"when\": {when}, \"steps\": [ {(steps == "" ? Step() : steps)} ] }}";

    public static string Matrix(string code, string rows, string currency = "AUD", string extra = "") => $"{{ \"code\": \"{code}\", \"name\": \"{code}\", \"currency\": \"{currency}\"{extra}, \"rows\": [ {rows} ] }}";

    public static string Doc(params string[] matrices) => $"{{ \"schemaVersion\": 1, \"matrices\": [ {string.Join(", ", matrices)} ] }}";

    public static string Single(params string[] rows) => Doc(Matrix("m", string.Join(", ", rows)));

    public static MatrixSet Parse(string json)
    {
        var (set, messages) = MatrixParser.Parse(json);
        Assert.NotNull(set);
        Assert.Empty(messages);
        return set!;
    }

    public static IReadOnlyList<ValidationMessage> Check(string json, MatrixReferenceData? reference = null) => MatrixParser.Validate(Parse(json), ClassificationScale.Default, reference);
    public static IEnumerable<string> Errors(string json, MatrixReferenceData? reference = null) => Check(json, reference).Where(m => m.Severity == ValidationSeverity.Error).Select(m => m.Code);
    public static IEnumerable<string> Warnings(string json, MatrixReferenceData? reference = null) => Check(json, reference).Where(m => m.Severity == ValidationSeverity.Warning).Select(m => m.Code);

    public static MatrixReferenceData Categories(params (string Code, string[] Path)[] c)
        => new(c.ToDictionary(x => x.Code, x => (IReadOnlyList<string>)x.Path, StringComparer.OrdinalIgnoreCase), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>());
}

[Trait("Category", "Unit")]
public sealed class ApprovalMatrixDefinitionTests
{
    [Fact]
    public void The_shipped_synthetic_matrices_are_valid()
    {
        foreach (var file in new[] { "synthetic-generic-value-approval-matrix.json", "synthetic-approval-matrix-advanced.json" })
        {
            var json = File.ReadAllText(Path.Combine(RepoRoot.Find(), "sample-data", file));
            Assert.Empty(MatrixJson.Errors(json));
        }
        // The generic demonstration is the three half-open tiers of the specification, not the original inclusive bounds.
        var generic = MatrixJson.Parse(File.ReadAllText(Path.Combine(RepoRoot.Find(), "sample-data", "synthetic-generic-value-approval-matrix.json")));
        Assert.Equal([(0m, (decimal?)100000m), (100000m, 500000m), (500000m, null)], generic.Matrices[0].Rows.Select(r => (r.When.ValueMin!.Value, r.When.ValueMax)).ToArray());
    }

    [Fact]
    public void Unknown_members_and_bad_json_are_errors_never_ignored()
    {
        var (set, messages) = MatrixParser.Parse(MatrixJson.Single(MatrixJson.Row("a")).Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"onApproveRun\": \"x\",", StringComparison.Ordinal));
        Assert.Null(set);
        Assert.Contains(messages, m => m.Code == "invalid_json");
        Assert.Contains(MatrixParser.Parse("{ nope").Messages, m => m.Code == "invalid_json");
        Assert.Contains(MatrixParser.Parse("null").Messages, m => m.Code == "invalid_json");
        // A row member that is not in the schema (for example an expression) is refused too.
        Assert.Null(MatrixParser.Parse(MatrixJson.Single(MatrixJson.Row("a").Replace("\"priority\"", "\"if\": \"value > 5\", \"priority\"", StringComparison.Ordinal))).Set);
        // And a date that is not a date.
        Assert.Null(MatrixParser.Parse(MatrixJson.Single(MatrixJson.Row("a", window: "\"effectiveFrom\": \"next tuesday\""))).Set);
    }

    [Fact]
    public void Value_ranges_are_half_open_so_adjacent_tiers_are_valid_and_overlapping_ones_are_not()
    {
        string Tier(string code, string min, string max) => MatrixJson.Row(code, $"{{ \"valueMin\": {min}{(max == "null" ? "" : $", \"valueMax\": {max}")} }}");
        Assert.Empty(MatrixJson.Errors(MatrixJson.Single(Tier("a", "0", "100000"), Tier("b", "100000", "500000"), Tier("c", "500000", "null"))));
        Assert.Contains("range_overlap", MatrixJson.Errors(MatrixJson.Single(Tier("a", "0", "100000.01"), Tier("b", "100000", "null"))));
        Assert.Contains("range_overlap", MatrixJson.Errors(MatrixJson.Single(Tier("a", "0", "200000"), Tier("b", "100000", "300000"), Tier("c", "300000", "null"))));
        Assert.Contains("range_overlap", MatrixJson.Errors(MatrixJson.Single(Tier("a", "0", "null"), Tier("b", "100000", "null"))));
        // A row with no value range shares the others' conditions, so it overlaps every one of them.
        Assert.Contains("range_overlap", MatrixJson.Errors(MatrixJson.Single(Tier("a", "0", "100000"), MatrixJson.Row("any"))));
    }

    [Fact]
    public void Gaps_between_ranges_are_errors_and_open_ends_are_warnings()
    {
        string Tier(string code, string min, string? max) => MatrixJson.Row(code, $"{{ \"valueMin\": {min}{(max is null ? "" : $", \"valueMax\": {max}")} }}");
        var gap = MatrixJson.Single(Tier("a", "0", "100000"), Tier("b", "150000", null));
        Assert.Contains("range_gap", MatrixJson.Errors(gap));
        Assert.Contains(MatrixJson.Check(gap), m => m.Code == "range_gap" && m.Message.Contains("100000", StringComparison.Ordinal) && m.Message.Contains("150000", StringComparison.Ordinal));
        Assert.Contains("range_lower", MatrixJson.Warnings(MatrixJson.Single(Tier("a", "1000", null))));
        Assert.Contains("range_upper", MatrixJson.Warnings(MatrixJson.Single(Tier("a", "0", "1000"))));
        Assert.DoesNotContain("range_upper", MatrixJson.Warnings(MatrixJson.Single(Tier("a", "0", null))));
    }

    [Fact]
    public void Ranges_are_compared_only_among_rows_that_share_every_other_condition()
    {
        // The same value band for two different categories is not an overlap: each category has its own ladder.
        string Band(string code, string category, string min, string? max) => MatrixJson.Row(code, $"{{ \"valueMin\": {min}{(max is null ? "" : $", \"valueMax\": {max}")}, \"categories\": [\"{category}\"] }}");
        var doc = MatrixJson.Single(Band("it-low", "IT", "0", "50000"), Band("it-high", "IT", "50000", null), Band("hr-low", "HR", "0", "10000"), Band("hr-high", "HR", "10000", null));
        Assert.Empty(MatrixJson.Errors(doc));
        // A gap in one category's ladder is still found.
        Assert.Contains("range_gap", MatrixJson.Errors(MatrixJson.Single(Band("it-low", "IT", "0", "50000"), Band("it-high", "IT", "60000", null), Band("hr", "HR", "0", null))));
    }

    [Fact]
    public void Rows_that_can_match_the_same_record_at_the_same_priority_are_a_tie_and_different_priorities_are_not()
    {
        var a = MatrixJson.Row("a", "{ \"businessUnits\": [\"BU1\"] }", priority: 100);
        var b = MatrixJson.Row("b", "{ \"sites\": [\"S1\"] }", priority: 100);    // can match a record in BU1 at site S1
        Assert.Contains("row_tie", MatrixJson.Errors(MatrixJson.Single(a, b)));
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(MatrixJson.Single(a, MatrixJson.Row("b", "{ \"sites\": [\"S1\"] }", priority: 200))));
        // Disjoint conditions cannot meet, so equal priorities are fine.
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", "{ \"businessUnits\": [\"BU1\"] }"), MatrixJson.Row("b", "{ \"businessUnits\": [\"BU2\"] }"))));
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", "{ \"classifications\": [\"Public\"] }"), MatrixJson.Row("b", "{ \"classifications\": [\"Internal\", \"Confidential\"] }"))));
        // Rows in force in different periods cannot meet.
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", window: "\"effectiveTo\": \"2026-12-31\""), MatrixJson.Row("b", window: "\"effectiveFrom\": \"2027-01-01\""))));
        Assert.Contains("row_tie", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", window: "\"effectiveTo\": \"2027-01-01\""), MatrixJson.Row("b", window: "\"effectiveFrom\": \"2027-01-01\""))));
    }

    [Fact]
    public void Custom_field_conditions_that_exclude_each_other_are_not_a_tie()
    {
        string Cust(string op, params string[] values) => $"{{ \"custom\": [ {{ \"field\": \"region\", \"operator\": \"{op}\", \"values\": [{string.Join(",", values.Select(v => $"\"{v}\""))}] }} ] }}";
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", Cust("Equals", "NSW")), MatrixJson.Row("b", Cust("In", "VIC", "QLD")))));
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", Cust("Equals", "NSW")), MatrixJson.Row("b", Cust("NotEquals", "NSW")))));
        Assert.Contains("row_tie", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", Cust("In", "NSW", "VIC")), MatrixJson.Row("b", Cust("In", "VIC", "QLD")))));
    }

    [Fact]
    public void Category_hierarchy_sharpens_tie_detection_when_it_is_known()
    {
        var parent = MatrixJson.Row("parent", "{ \"categories\": [\"ENERGY\"] }");
        var child = MatrixJson.Row("child", "{ \"categories\": [\"ENERGY-ELEC\"] }");
        var doc = MatrixJson.Single(parent, child);
        var reference = MatrixJson.Categories(("ENERGY", ["ENERGY"]), ("ENERGY-ELEC", ["ENERGY-ELEC", "ENERGY"]), ("IT", ["IT"]));
        Assert.Contains("row_tie", MatrixJson.Errors(doc, reference));                 // a record in ENERGY-ELEC matches both
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(doc));                       // without the hierarchy only shared codes are compared
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(MatrixJson.Single(parent.Replace("\"ENERGY\"]", "\"ENERGY\"], \"includeSubcategories\": false", StringComparison.Ordinal), child), reference));
        Assert.DoesNotContain("row_tie", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", "{ \"categories\": [\"IT\"] }"), parent), reference));
        Assert.Contains(MatrixJson.Warnings(doc, reference.CategoryAncestry.Count > 0 ? MatrixJson.Categories(("ENERGY", ["ENERGY"])) : reference), w => w == "reference_unknown");
    }

    [Fact]
    public void Exceptions_have_an_effect_and_replacing_exceptions_cannot_tie()
    {
        var b = MatrixJson.Row("base", "{}");
        Assert.Contains("row_effect", MatrixJson.Errors(MatrixJson.Single(b, MatrixJson.Row("x", kind: "Exception"))));
        Assert.Contains("row_effect", MatrixJson.Errors(MatrixJson.Single(b, MatrixJson.Row("x", kind: "Exception", effect: "DeleteEverything"))));
        Assert.Contains("row_effect", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("base2", effect: "AddSteps"))));
        Assert.Contains("row_tie", MatrixJson.Errors(MatrixJson.Single(b, MatrixJson.Row("r1", kind: "Exception", effect: "ReplaceSteps", priority: 300), MatrixJson.Row("r2", kind: "Exception", effect: "ReplaceSteps", priority: 300))));
        // Several adding exceptions at one priority are fine: all of them apply.
        Assert.Empty(MatrixJson.Errors(MatrixJson.Single(b, MatrixJson.Row("a1", kind: "Exception", effect: "AddSteps", priority: 300), MatrixJson.Row("a2", kind: "Exception", effect: "AddSteps", priority: 300))));
        Assert.Contains("base_required", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("x", kind: "Exception", effect: "AddSteps"))));
    }

    [Fact]
    public void Steps_name_groups_not_people_and_use_the_closed_modes()
    {
        string One(string step) => MatrixJson.Single(MatrixJson.Row("a", steps: step));
        Assert.Contains("approver_group", MatrixJson.Errors(One(MatrixJson.Step(group: "alice@example.test"))));
        Assert.Contains("approver_group", MatrixJson.Errors(One(MatrixJson.Step(group: ""))));
        Assert.Contains("backup_group", MatrixJson.Errors(One(MatrixJson.Step(group: "G", backup: "G"))));
        Assert.Contains("step_mode", MatrixJson.Errors(One(MatrixJson.Step(mode: "Majority"))));
        Assert.Contains("step_duplicate", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", steps: MatrixJson.Step("x") + "," + MatrixJson.Step("x", "GroupB")))));
        Assert.Contains("step_key", MatrixJson.Errors(One(MatrixJson.Step(key: "Not Valid"))));
        Assert.Contains("timeout", MatrixJson.Errors(One(MatrixJson.Step(extra: ", \"timeoutDays\": 4000"))));
        Assert.Contains("row_flow", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", flow: "Diagonal"))));
        Assert.Contains("steps_required", MatrixJson.Errors(MatrixJson.Doc(MatrixJson.Matrix("m", "{ \"code\": \"a\", \"priority\": 1, \"steps\": [] }"))));
    }

    [Fact]
    public void Currency_codes_and_rate_tables_are_checked()
    {
        Assert.Contains("currency", MatrixJson.Errors(MatrixJson.Doc(MatrixJson.Matrix("m", MatrixJson.Row("a"), currency: "dollars"))));
        string Rates(string rates) => MatrixJson.Doc(MatrixJson.Matrix("m", MatrixJson.Row("a"), extra: $", \"rates\": [ {rates} ]"));
        Assert.Empty(MatrixJson.Errors(Rates("{ \"currency\": \"USD\", \"rate\": 1.5, \"effectiveTo\": \"2026-12-31\" }, { \"currency\": \"USD\", \"rate\": 1.6, \"effectiveFrom\": \"2027-01-01\" }, { \"currency\": \"EUR\", \"rate\": 1.7 }")));
        Assert.Contains("rate_overlap", MatrixJson.Errors(Rates("{ \"currency\": \"USD\", \"rate\": 1.5 }, { \"currency\": \"USD\", \"rate\": 1.6, \"effectiveFrom\": \"2027-01-01\" }")));
        Assert.Contains("rate_currency", MatrixJson.Errors(Rates("{ \"currency\": \"AUD\", \"rate\": 1 }")));
        Assert.Contains("rate_value", MatrixJson.Errors(Rates("{ \"currency\": \"USD\", \"rate\": 0 }")));
        Assert.Contains("rate_value", MatrixJson.Errors(Rates("{ \"currency\": \"USD\", \"rate\": -2 }")));
        Assert.Contains("window_order", MatrixJson.Errors(Rates("{ \"currency\": \"USD\", \"rate\": 2, \"effectiveFrom\": \"2027-01-01\", \"effectiveTo\": \"2026-01-01\" }")));
    }

    [Fact]
    public void Windows_codes_priorities_and_classifications_are_checked()
    {
        Assert.Contains("window_order", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", window: "\"effectiveFrom\": \"2027-06-01\", \"effectiveTo\": \"2027-01-01\""))));
        Assert.Contains("matrix_duplicate", MatrixJson.Errors(MatrixJson.Doc(MatrixJson.Matrix("m", MatrixJson.Row("a")), MatrixJson.Matrix("m", MatrixJson.Row("a")))));
        Assert.Contains("row_duplicate", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", "{ \"businessUnits\": [\"X\"] }"), MatrixJson.Row("a", "{ \"businessUnits\": [\"Y\"] }"))));
        Assert.Contains("row_code", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("Bad Code"))));
        Assert.Contains("priority", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", priority: 99999))));
        Assert.Contains("classification_unknown", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", "{ \"classifications\": [\"Top Secret\"] }"))));
        Assert.Contains("value_range", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", "{ \"valueMin\": 10, \"valueMax\": 10 }"))));
        Assert.Contains("value_min", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a", "{ \"valueMin\": -1 }"))));
        Assert.Contains("matrices_required", MatrixJson.Errors(MatrixJson.Doc()));
        Assert.Contains("schema_version", MatrixJson.Errors(MatrixJson.Single(MatrixJson.Row("a")).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)));
    }

    [Fact]
    public void Custom_conditions_use_the_closed_operators_with_the_right_number_of_values()
    {
        string Cust(string op, string values, string field = "region") => MatrixJson.Single(MatrixJson.Row("a", $"{{ \"custom\": [ {{ \"field\": \"{field}\", \"operator\": \"{op}\", \"values\": [{values}] }} ] }}"));
        Assert.Empty(MatrixJson.Errors(Cust("In", "\"A\",\"B\"")));
        Assert.Empty(MatrixJson.Errors(Cust("AtLeast", "\"5000\"")));
        Assert.Contains("custom_operator", MatrixJson.Errors(Cust("Matches", "\"A\"")));
        Assert.Contains("custom_values", MatrixJson.Errors(Cust("Equals", "\"A\",\"B\"")));
        Assert.Contains("custom_values", MatrixJson.Errors(Cust("In", "")));
        Assert.Contains("custom_values", MatrixJson.Errors(Cust("AtLeast", "\"lots\"")));
        Assert.Contains("custom_field", MatrixJson.Errors(Cust("Equals", "\"A\"", "Region Code")));
    }

    [Fact]
    public void Gaps_and_overlaps_are_judged_in_each_period_a_row_is_in_force()
    {
        string Tier(string code, string min, string? max, string? window = null) => MatrixJson.Row(code, $"{{ \"valueMin\": {min}{(max is null ? "" : $", \"valueMax\": {max}")} }}", window: window);
        // Old ladder until the end of 2026, new ladder from 2027: no overlap, no gap in either period.
        var ok = MatrixJson.Single(Tier("old-low", "0", "100000", "\"effectiveTo\": \"2026-12-31\""), Tier("old-high", "100000", null, "\"effectiveTo\": \"2026-12-31\""),
            Tier("new-low", "0", "200000", "\"effectiveFrom\": \"2027-01-01\""), Tier("new-high", "200000", null, "\"effectiveFrom\": \"2027-01-01\""));
        Assert.Empty(MatrixJson.Errors(ok));
        // The new ladder starts a month early: for January 2027's predecessor period the two ladders overlap.
        var overlap = MatrixJson.Single(Tier("old-low", "0", "100000", "\"effectiveTo\": \"2026-12-31\""), Tier("old-high", "100000", null, "\"effectiveTo\": \"2026-12-31\""),
            Tier("new-low", "0", "200000", "\"effectiveFrom\": \"2026-12-01\""), Tier("new-high", "200000", null, "\"effectiveFrom\": \"2026-12-01\""));
        Assert.Contains("range_overlap", MatrixJson.Errors(overlap));
        // After the old ladder ends, only half the new ladder is in force: a gap in that period.
        var gap = MatrixJson.Single(Tier("a", "0", "100000"), Tier("b", "100000", null, "\"effectiveTo\": \"2026-12-31\""));
        Assert.Contains("range_upper", MatrixJson.Warnings(gap));
    }

    [Fact]
    public void A_change_between_two_documents_is_described_row_by_row()
    {
        var before = MatrixJson.Parse(MatrixJson.Single(MatrixJson.Row("a", "{ \"valueMin\": 0, \"valueMax\": 10 }"), MatrixJson.Row("b", "{ \"valueMin\": 10 }")));
        var after = MatrixJson.Parse(MatrixJson.Doc(MatrixJson.Matrix("m", MatrixJson.Row("a", "{ \"valueMin\": 0, \"valueMax\": 20 }") + "," + MatrixJson.Row("c", "{ \"valueMin\": 20 }")), MatrixJson.Matrix("n", MatrixJson.Row("z"))));
        var changes = MatrixDiff.Compare(before, after);
        Assert.Equal(["n"], changes.MatricesAdded);
        Assert.Equal(["m/c"], changes.RowsAdded);
        Assert.Equal(["m/b"], changes.RowsRemoved);
        Assert.Equal(["m/a"], changes.RowsChanged);
        Assert.Contains("rows changed: m/a", changes.Summary);
        Assert.False(MatrixDiff.Compare(before, before).Any);
        Assert.Equal(["m"], MatrixDiff.Compare(null, before).MatricesAdded);
    }
}
