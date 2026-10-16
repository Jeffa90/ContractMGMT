using Ecp.Approvals.Definitions;
using Ecp.Approvals.Domain;
using Ecp.Approvals.Resolution;
using Ecp.SharedKernel;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Unit")]
public sealed class ApprovalMatrixEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 16);

    private static (Selection Selection, ResolutionTrace Trace) Run(string json, ApprovalFacts facts, DateOnly? asOf = null, string code = "m")
    {
        var trace = new ResolutionTrace();
        return (MatrixEvaluator.Select(MatrixJson.Parse(json), code, facts, asOf ?? Today, trace), trace);
    }

    private static ApprovalFacts Facts(decimal? value = 1000m, string? currency = "AUD", string[]? category = null, string? contractType = null, string? legalEntity = null, string? bu = null, string? site = null,
        string? classification = "Internal", string? risk = null, string? supplierType = null, Dictionary<string, string?>? custom = null)
        => new(value, currency, category ?? [], contractType, legalEntity, bu, site, classification, risk, supplierType, custom ?? []);

    private static string Tiers() => MatrixJson.Single(
        MatrixJson.Row("tier-1", "{ \"valueMin\": 0, \"valueMax\": 100000 }", MatrixJson.Step("a", "Tier1")),
        MatrixJson.Row("tier-2", "{ \"valueMin\": 100000, \"valueMax\": 500000 }", MatrixJson.Step("a", "Tier2")),
        MatrixJson.Row("tier-3", "{ \"valueMin\": 500000 }", MatrixJson.Step("a", "Tier3")));

    [Theory]
    [InlineData("0", "tier-1")]
    [InlineData("99999.99", "tier-1")]
    [InlineData("100000", "tier-2")]
    [InlineData("499999.99", "tier-2")]
    [InlineData("500000", "tier-3")]
    [InlineData("9999999999", "tier-3")]
    public void Value_ranges_are_half_open_at_every_boundary(string value, string expectedRow)
    {
        var (s, _) = Run(Tiers(), Facts(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.True(s.Success);
        Assert.Equal(expectedRow, s.BaseRow!.Code);
    }

    [Fact]
    public void A_missing_value_matches_only_rows_without_a_value_range_and_otherwise_blocks()
    {
        var (s, trace) = Run(Tiers(), Facts(value: null));
        Assert.False(s.Success);
        Assert.Equal("no_match", s.BlockCode);
        Assert.Contains(trace.Entries, e => e.Outcome == TraceOutcomes.NotMatched && e.Message.Contains("no value is known", StringComparison.Ordinal));
        var anyValue = MatrixJson.Single(MatrixJson.Row("any"));
        Assert.True(Run(anyValue, Facts(value: null)).Selection.Success);
    }

    [Fact]
    public void No_match_blocks_with_an_explanation_and_never_defaults()
    {
        var (s, trace) = Run(MatrixJson.Single(MatrixJson.Row("it-only", "{ \"categories\": [\"IT\"] }")), Facts(category: ["HR"]));
        Assert.False(s.Success);
        Assert.Equal("no_match", s.BlockCode);
        Assert.Contains("blocked", s.BlockMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(trace.Entries, e => e.Code == "it-only" && e.Outcome == TraceOutcomes.NotMatched && e.Message.Contains("HR", StringComparison.Ordinal));
        Assert.Equal(TraceOutcomes.Blocked, trace.Entries[^1].Outcome);
    }

    [Fact]
    public void The_highest_priority_among_matching_rows_wins_and_the_trace_says_why()
    {
        var doc = MatrixJson.Single(MatrixJson.Row("general", "{}", MatrixJson.Step("a", "General"), priority: 100), MatrixJson.Row("energy", "{ \"categories\": [\"ENERGY\"] }", MatrixJson.Step("a", "Energy"), priority: 200));
        var (s, trace) = Run(doc, Facts(category: ["ENERGY"]));
        Assert.Equal("energy", s.BaseRow!.Code);
        Assert.Contains(trace.Entries, e => e.Stage == TraceStages.Selection && e.Message.Contains("wins on priority 200 over 'general' (100)", StringComparison.Ordinal));
        Assert.Equal("general", Run(doc, Facts(category: ["IT"])).Selection.BaseRow!.Code);
    }

    [Fact]
    public void A_priority_tie_at_run_time_blocks_rather_than_picking_one()
    {
        // The validator refuses to publish this, but a document could still reach the resolver (older release, draft); it must fail closed.
        var doc = MatrixJson.Single(MatrixJson.Row("a", "{ \"businessUnits\": [\"BU1\"] }"), MatrixJson.Row("b", "{ \"sites\": [\"S1\"] }"));
        var (s, _) = Run(doc, Facts(bu: "BU1", site: "S1"));
        Assert.False(s.Success);
        Assert.Equal("priority_tie", s.BlockCode);
        Assert.True(Run(doc, Facts(bu: "BU1", site: "S2")).Selection.Success);
    }

    [Fact]
    public void Category_conditions_follow_the_hierarchy_unless_subcategories_are_excluded()
    {
        string Doc(bool subs) => MatrixJson.Single(MatrixJson.Row("energy", $"{{ \"categories\": [\"ENERGY\"], \"includeSubcategories\": {(subs ? "true" : "false")} }}"));
        var sub = Facts(category: ["ENERGY-ELEC", "ENERGY"]);
        Assert.True(Run(Doc(true), sub).Selection.Success);
        Assert.False(Run(Doc(false), sub).Selection.Success);
        Assert.True(Run(Doc(false), Facts(category: ["ENERGY"])).Selection.Success);
        Assert.False(Run(Doc(true), Facts(category: [])).Selection.Success);          // unknown category never satisfies a category condition
        Assert.True(Run(Doc(true), Facts(category: ["energy"])).Selection.Success);   // codes compare without regard to case
    }

    [Fact]
    public void Every_dimension_narrows_a_row_and_an_unknown_fact_never_satisfies_it()
    {
        void Case(string when, Func<ApprovalFacts> hit, Func<ApprovalFacts> miss, Func<ApprovalFacts> unknown)
        {
            var doc = MatrixJson.Single(MatrixJson.Row("r", when));
            Assert.True(Run(doc, hit()).Selection.Success, "hit: " + when);
            Assert.False(Run(doc, miss()).Selection.Success, "miss: " + when);
            Assert.False(Run(doc, unknown()).Selection.Success, "unknown: " + when);
        }
        Case("{ \"contractTypes\": [\"SERVICES\"] }", () => Facts(contractType: "SERVICES"), () => Facts(contractType: "SUPPLY"), () => Facts());
        Case("{ \"legalEntities\": [\"LE1\", \"LE2\"] }", () => Facts(legalEntity: "le2"), () => Facts(legalEntity: "LE3"), () => Facts());
        Case("{ \"businessUnits\": [\"BU1\"] }", () => Facts(bu: "BU1"), () => Facts(bu: "BU2"), () => Facts());
        Case("{ \"sites\": [\"S1\"] }", () => Facts(site: "S1"), () => Facts(site: "S2"), () => Facts());
        Case("{ \"classifications\": [\"Restricted\", \"Legal Privileged\"] }", () => Facts(classification: "Restricted"), () => Facts(classification: "Internal"), () => Facts(classification: null));
        Case("{ \"riskLevels\": [\"High\"] }", () => Facts(risk: "High"), () => Facts(risk: "Low"), () => Facts());
        Case("{ \"supplierTypes\": [\"Sole\"] }", () => Facts(supplierType: "Sole"), () => Facts(supplierType: "Panel"), () => Facts());
    }

    [Fact]
    public void Custom_field_operators_compare_text_and_numbers_and_never_run_code()
    {
        string Cust(string op, params string[] values) => MatrixJson.Single(MatrixJson.Row("r", $"{{ \"custom\": [ {{ \"field\": \"f\", \"operator\": \"{op}\", \"values\": [{string.Join(",", values.Select(v => $"\"{v}\""))}] }} ] }}"));
        ApprovalFacts F(string? v) => Facts(custom: new Dictionary<string, string?> { ["f"] = v });
        Assert.True(Run(Cust("Equals", "NSW"), F("nsw")).Selection.Success);
        Assert.False(Run(Cust("Equals", "NSW"), F("VIC")).Selection.Success);
        Assert.True(Run(Cust("NotEquals", "NSW"), F("VIC")).Selection.Success);
        Assert.True(Run(Cust("In", "NSW", "VIC"), F("VIC")).Selection.Success);
        Assert.True(Run(Cust("NotIn", "NSW", "VIC"), F("QLD")).Selection.Success);
        Assert.True(Run(Cust("AtLeast", "5000"), F("5000")).Selection.Success);
        Assert.False(Run(Cust("AtLeast", "5000"), F("4999.99")).Selection.Success);
        Assert.True(Run(Cust("LessThan", "5000"), F("4999.99")).Selection.Success);
        Assert.False(Run(Cust("LessThan", "5000"), F("not a number")).Selection.Success);
        Assert.False(Run(Cust("NotEquals", "NSW"), F(null)).Selection.Success);          // unknown never satisfies, not even a negative condition
        Assert.False(Run(Cust("Equals", "NSW"), Facts()).Selection.Success);
        Assert.False(Run(Cust("Equals", "NSW"), F("  ")).Selection.Success);
    }

    [Fact]
    public void Currency_needs_a_rate_in_force_on_the_date_and_is_never_assumed()
    {
        var doc = MatrixJson.Doc(MatrixJson.Matrix("m", MatrixJson.Row("low", "{ \"valueMin\": 0, \"valueMax\": 100000 }") + "," + MatrixJson.Row("high", "{ \"valueMin\": 100000 }"),
            extra: ", \"rates\": [ { \"currency\": \"USD\", \"rate\": 1.5, \"effectiveFrom\": \"2026-01-01\", \"effectiveTo\": \"2026-12-31\" }, { \"currency\": \"USD\", \"rate\": 2, \"effectiveFrom\": \"2027-01-01\" } ]"));
        // 70,000 USD at 1.5 = 105,000 AUD: the high band.
        var (s, trace) = Run(doc, Facts(70000m, "USD"));
        Assert.Equal("high", s.BaseRow!.Code);
        Assert.Equal(105000m, s.ConvertedValue);
        Assert.Contains(trace.Entries, e => e.Stage == TraceStages.Currency && e.Message.Contains("Converted 70,000 USD at 1.5", StringComparison.Ordinal));
        // On a future date the later rate applies: 40,000 USD at 2 = 80,000 AUD, the low band.
        Assert.Equal("low", Run(doc, Facts(40000m, "USD"), new DateOnly(2027, 3, 1)).Selection.BaseRow!.Code);
        // The last day of the first rate and the first day of the next.
        Assert.Equal(1.5m, Run(doc, Facts(1m, "USD"), new DateOnly(2026, 12, 31)).Selection.ConvertedValue);
        Assert.Equal(2m, Run(doc, Facts(1m, "USD"), new DateOnly(2027, 1, 1)).Selection.ConvertedValue);
        // Before any rate is in force, and for a currency with no rate at all, the submission is blocked.
        var early = Run(doc, Facts(1m, "USD"), new DateOnly(2025, 6, 1));
        Assert.Equal("no_rate", early.Selection.BlockCode);
        Assert.Equal("no_rate", Run(doc, Facts(1m, "EUR")).Selection.BlockCode);
        Assert.Equal("currency_missing", Run(doc, Facts(1m, null)).Selection.BlockCode);
        Assert.Equal(1000m, Run(doc, Facts(1000m, "aud")).Selection.ConvertedValue);       // the matrix currency needs no rate
    }

    [Fact]
    public void Effective_dates_apply_to_the_matrix_and_to_each_row_inclusively()
    {
        var doc = MatrixJson.Single(
            MatrixJson.Row("old", "{}", MatrixJson.Step("a", "Old"), window: "\"effectiveTo\": \"2026-12-31\""),
            MatrixJson.Row("new", "{}", MatrixJson.Step("a", "New"), window: "\"effectiveFrom\": \"2027-01-01\""));
        Assert.Equal("old", Run(doc, Facts(), new DateOnly(2026, 12, 31)).Selection.BaseRow!.Code);
        Assert.Equal("new", Run(doc, Facts(), new DateOnly(2027, 1, 1)).Selection.BaseRow!.Code);
        Assert.Equal("old", Run(doc, Facts(), new DateOnly(2020, 1, 1)).Selection.BaseRow!.Code);
        var (past, trace) = Run(doc, Facts(), new DateOnly(2027, 6, 1));
        Assert.Equal("new", past.BaseRow!.Code);
        Assert.Contains(trace.Entries, e => e.Code == "old" && e.Outcome == TraceOutcomes.Skipped && e.Message.Contains("not in force", StringComparison.Ordinal));

        var bounded = MatrixJson.Doc(MatrixJson.Matrix("m", MatrixJson.Row("a"), extra: ", \"effectiveFrom\": \"2027-01-01\", \"effectiveTo\": \"2027-12-31\""));
        Assert.Equal("matrix_not_effective", Run(bounded, Facts(), new DateOnly(2026, 12, 31)).Selection.BlockCode);
        Assert.True(Run(bounded, Facts(), new DateOnly(2027, 1, 1)).Selection.Success);
        Assert.True(Run(bounded, Facts(), new DateOnly(2027, 12, 31)).Selection.Success);
        Assert.Equal("matrix_not_effective", Run(bounded, Facts(), new DateOnly(2028, 1, 1)).Selection.BlockCode);
        Assert.Equal("matrix_not_found", Run(bounded, Facts(), code: "other").Selection.BlockCode);
    }

    [Fact]
    public void Sequential_rows_make_one_stage_per_step_and_parallel_rows_one_stage_for_all()
    {
        var steps = MatrixJson.Step("s1", "G1") + "," + MatrixJson.Step("s2", "G2") + "," + MatrixJson.Step("s3", "G3");
        var seq = Run(MatrixJson.Single(MatrixJson.Row("r", steps: steps, flow: "Sequential")), Facts()).Selection;
        Assert.Equal([0, 1, 2], seq.Steps.Select(s => s.Stage).ToArray());
        var par = Run(MatrixJson.Single(MatrixJson.Row("r", steps: steps, flow: "Parallel")), Facts()).Selection;
        Assert.Equal([0, 0, 0], par.Steps.Select(s => s.Stage).ToArray());
        Assert.Equal(["r/s1", "r/s2", "r/s3"], par.Steps.Select(s => s.Key).ToArray());
    }

    [Fact]
    public void Adding_exceptions_follow_the_base_steps_highest_priority_first()
    {
        var doc = MatrixJson.Single(
            MatrixJson.Row("base", "{}", MatrixJson.Step("b", "Base")),
            MatrixJson.Row("add-low", "{}", MatrixJson.Step("x", "AddLow"), priority: 150, kind: "Exception", effect: "AddSteps"),
            MatrixJson.Row("add-high", "{}", MatrixJson.Step("y", "AddHigh"), priority: 250, kind: "Exception", effect: "AddSteps"),
            MatrixJson.Row("add-other", "{ \"sites\": [\"S9\"] }", MatrixJson.Step("z", "NotMe"), priority: 300, kind: "Exception", effect: "AddSteps"));
        var (s, trace) = Run(doc, Facts());
        Assert.Equal(["base/b", "add-high/y", "add-low/x"], s.Steps.Select(x => x.Key).ToArray());
        Assert.Equal([0, 1, 2], s.Steps.Select(x => x.Stage).ToArray());
        Assert.Equal(["add-high", "add-low"], s.AddRows.Select(r => r.Code).ToArray());
        Assert.Contains(trace.Entries, e => e.Stage == TraceStages.Exception && e.Code == "add-high" && e.Outcome == TraceOutcomes.Applied);
        Assert.DoesNotContain(trace.Entries, e => e.Stage == TraceStages.Exception && e.Code == "add-other");
    }

    [Fact]
    public void A_replacing_exception_swaps_the_base_steps_and_the_highest_one_wins()
    {
        var doc = MatrixJson.Single(
            MatrixJson.Row("base", "{}", MatrixJson.Step("b", "Base")),
            MatrixJson.Row("replace-1", "{}", MatrixJson.Step("e1", "Exec1"), priority: 200, kind: "Exception", effect: "ReplaceSteps"),
            MatrixJson.Row("replace-2", "{}", MatrixJson.Step("e2", "Exec2"), priority: 300, kind: "Exception", effect: "ReplaceSteps"),
            MatrixJson.Row("add", "{}", MatrixJson.Step("x", "Extra"), priority: 100, kind: "Exception", effect: "AddSteps"));
        var (s, trace) = Run(doc, Facts());
        Assert.Equal(["replace-2/e2", "add/x"], s.Steps.Select(x => x.Key).ToArray());
        Assert.Equal("base", s.BaseRow!.Code);
        Assert.Equal("replace-2", s.ReplaceRow!.Code);
        Assert.Contains(trace.Entries, e => e.Code == "replace-2" && e.Message.Contains("replaces the steps of 'base'", StringComparison.Ordinal) && e.Message.Contains("outranks 'replace-1'", StringComparison.Ordinal));
        // Two replacing exceptions at one priority have no winner: blocked.
        var tie = MatrixJson.Single(MatrixJson.Row("base"), MatrixJson.Row("r1", priority: 300, kind: "Exception", effect: "ReplaceSteps"), MatrixJson.Row("r2", priority: 300, kind: "Exception", effect: "ReplaceSteps"));
        Assert.Equal("priority_tie", Run(tie, Facts()).Selection.BlockCode);
    }

    [Fact]
    public void An_exception_alone_never_approves_anything()
    {
        var doc = MatrixJson.Single(MatrixJson.Row("base", "{ \"categories\": [\"IT\"] }"), MatrixJson.Row("only-exception", "{}", priority: 500, kind: "Exception", effect: "ReplaceSteps"));
        var (s, _) = Run(doc, Facts(category: ["HR"]));
        Assert.False(s.Success);
        Assert.Equal("no_match", s.BlockCode);
    }

    [Fact]
    public void The_trace_is_in_order_and_complete()
    {
        var (s, trace) = Run(Tiers(), Facts(250000m));
        Assert.True(s.Success);
        var entries = trace.Entries;
        Assert.Equal(Enumerable.Range(1, entries.Count), entries.Select(e => e.Sequence));
        Assert.Equal(TraceStages.Matrix, entries[0].Stage);
        Assert.Equal(3, entries.Count(e => e.Stage == TraceStages.Row));
        Assert.Contains(entries, e => e.Code == "tier-1" && e.Outcome == TraceOutcomes.NotMatched && e.Message.Contains("not below the maximum 100,000 (the maximum is excluded)", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Code == "tier-2" && e.Outcome == TraceOutcomes.Matched);
        Assert.Contains(entries, e => e.Stage == TraceStages.Selection && e.Outcome == TraceOutcomes.Selected);
        Assert.Contains(entries, e => e.Stage == TraceStages.Plan);
    }
}

