using System.Globalization;
using Ecp.Configuration;
using Ecp.Identity.Domain;
using Ecp.Metadata;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>The custom field definition document, the value rules of every field type, who may see and change a field, and the calculated display fields.</summary>
public class CustomFieldTests
{
    private static string Field(string key, string type, string extra = "") => $"{{ \"key\": \"{key}\", \"label\": \"{key} label\", \"type\": \"{type}\"{(extra.Length == 0 ? "" : ", " + extra)} }}";
    private static string Doc(string fields, string extra = "") => $"{{ \"schemaVersion\": 1{(extra.Length == 0 ? "" : ", " + extra)}, \"fields\": [ {fields} ] }}";
    private static FieldValidationContext Ctx(CustomFieldSet? previous = null, FieldUsage? usage = null, IReadOnlySet<string>? states = null)
        => new(["Public", "Internal", "Confidential", "Restricted", "Legal Privileged"], RoleCodes.All.ToHashSet(), states, previous, usage);

    private static (CustomFieldSet Set, List<ValidationMessage> Messages) Check(string json, FieldValidationContext? ctx = null)
    {
        var (set, messages) = FieldParser.Parse(json);
        Assert.NotNull(set);
        messages.AddRange(FieldParser.Validate(set, ctx ?? Ctx()));
        return (set, messages);
    }
    private static IEnumerable<string> Errors(List<ValidationMessage> m) => m.Where(x => x.Severity == ValidationSeverity.Error).Select(x => x.Code);

    private static ParseResult Parse(string json, params string?[] raw)
    {
        var (set, messages) = Check(json);
        Assert.Empty(Errors(messages));
        return ValueCodec.Parse(set.Fields[0], set, raw);
    }

    [Fact]
    public void A_document_with_every_type_validates_and_an_unknown_member_is_an_error()
    {
        var all = string.Join(",", FieldTypes.All.Where(t => t is not (FieldTypes.SingleChoice or FieldTypes.MultiChoice or FieldTypes.Lookup or FieldTypes.Calculated)).Select(t => Field("f_" + t.ToLowerInvariant(), t)))
            + "," + Field("pick", "SINGLE_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"Alpha\" } ]")
            + "," + Field("many", "MULTI_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"Alpha\" }, { \"code\": \"B\", \"label\": \"Beta\" } ]")
            + "," + Field("shared", "LOOKUP", "\"lookup\": \"colours\"")
            + "," + Field("amount", "CURRENCY") + "," + Field("shown", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Template\", \"template\": \"{amount}\" }");
        var (_, messages) = Check(Doc(all, "\"lookups\": [ { \"name\": \"colours\", \"members\": [ { \"code\": \"RED\", \"label\": \"Red\" } ] } ]"));
        Assert.Empty(Errors(messages));
        var (set, unknown) = FieldParser.Parse("{ \"schemaVersion\": 1, \"fields\": [], \"script\": \"x\" }");
        Assert.Null(set);
        Assert.Contains(unknown, x => x.Code == "invalid_json");
    }

    [Theory]
    [InlineData("BAD KEY", "field_key")]
    [InlineData("Upper", "field_key")]
    [InlineData("agreementReference", "field_key")]
    [InlineData("status", "core_key")]
    public void Keys_are_stable_lower_case_identifiers_that_never_reuse_a_core_field(string key, string code)
        => Assert.Contains(code, Errors(Check(Doc(Field(key, "TEXT"))).Messages));

    [Fact]
    public void Duplicate_keys_and_choice_codes_and_unknown_sections_are_errors()
    {
        Assert.Contains("duplicate_key", Errors(Check(Doc(Field("a_1", "TEXT") + "," + Field("a_1", "TEXT"))).Messages));
        Assert.Contains("duplicate_choice", Errors(Check(Doc(Field("c_1", "SINGLE_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" }, { \"code\": \"a\", \"label\": \"b\" } ]"))).Messages));
        Assert.Contains("unknown_section", Errors(Check(Doc(Field("s_1", "TEXT", "\"section\": \"nowhere\""))).Messages));
        Assert.Empty(Errors(Check(Doc(Field("s_1", "TEXT", "\"section\": \"terms\""), "\"sections\": [ { \"key\": \"terms\", \"label\": \"Terms\", \"order\": 1 } ]")).Messages));
    }