[Trait("Category", "Unit")]
public sealed class ApprovalPlanTests
{
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Carol = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Dave = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid Erin = Guid.Parse("00000000-0000-0000-0000-00000000000e");

    private static PlanApprover Primary(Guid id, string name) => new(id, name, ParticipantRoles.Primary, null, null, "primary");
    private static PlanApprover Backup(Guid id, string name) => new(id, name, ParticipantRoles.Backup, null, null, "backup");
    private static PlanApprover Delegate(Guid id, string name, Guid forId, string forName) => new(id, name, ParticipantRoles.Delegate, forId, forName, "delegate");

    private static PlanStep Step(string key, int stage, string mode, bool allowBackup, params PlanApprover[] people) => new(key, "row", key, stage, "G-" + key, null, mode, null, null, allowBackup, people);
    private static ApprovalPlan Plan(params PlanStep[] steps) => new(1, "m", "m", 1, null, "hash", DateTime.UtcNow, new DateOnly(2026, 10, 16), "AUD", "AUD", 1m, 1m, ["row"], steps);

    private static PlanResponse Approve(string step, Guid by, Guid? onBehalf = null, string role = ParticipantRoles.Primary, string name = "x") => new(step, by, name, role, onBehalf, null, true, null, DateTime.UtcNow);
    private static PlanResponse Reject(string step, Guid by) => new(step, by, "x", ParticipantRoles.Primary, null, null, false, "no", DateTime.UtcNow);

    [Fact]
    public void A_first_response_step_is_settled_by_one_approval_from_any_listed_person()
    {
        var plan = Plan(Step("s", 0, StepModes.FirstResponse, true, Primary(Alice, "Alice"), Backup(Bob, "Bob")));
        Assert.Equal(PlanStatus.InProgress, ApprovalPlans.Evaluate(plan, []).Status);
        Assert.Equal(PlanStatus.Approved, ApprovalPlans.Evaluate(plan, [Approve("s", Bob, role: ParticipantRoles.Backup)]).Status);
        Assert.Equal(PlanStatus.Approved, ApprovalPlans.Evaluate(plan, [Approve("s", Alice)]).Status);
    }

    [Fact]
    public void An_all_required_step_needs_every_named_primary_and_reports_who_is_outstanding()
    {
        var plan = Plan(Step("s", 0, StepModes.AllRequired, false, Primary(Alice, "Alice"), Primary(Bob, "Bob"), Primary(Carol, "Carol")));
        var one = ApprovalPlans.Evaluate(plan, [Approve("s", Alice)]);
        Assert.Equal(PlanStatus.InProgress, one.Status);
        Assert.Equal(["Bob", "Carol"], one.Steps[0].Outstanding.Select(o => o.Name).OrderBy(x => x).ToArray());
        Assert.Equal(PlanStatus.InProgress, ApprovalPlans.Evaluate(plan, [Approve("s", Alice), Approve("s", Bob)]).Status);
        Assert.Equal(PlanStatus.Approved, ApprovalPlans.Evaluate(plan, [Approve("s", Alice), Approve("s", Bob), Approve("s", Carol)]).Status);
    }