    [Fact]
    public void Validation_that_does_not_belong_to_the_type_is_refused()
    {
        Assert.Contains("validation_unsupported", Errors(Check(Doc(Field("n_1", "INTEGER", "\"validation\": { \"maxLength\": 5 }"))).Messages));
        Assert.Contains("validation_unsupported", Errors(Check(Doc(Field("d_1", "DATE", "\"validation\": { \"min\": 1 }"))).Messages));
        Assert.Contains("validation_unsupported", Errors(Check(Doc(Field("t_1", "LONG_TEXT", "\"validation\": { \"pattern\": \"a\" }"))).Messages));
        Assert.Contains("validation_unsupported", Errors(Check(Doc(Field("i_1", "INTEGER", "\"validation\": { \"decimals\": 2 }"))).Messages));
        Assert.Contains("range", Errors(Check(Doc(Field("p_1", "PERCENTAGE", "\"validation\": { \"max\": 150 }"))).Messages));
        Assert.Contains("range", Errors(Check(Doc(Field("r_1", "DECIMAL", "\"validation\": { \"min\": 9, \"max\": 1 }"))).Messages));
    }

    [Theory]
    [InlineData("(a)\\\\1")]         // a back-reference needs the backtracking engine
    [InlineData("(?<=x)y")]          // so does look-behind
    [InlineData("(a+)+b(")]          // not a pattern at all
    public void An_unsafe_or_unsupported_pattern_is_refused_at_publication(string pattern)
        => Assert.Contains("pattern", Errors(Check(Doc(Field("t_1", "TEXT", $"\"validation\": {{ \"pattern\": \"{pattern}\" }}"))).Messages));