    [Fact]
    public void An_all_required_step_with_no_named_primary_can_never_be_satisfied()
    {
        var plan = Plan(Step("s", 0, StepModes.AllRequired, true, Backup(Bob, "Bob")));
        Assert.False(ApprovalPlans.IsSatisfied(plan.Steps[0], [Approve("s", Bob, role: ParticipantRoles.Backup)]));
    }

    [Fact]
    public void A_delegate_covers_the_seat_of_the_person_who_delegated()
    {
        var plan = Plan(Step("s", 0, StepModes.AllRequired, false, Primary(Alice, "Alice"), Primary(Bob, "Bob"), Delegate(Dave, "Dave", Alice, "Alice")));
        var state = ApprovalPlans.Evaluate(plan, [Approve("s", Dave, onBehalf: Alice, role: ParticipantRoles.Delegate), Approve("s", Bob)]);
        Assert.Equal(PlanStatus.Approved, state.Status);
        // Alice does not also need to respond, and cannot: her seat is covered.
        var placement = ApprovalPlans.Place(plan, [Approve("s", Dave, onBehalf: Alice, role: ParticipantRoles.Delegate)], "s", Alice, [Primary(Alice, "Alice")]);
        Assert.False(placement.Allowed);
        Assert.Equal("not_a_required_approver", placement.ErrorCode);
    }

    [Fact]
    public void Sequential_stages_open_one_at_a_time()
    {
        var plan = Plan(Step("a", 0, StepModes.FirstResponse, true, Primary(Alice, "Alice")), Step("b", 1, StepModes.FirstResponse, true, Primary(Bob, "Bob")), Step("c", 2, StepModes.FirstResponse, true, Primary(Carol, "Carol")));
        var s0 = ApprovalPlans.Evaluate(plan, []);
        Assert.Equal([StepStatus.Open, StepStatus.Waiting, StepStatus.Waiting], s0.Steps.Select(s => s.Status).ToArray());
        Assert.Equal(["G-a"], s0.ActiveGroupKeys);
        var s1 = ApprovalPlans.Evaluate(plan, [Approve("a", Alice)]);
        Assert.Equal([StepStatus.Satisfied, StepStatus.Open, StepStatus.Waiting], s1.Steps.Select(s => s.Status).ToArray());
        Assert.Equal(["G-b"], s1.ActiveGroupKeys);
        Assert.Equal(PlanStatus.Approved, ApprovalPlans.Evaluate(plan, [Approve("a", Alice), Approve("b", Bob), Approve("c", Carol)]).Status);
        // A later step cannot be answered ahead of its turn.
        var early = ApprovalPlans.Place(plan, [], "b", Bob, [Primary(Bob, "Bob")]);
        Assert.False(early.Allowed);
        Assert.Equal("step_not_open", early.ErrorCode);
    }