    [Fact]
    public void A_catastrophic_looking_pattern_cannot_hang_a_save_because_the_engine_does_not_backtrack()
    {
        var (set, m) = Check(Doc(Field("t_1", "TEXT", "\"validation\": { \"pattern\": \"^(a+)+$\" }")));
        Assert.Empty(Errors(m));
        var started = DateTime.UtcNow;
        var result = ValueCodec.Parse(set.Fields[0], set, [new string('a', 5000) + "!"]);
        Assert.False(result.IsValid);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Choices_are_required_for_choice_fields_and_forbidden_elsewhere_and_a_lookup_names_a_defined_list()
    {
        Assert.Contains("choices_required", Errors(Check(Doc(Field("c_1", "SINGLE_CHOICE"))).Messages));
        Assert.Contains("choices_not_allowed", Errors(Check(Doc(Field("c_1", "TEXT", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" } ]"))).Messages));
        Assert.Contains("lookup_missing", Errors(Check(Doc(Field("l_1", "LOOKUP", "\"lookup\": \"nothing\""))).Messages));
        Assert.Contains("lookup_not_allowed", Errors(Check(Doc(Field("l_1", "TEXT", "\"lookup\": \"x\""))).Messages));
    }

    [Fact]
    public void A_default_must_be_a_value_the_field_would_accept()
    {
        Assert.Contains("default_invalid", Errors(Check(Doc(Field("n_1", "INTEGER", "\"default\": \"abc\""))).Messages));
        Assert.Contains("default_invalid", Errors(Check(Doc(Field("c_1", "SINGLE_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" } ], \"default\": \"Z\""))).Messages));
        Assert.Contains("default_invalid", Errors(Check(Doc(Field("b_1", "BOOLEAN", "\"default\": {}"))).Messages));
        Assert.Contains("default_invalid", Errors(Check(Doc(Field("p_1", "PERSON", "\"default\": \"11111111-1111-1111-1111-111111111111\""))).Messages));
        Assert.Empty(Errors(Check(Doc(Field("n_1", "INTEGER", "\"default\": 7"))).Messages));
        Assert.Empty(Errors(Check(Doc(Field("m_1", "MULTI_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" }, { \"code\": \"B\", \"label\": \"b\" } ], \"default\": [\"A\", \"B\"]"))).Messages));
    }

    [Fact]
    public void Roles_classification_and_states_must_be_known()
    {
        Assert.Contains("unknown_role", Errors(Check(Doc(Field("t_1", "TEXT", "\"visibleToRoles\": [\"Wizard\"]"))).Messages));
        Assert.Contains("classification", Errors(Check(Doc(Field("t_1", "TEXT", "\"classification\": \"Top Secret\""))).Messages));
        var m = Check(Doc(Field("t_1", "TEXT", "\"editableInStates\": [\"Nonexistent\"]")), Ctx(states: new HashSet<string> { "Draft" })).Messages;
        Assert.Contains(m, x => x.Code == "unknown_state" && x.Severity == ValidationSeverity.Warning);
        Assert.Empty(Errors(m));
    }

    [Fact]
    public void Calculated_fields_are_display_only_and_use_a_closed_list_of_operations_on_plain_fields()
    {
        var ok = Check(Doc(Field("a_1", "DECIMAL") + "," + Field("b_1", "DECIMAL") + "," + Field("sum_1", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Sum\", \"operands\": [\"a_1\", \"b_1\"] }")));
        Assert.Empty(Errors(ok.Messages));
        Assert.Contains("calculated_required", Errors(Check(Doc(Field("c_1", "CALCULATED_DISPLAY", "\"required\": true, \"calculated\": { \"kind\": \"Sum\", \"operands\": [\"estimatedValue\", \"estimatedValue\"] }"))).Messages));
        Assert.Contains("calculated_search", Errors(Check(Doc(Field("c_1", "CALCULATED_DISPLAY", "\"filterable\": true, \"calculated\": { \"kind\": \"Sum\", \"operands\": [\"estimatedValue\", \"estimatedValue\"] }"))).Messages));
        Assert.Contains("calculation_kind", Errors(Check(Doc(Field("c_1", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Eval\", \"template\": \"1+1\" }"))).Messages));
        Assert.Contains("operand_unknown", Errors(Check(Doc(Field("c_1", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Sum\", \"operands\": [\"nope\", \"estimatedValue\"] }"))).Messages));
        // A calculation cannot be built on another calculation (so there are no cycles), nor on text.
        var chained = Doc(Field("a_1", "DECIMAL") + "," + Field("x_1", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Sum\", \"operands\": [\"a_1\", \"a_1\"] }") + "," + Field("y_1", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Sum\", \"operands\": [\"x_1\", \"a_1\"] }"));
        Assert.Contains("operand_invalid", Errors(Check(chained).Messages));
        Assert.Contains("operands", Errors(Check(Doc(Field("t_1", "TEXT") + "," + Field("c_1", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Sum\", \"operands\": [\"t_1\", \"estimatedValue\"] }"))).Messages));
        Assert.Contains("template", Errors(Check(Doc(Field("c_1", "CALCULATED_DISPLAY", "\"calculated\": { \"kind\": \"Template\", \"template\": \"{{{{x}}}} ${evil}\" }"))).Messages));
    }

    [Fact]
    public void The_calculator_works_out_each_operation_and_gives_nothing_for_a_missing_operand_or_a_zero_divisor()
    {
        FieldDefinition Calc(string kind, string[] operands, string? template = null)
            => Check(Doc(Field("c_1", "CALCULATED_DISPLAY", $"\"calculated\": {{ \"kind\": \"{kind}\", \"operands\": [{string.Join(",", operands.Select(o => "\"" + o + "\""))}]{(template is null ? "" : $", \"template\": \"{template}\"")} }}"))).Set.Fields[0];
        var culture = CultureInfo.GetCultureInfo("en-AU");
        decimal? N(string k) => k switch { "estimatedValue" => 1000m, "noticePeriodValue" => 4m, "zero" => 0m, _ => null };
        DateOnly? D(string k) => k switch { "startDate" => new DateOnly(2026, 1, 1), "endDate" => new DateOnly(2026, 1, 31), _ => null };
        Assert.Equal("1,004", Calculator.Evaluate(Calc("Sum", ["estimatedValue", "noticePeriodValue"]), N, D, _ => null, culture));
        Assert.Equal("996", Calculator.Evaluate(Calc("Difference", ["estimatedValue", "noticePeriodValue"]), N, D, _ => null, culture));
        Assert.Equal("250", Calculator.Evaluate(Calc("Ratio", ["estimatedValue", "noticePeriodValue"]), N, D, _ => null, culture));
        Assert.Equal("4,000", Calculator.Evaluate(Calc("Product", ["estimatedValue", "noticePeriodValue"]), N, D, _ => null, culture));
        Assert.Equal("30", Calculator.Evaluate(Calc("DaysBetween", ["startDate", "endDate"]), N, D, _ => null, culture));
        Assert.Equal("Term: x", Calculator.Evaluate(Calc("Template", [], "Term: {contractType}"), N, D, k => k == "contractType" ? "x" : null, culture));
        Assert.Null(Calculator.Evaluate(Calc("Ratio", ["estimatedValue", "zero"]), N, D, _ => null, culture));
        Assert.Null(Calculator.Evaluate(Calc("Sum", ["estimatedValue", "reviewDate"]), N, D, _ => null, culture));
    }

    // ---- values ------------------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("TEXT", "Hello", true)]
    [InlineData("TEXT", "line\nbreak", false)]
    [InlineData("TEXT", "bell\u0007", false)]
    [InlineData("INTEGER", "42", true)]
    [InlineData("INTEGER", "4.2", false)]
    [InlineData("INTEGER", "abc", false)]
    [InlineData("INTEGER", "-7", true)]
    [InlineData("DECIMAL", "12.3456", true)]
    [InlineData("DECIMAL", "12.34567", false)]
    [InlineData("CURRENCY", "1,234.50", true)]
    [InlineData("CURRENCY", "1.234", false)]
    [InlineData("PERCENTAGE", "99.5", true)]
    [InlineData("PERCENTAGE", "100.5", false)]
    [InlineData("PERCENTAGE", "-1", false)]
    [InlineData("DATE", "2026-02-28", true)]
    [InlineData("DATE", "28/02/2026", false)]
    [InlineData("DATE", "2026-02-30", false)]
    [InlineData("DATETIME", "2026-02-28T10:30:00Z", true)]
    [InlineData("DATETIME", "yesterday", false)]
    [InlineData("BOOLEAN", "yes", true)]
    [InlineData("BOOLEAN", "maybe", false)]
    [InlineData("EMAIL", "a.b@example.com", true)]
    [InlineData("EMAIL", "not-an-email", false)]
    [InlineData("EMAIL", "a@b", false)]
    [InlineData("URL", "https://example.com/a?b=c", true)]
    [InlineData("URL", "javascript:alert(1)", false)]
    [InlineData("URL", "ftp://example.com", false)]
    [InlineData("URL", "https://user:pass@example.com", false)]
    [InlineData("URL", "https://exa mple.com", false)]
    [InlineData("PERSON", "11111111-1111-1111-1111-111111111111", true)]
    [InlineData("PERSON", "00000000-0000-0000-0000-000000000000", false)]
    [InlineData("FILE_REFERENCE", "nope", false)]
    public void Every_type_accepts_what_it_should_and_refuses_the_rest(string type, string input, bool valid)
    {
        var result = Parse(Doc(Field("f_1", type)), input);
        Assert.Equal(valid, result.IsValid);
        if (valid) Assert.Single(result.Values);
    }

    [Fact]
    public void Blank_input_is_no_value_and_a_single_value_field_refuses_several()
    {
        Assert.True(Parse(Doc(Field("f_1", "INTEGER")), "  ").IsEmpty);
        Assert.False(Parse(Doc(Field("f_1", "INTEGER")), "1", "2").IsValid);
    }

    [Fact]
    public void Predefined_checks_apply_length_range_pattern_date_and_item_limits()
    {
        Assert.False(Parse(Doc(Field("f_1", "TEXT", "\"validation\": { \"minLength\": 3 }")), "ab").IsValid);
        Assert.False(Parse(Doc(Field("f_1", "TEXT", "\"validation\": { \"maxLength\": 3 }")), "abcd").IsValid);
        Assert.True(Parse(Doc(Field("f_1", "TEXT", "\"validation\": { \"pattern\": \"^[A-Z]{2}\\\\d{4}$\", \"patternMessage\": \"Use AB1234.\" }")), "AB1234").IsValid);
        var bad = Parse(Doc(Field("f_1", "TEXT", "\"validation\": { \"pattern\": \"^[A-Z]{2}\\\\d{4}$\", \"patternMessage\": \"Use AB1234.\" }")), "oops");
        Assert.Equal("Use AB1234.", bad.Errors[0]);
        Assert.False(Parse(Doc(Field("f_1", "INTEGER", "\"validation\": { \"min\": 1, \"max\": 10 }")), "11").IsValid);
        Assert.False(Parse(Doc(Field("f_1", "INTEGER", "\"validation\": { \"min\": 1, \"max\": 10 }")), "0").IsValid);
        Assert.True(Parse(Doc(Field("f_1", "PERCENTAGE", "\"validation\": { \"min\": 5, \"max\": 50 }")), "50").IsValid);
        Assert.False(Parse(Doc(Field("f_1", "DATE", "\"validation\": { \"minDate\": \"2026-01-01\" }")), "2025-12-31").IsValid);
        Assert.False(Parse(Doc(Field("f_1", "DATE", "\"validation\": { \"maxDate\": \"2026-01-01\" }")), "2026-01-02").IsValid);
        var multi = Doc(Field("f_1", "MULTI_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" }, { \"code\": \"B\", \"label\": \"b\" }, { \"code\": \"C\", \"label\": \"c\" } ], \"validation\": { \"minItems\": 2, \"maxItems\": 2 }"));
        Assert.False(Parse(multi, "A").IsValid);
        Assert.True(Parse(multi, "A", "b").IsValid);                       // codes and labels match ignoring case
        Assert.False(Parse(multi, "A", "B", "C").IsValid);
        Assert.Equal(["A", "B"], ValueCodec.ToRaw(Check(multi).Set.Fields[0], Parse(multi, "A", "b", "a").Values));    // the same item twice is one item
    }

    [Fact]
    public void An_inactive_choice_is_refused_for_new_entry_but_kept_where_it_is_already_stored()
    {
        var json = Doc(Field("f_1", "SINGLE_CHOICE", "\"choices\": [ { \"code\": \"OLD\", \"label\": \"Old\", \"active\": false }, { \"code\": \"NEW\", \"label\": \"New\" } ]"));
        var (set, _) = Check(json);
        Assert.False(ValueCodec.Parse(set.Fields[0], set, ["OLD"]).IsValid);
        Assert.True(ValueCodec.Parse(set.Fields[0], set, ["OLD"], new HashSet<string> { "OLD" }).IsValid);
        Assert.True(ValueCodec.Parse(set.Fields[0], set, ["new"]).IsValid);
        Assert.Equal("NEW", ValueCodec.Parse(set.Fields[0], set, ["New"]).Values[0].Text);
    }

    [Fact]
    public void Dates_and_times_are_stored_as_UTC_and_text_is_neutral_about_culture()
    {
        var (set, _) = Check(Doc(Field("d_1", "DATETIME") + "," + Field("n_1", "DECIMAL")));
        var when = ValueCodec.Parse(set.Fields[0], set, ["2026-03-01T10:00:00+10:00"]).Values[0].DateTimeUtc!.Value;
        Assert.Equal(DateTimeKind.Utc, when.Kind);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), when);
        Assert.Equal("2026-03-01T00:00:00Z", ValueCodec.ToRaw(set.Fields[0], [new TypedValue(DateTimeUtc: when)])[0]);
        Assert.Equal("1234.5", ValueCodec.ToRaw(set.Fields[1], ValueCodec.Parse(set.Fields[1], set, ["1,234.50"]).Values)[0]);
        Assert.Contains("10:00", ValueCodec.Format(set.Fields[0], set, [new TypedValue(DateTimeUtc: when)], CultureInfo.GetCultureInfo("en-AU"), TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    }

    // ---- who sees and changes a field --------------------------------------------------------------------------------------------------------------------

    private static FieldDefinition Rule(string extra) => Check(Doc(Field("f_1", "TEXT", extra))).Set.Fields[0];
    private static FieldContext Who(string[] roles, string status = "Draft", string? type = "Services", bool has = false, string today = "2026-06-01")
        => new(roles.ToHashSet(), status, type, DateOnly.Parse(today), has);

    [Fact]
    public void Visibility_and_editability_follow_role_state_and_contract_type()
    {
        var f = Rule("\"visibleToRoles\": [\"Legal\"], \"editableByRoles\": [\"Legal\"], \"visibleInStates\": [\"Draft\", \"Legal Cleared\"], \"editableInStates\": [\"Draft\"], \"contractTypes\": [\"Services\"]");
        Assert.True(FieldRules.Visible(f, Who(["Legal"])));
        Assert.True(FieldRules.Editable(f, Who(["Legal"])));
        Assert.False(FieldRules.Visible(f, Who(["Viewer"])));                       // wrong role
        Assert.False(FieldRules.Visible(f, Who(["Legal"], status: "Awaiting Approval")));   // wrong state
        Assert.True(FieldRules.Visible(f, Who(["Legal"], status: "Legal Cleared")));
        Assert.False(FieldRules.Editable(f, Who(["Legal"], status: "Legal Cleared")));      // visible, not editable in this state
        Assert.False(FieldRules.Visible(f, Who(["Legal"], type: "Goods")));         // a different contract type: not applicable
        Assert.True(FieldRules.Visible(f, Who(["Legal"], type: "Goods", has: true)));        // ... unless it already holds a value
        Assert.False(FieldRules.Editable(f, Who(["Legal"], type: "Goods", has: true)));
        var open = Rule("");
        Assert.True(FieldRules.Editable(open, Who(["Viewer"])));                    // no role list means everyone who can edit the contract
    }

    [Fact]
    public void A_retired_or_out_of_date_field_takes_no_new_values_but_keeps_showing_its_stored_ones()
    {
        var retired = Rule("\"status\": \"Retired\"");
        Assert.False(FieldRules.Editable(retired, Who(["Viewer"], has: true)));
        Assert.True(FieldRules.Visible(retired, Who(["Viewer"], has: true)));
        Assert.False(FieldRules.Visible(retired, Who(["Viewer"], has: false)));
        var windowed = Rule("\"effectiveFrom\": \"2026-07-01\", \"effectiveTo\": \"2026-12-31\"");
        Assert.False(FieldRules.Editable(windowed, Who(["Viewer"], today: "2026-06-30")));
        Assert.True(FieldRules.Editable(windowed, Who(["Viewer"], today: "2026-07-01")));
        Assert.True(FieldRules.Editable(windowed, Who(["Viewer"], today: "2026-12-31")));
        Assert.False(FieldRules.Editable(windowed, Who(["Viewer"], today: "2027-01-01")));
    }

    // ---- change control ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_field_in_use_cannot_be_removed_changed_in_type_or_lose_a_used_choice()
    {
        var before = Check(Doc(Field("c_1", "SINGLE_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" }, { \"code\": \"B\", \"label\": \"b\" } ]") + "," + Field("n_1", "INTEGER") + "," + Field("gone_1", "TEXT"))).Set;
        var usage = new FieldUsage(10, new Dictionary<string, int> { ["c_1"] = 4, ["n_1"] = 2, ["gone_1"] = 1 }, new Dictionary<string, int> { [FieldUsage.MemberKey("c_1", "B")] = 3 });

        var removed = Check(Doc(Field("c_1", "SINGLE_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" } ]") + "," + Field("n_1", "TEXT")), Ctx(before, usage));
        Assert.Contains("member_in_use", Errors(removed.Messages));
        Assert.Contains("type_in_use", Errors(removed.Messages));
        Assert.Contains("field_in_use", Errors(removed.Messages));       // gone_1 vanished

        var retiredNotRemoved = Check(Doc(Field("c_1", "SINGLE_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" }, { \"code\": \"B\", \"label\": \"b\", \"active\": false } ]")
            + "," + Field("n_1", "INTEGER") + "," + Field("gone_1", "TEXT", "\"status\": \"Retired\"")), Ctx(before, usage));
        Assert.Empty(Errors(retiredNotRemoved.Messages));
        Assert.Contains(retiredNotRemoved.Messages, m => m.Code == "retired");

        // An unused field may be removed (with a note), and a newly required field reports how many contracts lack it.
        var unused = Check(Doc(Field("c_1", "SINGLE_CHOICE", "\"choices\": [ { \"code\": \"A\", \"label\": \"a\" }, { \"code\": \"B\", \"label\": \"b\" } ], \"required\": true") + "," + Field("n_1", "INTEGER")), Ctx(before, new FieldUsage(10, new Dictionary<string, int>(), new Dictionary<string, int>())));
        Assert.Empty(Errors(unused.Messages));
        Assert.Contains(unused.Messages, m => m.Code == "required_backfill" && m.Message.Contains("10"));
        Assert.Contains(unused.Messages, m => m.Code == "field_removed");
    }

    [Fact]
    public void Markup_in_labels_and_choices_is_just_text_here_and_is_encoded_where_it_is_shown()
    {
        var (set, m) = Check(Doc(Field("x_1", "SINGLE_CHOICE", "\"label\": \"<script>alert(1)</script>\", \"helpText\": \"<img src=x onerror=alert(1)>\", \"choices\": [ { \"code\": \"A\", \"label\": \"<b>bold</b>\" } ]")));
        Assert.Empty(Errors(m));
        Assert.Equal("<script>alert(1)</script>", set.Fields[0].Label);        // stored verbatim; Razor encodes it on output (see the page tests)
    }
}