    [Fact]
    public void Parallel_steps_are_open_together_and_the_next_stage_waits_for_all_of_them()
    {
        var plan = Plan(Step("a", 0, StepModes.FirstResponse, true, Primary(Alice, "Alice")), Step("b", 0, StepModes.AllRequired, false, Primary(Bob, "Bob")), Step("c", 1, StepModes.FirstResponse, true, Primary(Carol, "Carol")));
        var open = ApprovalPlans.Evaluate(plan, []);
        Assert.Equal(["a", "b"], open.Open.Select(s => s.Step.Key).OrderBy(x => x).ToArray());
        Assert.Equal(["G-a", "G-b"], open.ActiveGroupKeys.OrderBy(x => x).ToArray());
        var half = ApprovalPlans.Evaluate(plan, [Approve("a", Alice)]);
        Assert.Equal(["b"], half.Open.Select(s => s.Step.Key).ToArray());
        Assert.Equal(StepStatus.Waiting, half.Steps.First(s => s.Step.Key == "c").Status);
        var both = ApprovalPlans.Evaluate(plan, [Approve("a", Alice), Approve("b", Bob)]);
        Assert.Equal(["c"], both.Open.Select(s => s.Step.Key).ToArray());
    }

    [Fact]
    public void Any_rejection_on_an_open_step_rejects_the_whole_plan()
    {
        var plan = Plan(Step("a", 0, StepModes.FirstResponse, true, Primary(Alice, "Alice")), Step("b", 1, StepModes.FirstResponse, true, Primary(Bob, "Bob")));
        var state = ApprovalPlans.Evaluate(plan, [Approve("a", Alice), Reject("b", Bob)]);
        Assert.Equal(PlanStatus.Rejected, state.Status);
        Assert.Equal(StepStatus.Rejected, state.Steps[1].Status);
        Assert.Empty(state.ActiveGroupKeys);
    }

    [Fact]
    public void Placement_records_on_behalf_of_for_backups_and_delegates_and_refuses_repeats()
    {
        var first = Step("s", 0, StepModes.FirstResponse, true, Primary(Alice, "Alice"), Backup(Bob, "Bob"), Delegate(Dave, "Dave", Alice, "Alice"));
        var plan = Plan(first);
        var backup = ApprovalPlans.Place(plan, [], "s", Bob, [Backup(Bob, "Bob")]);
        Assert.True(backup.Allowed);
        Assert.Equal(Alice, backup.OnBehalfOfUserId);              // one primary: the backup stands in for that person
        Assert.Equal(ParticipantRoles.Backup, backup.Role);
        var delegated = ApprovalPlans.Place(plan, [], "s", Dave, [Delegate(Dave, "Dave", Alice, "Alice")]);
        Assert.Equal((ParticipantRoles.Delegate, Alice), (delegated.Role, delegated.OnBehalfOfUserId));
        Assert.Null(ApprovalPlans.Place(plan, [], "s", Alice, [Primary(Alice, "Alice")]).OnBehalfOfUserId);
        // Two primaries: a backup is not "on behalf of" anyone in particular.
        var twoPrimaries = Plan(Step("s", 0, StepModes.FirstResponse, true, Primary(Alice, "Alice"), Primary(Carol, "Carol"), Backup(Bob, "Bob")));
        Assert.Null(ApprovalPlans.Place(twoPrimaries, [], "s", Bob, [Backup(Bob, "Bob")]).OnBehalfOfUserId);
        // Once settled, a first-response step takes no more responses; and nobody answers twice.
        Assert.Equal("step_not_open", ApprovalPlans.Place(plan, [Approve("s", Alice)], "s", Bob, [Backup(Bob, "Bob")]).ErrorCode);
        var all = Plan(Step("s", 0, StepModes.AllRequired, false, Primary(Alice, "Alice"), Primary(Carol, "Carol")));
        Assert.Equal("already_responded", ApprovalPlans.Place(all, [Approve("s", Alice)], "s", Alice, [Primary(Alice, "Alice")]).ErrorCode);
        Assert.Equal("not_a_participant", ApprovalPlans.Place(all, [], "s", Erin, []).ErrorCode);
        Assert.Equal("step_unknown", ApprovalPlans.Place(all, [], "zzz", Alice, [Primary(Alice, "Alice")]).ErrorCode);
    }

    [Fact]
    public void A_backup_stands_in_for_an_all_required_seat_only_where_the_step_allows_it()
    {
        PlanStep Make(bool allow) => Step("s", 0, StepModes.AllRequired, allow, Primary(Alice, "Alice"), Primary(Carol, "Carol"), Backup(Bob, "Bob"));
        var denied = ApprovalPlans.Place(Plan(Make(false)), [], "s", Bob, [Backup(Bob, "Bob")]);
        Assert.False(denied.Allowed);
        Assert.Equal("backup_not_allowed", denied.ErrorCode);
        var plan = Plan(Make(true));
        var covers = ApprovalPlans.Place(plan, [], "s", Bob, [Backup(Bob, "Bob")]);
        Assert.True(covers.Allowed);
        Assert.Equal(Alice, covers.OnBehalfOfUserId);                // the first outstanding seat by name
        // The next backup response covers the next seat; when all are covered there is nothing left to cover.
        var afterOne = new[] { Approve("s", Bob, onBehalf: Alice, role: ParticipantRoles.Backup) };
        Assert.Equal(Carol, ApprovalPlans.Place(plan, afterOne, "s", Dave, [Backup(Dave, "Dave")]).OnBehalfOfUserId);
        var covered = new[] { Approve("s", Alice), Approve("s", Carol) };
        Assert.Equal(PlanStatus.Approved, ApprovalPlans.Evaluate(plan, covered).Status);
    }

    [Fact]
    public void Outstanding_lists_everyone_for_a_first_response_step_until_it_is_settled()
    {
        var plan = Plan(Step("s", 0, StepModes.FirstResponse, true, Primary(Alice, "Alice"), Backup(Bob, "Bob")));
        Assert.Equal(2, ApprovalPlans.Outstanding(plan.Steps[0], []).Count);
        Assert.Empty(ApprovalPlans.Outstanding(plan.Steps[0], [Approve("s", Alice)]));
    }
}

[Trait("Category", "Unit")]
public sealed class ApprovalDelegationRuleTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

    private static DelegationGraph.Edge E(Guid from, Guid to) => new(from, to);

    [Fact]
    public void A_loop_is_found_whether_it_has_two_members_or_many()
    {
        Assert.True(DelegationGraph.WouldCreateCycle([E(A, B)], B, A));                          // A to B, then B back to A
        Assert.True(DelegationGraph.WouldCreateCycle([E(A, B), E(B, C)], C, A));               // A to B to C, then C back to A
        Assert.True(DelegationGraph.WouldCreateCycle([E(A, B), E(B, C), E(C, D)], D, B));      // closes a loop that does not include the first person
        Assert.False(DelegationGraph.WouldCreateCycle([E(A, B), E(B, C)], A, C));              // a shortcut is not a loop
        Assert.False(DelegationGraph.WouldCreateCycle([E(A, B)], C, D));
        Assert.False(DelegationGraph.WouldCreateCycle([], A, B));
        Assert.True(DelegationGraph.WouldCreateCycle([], A, A));                                  // delegating to yourself is the smallest loop
    }

    [Fact]
    public void Edges_that_are_part_of_a_loop_are_identified_so_resolution_can_ignore_them()
    {
        var inLoop = DelegationGraph.EdgesInCycles([E(A, B), E(B, C), E(C, A), E(C, D)]);
        Assert.Equal([E(A, B), E(B, C), E(C, A)], inLoop.OrderBy(e => e.From).ThenBy(e => e.To).ToArray().OrderBy(e => new[] { A, B, C }.ToList().IndexOf(e.From)).ToArray());
        Assert.DoesNotContain(E(C, D), inLoop);
        Assert.Empty(DelegationGraph.EdgesInCycles([E(A, B), E(B, C)]));
        Assert.Empty(DelegationGraph.EdgesInCycles([]));
    }

    [Fact]
    public void A_delegation_has_a_bounded_future_window_a_reason_and_a_different_delegate()
    {
        Guid t = Guid.NewGuid(), o = Guid.NewGuid();
        ApprovalDelegation Make(Guid to, DateTime from, DateTime until, string reason = "On leave") => ApprovalDelegation.Create(t, o, "G", A, "Alice", to, "Bob", from, until, reason, Now);
        var ok = Make(B, Now, Now.AddDays(14));
        Assert.True(ok.IsActiveAt(Now.AddDays(1)));
        Assert.False(ok.IsActiveAt(Now.AddDays(14)));                // the end is excluded
        Assert.False(ok.IsActiveAt(Now.AddDays(-1)));
        Assert.Equal("delegation_self", Assert.Throws<DomainException>(() => Make(A, Now, Now.AddDays(1))).Code);
        Assert.Equal("delegation_window", Assert.Throws<DomainException>(() => Make(B, Now.AddDays(2), Now.AddDays(1))).Code);
        Assert.Equal("delegation_window", Assert.Throws<DomainException>(() => Make(B, Now.AddDays(-5), Now.AddDays(-1))).Code);
        Assert.Equal("delegation_window", Assert.Throws<DomainException>(() => Make(B, Now, Now.AddDays(ApprovalDelegation.MaxDays + 1))).Code);
        Assert.NotNull(Make(B, Now, Now.AddDays(ApprovalDelegation.MaxDays)));
        Assert.ThrowsAny<DomainException>(() => Make(B, Now, Now.AddDays(1), " "));
    }

    [Fact]
    public void A_delegation_can_be_ended_once_and_an_ended_one_no_longer_applies()
    {
        var d = ApprovalDelegation.Create(Guid.NewGuid(), Guid.NewGuid(), "G", A, "Alice", B, "Bob", Now, Now.AddDays(30), "Leave", Now);
        Assert.True(d.Overlaps(Now.AddDays(1), Now.AddDays(2)));
        d.Revoke("Alice", "Back early", Now.AddDays(1));
        Assert.False(d.IsActiveAt(Now.AddDays(2)));
        Assert.False(d.Overlaps(Now.AddDays(1), Now.AddDays(2)));
        Assert.Equal("delegation_revoked", Assert.Throws<DomainException>(() => d.Revoke("Alice", "again", Now.AddDays(2))).Code);
    }
}
