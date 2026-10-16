using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ecp.Configuration;

namespace Ecp.Metadata;

/// <summary>What the validator needs to know about the world outside the document. Everything is optional: an absent fact only removes the check that needed it.</summary>
public sealed record FieldValidationContext(
    IReadOnlyList<string> Classifications, IReadOnlySet<string> KnownRoles, IReadOnlySet<string>? KnownStates = null, CustomFieldSet? Previous = null, FieldUsage? Usage = null);

/// <summary>How much of the existing data depends on the published definitions: used to refuse removing what is in use and to show the impact of a change.</summary>
public sealed record FieldUsage(int Contracts, IReadOnlyDictionary<string, int> ValuesByKey, IReadOnlyDictionary<string, int> ValuesByMember)
{
    public static readonly FieldUsage None = new(0, new Dictionary<string, int>(), new Dictionary<string, int>());
    public static string MemberKey(string key, string member) => key.ToLowerInvariant() + "\u001f" + member;
    public int Of(string key) => ValuesByKey.GetValueOrDefault(key.ToLowerInvariant());
    public int Of(string key, string member) => ValuesByMember.GetValueOrDefault(MemberKey(key, member));
}

/// <summary>Reads a custom field document strictly (an unknown member is an error, never ignored) and checks it. Publication is refused while any error remains.</summary>
public static partial class FieldParser
{
    public const int MaxFields = 300, MaxSections = 40, MaxLists = 100, MaxMembers = 500, MaxChoices = 200;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, ReadCommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16, NumberHandling = JsonNumberHandling.Strict
    };

    private sealed class RawDoc { public int SchemaVersion { get; set; } public List<RawSection>? Sections { get; set; } public List<RawList>? Lookups { get; set; } public List<RawField>? Fields { get; set; } }
    private sealed class RawSection { public string? Key { get; set; } public string? Label { get; set; } public int Order { get; set; } }
    private sealed class RawChoice { public string? Code { get; set; } public string? Label { get; set; } public bool? Active { get; set; } }
    private sealed class RawList { public string? Name { get; set; } public string? Label { get; set; } public List<RawChoice>? Members { get; set; } }
    private sealed class RawValidation
    {
        public int? MinLength { get; set; } public int? MaxLength { get; set; } public string? Pattern { get; set; } public string? PatternMessage { get; set; } public decimal? Min { get; set; } public decimal? Max { get; set; }
        public int? Decimals { get; set; } public DateOnly? MinDate { get; set; } public DateOnly? MaxDate { get; set; } public int? MinItems { get; set; } public int? MaxItems { get; set; } public string? Currency { get; set; }
    }
    private sealed class RawCalc { public string? Kind { get; set; } public List<string>? Operands { get; set; } public string? Template { get; set; } public int? Decimals { get; set; } }
    private sealed class RawField
    {
        public string? Key { get; set; } public string? Label { get; set; } public string? Description { get; set; } public string? HelpText { get; set; } public string? Placeholder { get; set; } public string? Type { get; set; }
        public string? Section { get; set; } public int Order { get; set; } public bool Required { get; set; } public JsonElement? Default { get; set; } public RawValidation? Validation { get; set; } public List<RawChoice>? Choices { get; set; }
        public string? Lookup { get; set; } public RawCalc? Calculated { get; set; } public bool Searchable { get; set; } public bool Filterable { get; set; } public bool Sortable { get; set; } public bool Reportable { get; set; }
        public string? Classification { get; set; } public List<string>? VisibleToRoles { get; set; } public List<string>? EditableByRoles { get; set; } public List<string>? VisibleInStates { get; set; }
        public List<string>? EditableInStates { get; set; } public List<string>? ContractTypes { get; set; } public string? Status { get; set; } public DateOnly? EffectiveFrom { get; set; } public DateOnly? EffectiveTo { get; set; } public bool UsableInApprovals { get; set; }
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]{1,62}$", RegexOptions.NonBacktracking)] private static partial Regex KeyPattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _./&-]{0,49}$", RegexOptions.NonBacktracking)] private static partial Regex CodePattern();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,48}$", RegexOptions.NonBacktracking)] private static partial Regex ListNamePattern();
    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.NonBacktracking)] private static partial Regex Placeholder();

    private static ValidationMessage Err(string path, string code, string message) => new(ValidationSeverity.Error, path, code, message);
    private static ValidationMessage Warn(string path, string code, string message) => new(ValidationSeverity.Warning, path, code, message);

    public static (CustomFieldSet? Set, List<ValidationMessage> Messages) Parse(string json)
    {
        var messages = new List<ValidationMessage>();
        RawDoc? raw;
        try { raw = JsonSerializer.Deserialize<RawDoc>(json, Options); }
        catch (JsonException ex) { messages.Add(Err("$", "invalid_json", "The custom field definitions could not be read: " + ex.Message.Split('\n')[0])); return (null, messages); }
        if (raw is null) { messages.Add(Err("$", "invalid_json", "The custom field document is empty.")); return (null, messages); }

        static IReadOnlyList<string> L(List<string>? l) => (l ?? []).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).ToList();
        static IReadOnlyList<FieldChoice> C(List<RawChoice>? l) => (l ?? []).Select(c => new FieldChoice(c.Code?.Trim() ?? "", c.Label?.Trim() ?? "", c.Active ?? true)).ToList();

        var fields = new List<FieldDefinition>();
        for (var i = 0; i < (raw.Fields?.Count ?? 0); i++)
        {
            var f = raw.Fields![i];
            var v = f.Validation;
            var defaults = DefaultOf(f.Default, $"$.fields[{i}].default", messages);
            fields.Add(new FieldDefinition(
                f.Key?.Trim() ?? "", f.Label?.Trim() ?? "", f.Description?.Trim() ?? "", f.HelpText?.Trim() ?? "", f.Placeholder?.Trim() ?? "", f.Type?.Trim().ToUpperInvariant() ?? "", f.Section?.Trim().ToLowerInvariant() ?? "general",
                f.Order, f.Required, defaults,
                v is null ? FieldValidation.None : new FieldValidation(v.MinLength, v.MaxLength, string.IsNullOrWhiteSpace(v.Pattern) ? null : v.Pattern, v.PatternMessage?.Trim(), v.Min, v.Max, v.Decimals, v.MinDate, v.MaxDate, v.MinItems, v.MaxItems,
                    v.Currency?.Trim().ToUpperInvariant()),
                C(f.Choices), string.IsNullOrWhiteSpace(f.Lookup) ? null : f.Lookup.Trim(),
                f.Calculated is null ? null : new CalculatedSpec(f.Calculated.Kind?.Trim() ?? "", L(f.Calculated.Operands), f.Calculated.Template, f.Calculated.Decimals),
                f.Searchable, f.Filterable, f.Sortable, f.Reportable, f.Classification?.Trim() ?? "Internal", L(f.VisibleToRoles), L(f.EditableByRoles), L(f.VisibleInStates), L(f.EditableInStates), L(f.ContractTypes),
                f.Status?.Trim() ?? FieldStatuses.Active, f.EffectiveFrom, f.EffectiveTo, f.UsableInApprovals));
        }
        var set = new CustomFieldSet
        {
            SchemaVersion = raw.SchemaVersion,
            Sections = (raw.Sections ?? []).Select(s => new FieldSection(s.Key?.Trim().ToLowerInvariant() ?? "", s.Label?.Trim() ?? "", s.Order)).ToList(),
            Lookups = (raw.Lookups ?? []).Select(l => new LookupList(l.Name?.Trim() ?? "", l.Label?.Trim() ?? l.Name?.Trim() ?? "", C(l.Members))).ToList(),
            Fields = fields
        };
        return (set, messages);
    }

    private static IReadOnlyList<string> DefaultOf(JsonElement? e, string path, List<ValidationMessage> messages)
    {
        if (e is not { } el || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return [];
        string One(JsonElement x, bool top)
        {
            switch (x.ValueKind)
            {
                case JsonValueKind.String: return x.GetString() ?? "";
                case JsonValueKind.Number: return x.GetDecimal().ToString(CultureInfo.InvariantCulture);
                case JsonValueKind.True: return "true";
                case JsonValueKind.False: return "false";
                default: messages.Add(Err(path, "default_invalid", "A default value must be text, a number, yes/no, or a list of text.")); return "";
            }
        }
        if (el.ValueKind == JsonValueKind.Array) return el.EnumerateArray().Select(x => One(x, false)).Where(x => x.Length > 0).ToList();
        var single = One(el, true);
        return single.Length == 0 ? [] : [single];
    }

    // ---- validation ----------------------------------------------------------------------------------------------------------------------------------------

    public static List<ValidationMessage> Validate(CustomFieldSet set, FieldValidationContext ctx)
    {
        var m = new List<ValidationMessage>();
        if (set.SchemaVersion != 1) m.Add(Err("$.schemaVersion", "schema_version", "The schema version must be 1."));
        if (set.Fields.Count > MaxFields) m.Add(Err("$.fields", "too_many", $"At most {MaxFields} fields are allowed."));
        if (set.Sections.Count > MaxSections) m.Add(Err("$.sections", "too_many", $"At most {MaxSections} sections are allowed."));
        if (set.Lookups.Count > MaxLists) m.Add(Err("$.lookups", "too_many", $"At most {MaxLists} lookup lists are allowed."));

        // Sections.
        var sectionKeys = new HashSet<string>(StringComparer.Ordinal) { "general" };
        for (var i = 0; i < set.Sections.Count; i++)
        {
            var s = set.Sections[i]; var p = $"$.sections[{i}]";
            if (!KeyPattern().IsMatch(s.Key)) m.Add(Err(p + ".key", "section_key", "A section key is lower-case letters, digits and underscores, starting with a letter."));
            else if (!sectionKeys.Add(s.Key) && s.Key != "general") m.Add(Err(p + ".key", "duplicate_section", $"The section key '{s.Key}' is used twice."));
            if (string.IsNullOrWhiteSpace(s.Label) || s.Label.Length > 100) m.Add(Err(p + ".label", "section_label", "A section needs a label of up to 100 characters."));
        }

        // Lookup lists.
        var listNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < set.Lookups.Count; i++)
        {
            var l = set.Lookups[i]; var p = $"$.lookups[{i}]";
            if (!ListNamePattern().IsMatch(l.Name)) m.Add(Err(p + ".name", "list_name", "A lookup list name is letters, digits and underscores, starting with a letter."));
            else if (!listNames.Add(l.Name)) m.Add(Err(p + ".name", "duplicate_list", $"The lookup list '{l.Name}' is defined twice."));
            CheckMembers(l.Members, p + ".members", MaxMembers, m);
        }

        // Fields.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < set.Fields.Count; i++)
        {
            var f = set.Fields[i]; var p = $"$.fields[{i}]";
            if (!KeyPattern().IsMatch(f.Key)) m.Add(Err(p + ".key", "field_key", "A field key is lower-case letters, digits and underscores, starting with a letter (2 to 63 characters). It cannot be changed once published."));
            else if (CoreFields.IsCore(f.Key)) m.Add(Err(p + ".key", "core_key", $"'{f.Key}' is the key of a core contract field and cannot be used for a custom field."));
            else if (!keys.Add(f.Key)) m.Add(Err(p + ".key", "duplicate_key", $"The field key '{f.Key}' is used more than once."));
            if (string.IsNullOrWhiteSpace(f.Label) || f.Label.Length > 100) m.Add(Err(p + ".label", "label", "A field needs a label of up to 100 characters."));
            if (f.Description.Length > 500 || f.HelpText.Length > 500 || f.Placeholder.Length > 100) m.Add(Err(p, "text_too_long", "Description and help text are limited to 500 characters and the placeholder to 100."));
            if (f.Order is < 0 or > 100000) m.Add(Err(p + ".order", "order", "The display order must be between 0 and 100000."));
            if (!sectionKeys.Contains(f.Section)) m.Add(Err(p + ".section", "unknown_section", $"The section '{f.Section}' is not defined."));
            if (f.Status is not (FieldStatuses.Active or FieldStatuses.Retired)) m.Add(Err(p + ".status", "status", "The status must be Active or Retired."));
            if (f.EffectiveFrom is { } from && f.EffectiveTo is { } to && to < from) m.Add(Err(p + ".effectiveTo", "dates", "The field cannot end before it starts."));
            if (!ctx.Classifications.Contains(f.Classification, StringComparer.OrdinalIgnoreCase)) m.Add(Err(p + ".classification", "classification", $"The classification '{f.Classification}' is not one of this organisation's levels."));
            foreach (var role in f.VisibleToRoles.Concat(f.EditableByRoles).Distinct()) if (!ctx.KnownRoles.Contains(role)) m.Add(Err(p, "unknown_role", $"The role '{role}' is not recognised."));
            if (ctx.KnownStates is { } states)
                foreach (var st in f.VisibleInStates.Concat(f.EditableInStates).Distinct()) if (!states.Contains(st)) m.Add(Warn(p, "unknown_state", $"'{st}' is not a state of the current workflow."));
            if (!FieldTypes.IsKnown(f.Type)) { m.Add(Err(p + ".type", "type", $"The type '{f.Type}' is not supported.")); continue; }
            ValidateType(set, f, p, m);
        }

        // Calculated fields refer to real, plain fields.
        for (var i = 0; i < set.Fields.Count; i++)
            if (set.Fields[i].IsCalculated) ValidateCalculation(set, set.Fields[i], $"$.fields[{i}]", m);

        if (ctx.Previous is { } previous) CompareWithPrevious(set, previous, ctx.Usage ?? FieldUsage.None, m);
        return m;
    }

    private static void CheckMembers(IReadOnlyList<FieldChoice> members, string path, int max, List<ValidationMessage> m)
    {
        if (members.Count > max) m.Add(Err(path, "too_many", $"At most {max} choices are allowed here."));
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var j = 0; j < members.Count; j++)
        {
            var c = members[j];
            if (!CodePattern().IsMatch(c.Code)) m.Add(Err($"{path}[{j}].code", "choice_code", "A choice code starts with a letter or digit and uses letters, digits, spaces and _ . / & - (up to 50 characters)."));
            else if (!codes.Add(c.Code)) m.Add(Err($"{path}[{j}].code", "duplicate_choice", $"The choice code '{c.Code}' is used twice."));
            if (string.IsNullOrWhiteSpace(c.Label) || c.Label.Length > 100) m.Add(Err($"{path}[{j}].label", "choice_label", "A choice needs a label of up to 100 characters."));
        }
    }

    private static void ValidateType(CustomFieldSet set, FieldDefinition f, string p, List<ValidationMessage> m)
    {
        var v = f.Validation;
        void Unsupported(string what) => m.Add(Err(p + ".validation", "validation_unsupported", $"{what} cannot be used with the type {FieldTypes.Label(f.Type)}."));
        bool text = f.Type is FieldTypes.Text or FieldTypes.LongText, numeric = FieldTypes.IsNumeric(f.Type), date = f.Type is FieldTypes.Date or FieldTypes.DateTime;

        if ((v.MinLength is not null || v.MaxLength is not null) && !text) Unsupported("A length limit");
        if (v.Pattern is not null && f.Type != FieldTypes.Text) Unsupported("A pattern");
        if ((v.Min is not null || v.Max is not null) && !numeric) Unsupported("A minimum or maximum");
        if (v.Decimals is not null && !(numeric && f.Type != FieldTypes.Integer)) Unsupported("A decimal-places limit");
        if ((v.MinDate is not null || v.MaxDate is not null) && !date) Unsupported("A date limit");
        if ((v.MinItems is not null || v.MaxItems is not null) && f.Type != FieldTypes.MultiChoice) Unsupported("An item-count limit");
        if (v.Currency is not null && f.Type != FieldTypes.Currency) Unsupported("A currency");
        if (v.PatternMessage is not null && v.Pattern is null) m.Add(Err(p + ".validation", "pattern_message", "A pattern message needs a pattern."));

        var maxLen = f.Type == FieldTypes.Text ? ValueCodec.MaxText : ValueCodec.MaxLongText;
        if (text && (v.MaxLength is < 1 || v.MaxLength > maxLen)) m.Add(Err(p + ".validation.maxLength", "length", $"The maximum length must be between 1 and {maxLen}."));
        if (text && v.MinLength is < 0) m.Add(Err(p + ".validation.minLength", "length", "The minimum length cannot be negative."));
        if (text && v.MinLength > (v.MaxLength ?? maxLen)) m.Add(Err(p + ".validation", "length", "The minimum length is above the maximum."));
        if (v.Pattern is not null && f.Type == FieldTypes.Text && !ValueCodec.IsAcceptablePattern(v.Pattern, out var why)) m.Add(Err(p + ".validation.pattern", "pattern", why!));
        if (v.Min > v.Max) m.Add(Err(p + ".validation", "range", "The minimum is above the maximum."));
        if (v.Decimals is < 0 or > 8) m.Add(Err(p + ".validation.decimals", "decimals", "Decimal places must be between 0 and 8."));
        if (f.Type == FieldTypes.Percentage && (v.Min < 0 || v.Max > 100)) m.Add(Err(p + ".validation", "range", "A percentage stays between 0 and 100."));
        if (v.MinDate > v.MaxDate) m.Add(Err(p + ".validation", "range", "The earliest date is after the latest."));
        if (v.MinItems is < 0 || v.MaxItems is < 1 || v.MinItems > v.MaxItems) m.Add(Err(p + ".validation", "items", "Check the item counts: the minimum cannot be negative or above the maximum."));
        if (v.Currency is not null && !Regex.IsMatch(v.Currency, "^[A-Z]{3}$", RegexOptions.NonBacktracking)) m.Add(Err(p + ".validation.currency", "currency", "A currency is a three-letter ISO 4217 code."));

        // Choices and lookups.
        if (f.Type is FieldTypes.SingleChoice or FieldTypes.MultiChoice)
        {
            if (f.Choices.Count == 0) m.Add(Err(p + ".choices", "choices_required", "A choice field needs at least one choice."));
            CheckMembers(f.Choices, p + ".choices", MaxChoices, m);
            if (f.Lookup is not null) m.Add(Err(p + ".lookup", "lookup_not_allowed", "A choice field has its own choices; use the Lookup type for a shared list."));
        }
        else
        {
            if (f.Choices.Count > 0) m.Add(Err(p + ".choices", "choices_not_allowed", $"Choices cannot be used with the type {FieldTypes.Label(f.Type)}."));
            if (f.Type != FieldTypes.Lookup && f.Lookup is not null) m.Add(Err(p + ".lookup", "lookup_not_allowed", "Only the Lookup type names a list."));
        }
        if (f.Type == FieldTypes.Lookup && (f.Lookup is null || set.FindList(f.Lookup) is null)) m.Add(Err(p + ".lookup", "lookup_missing", $"The lookup list '{f.Lookup}' is not defined."));

        if (f.Type == FieldTypes.Calculated)
        {
            if (f.Calculated is null) m.Add(Err(p + ".calculated", "calculation_required", "A calculated field needs a calculation."));
            if (f.Required) m.Add(Err(p + ".required", "calculated_required", "A calculated field cannot be required: it is worked out, not entered."));
            if (f.Searchable || f.Filterable || f.Sortable) m.Add(Err(p, "calculated_search", "A calculated field is display-only and cannot be searched, filtered or sorted."));
            if (f.Default.Count > 0) m.Add(Err(p + ".default", "calculated_default", "A calculated field has no default."));
            if (f.EditableByRoles.Count > 0 || f.EditableInStates.Count > 0) m.Add(Err(p, "calculated_editable", "A calculated field cannot be edited, so it has no edit rules."));
        }
        else
        {
            if (f.Calculated is not null) m.Add(Err(p + ".calculated", "calculation_not_allowed", "Only a calculated field has a calculation."));
            if (f.Type is FieldTypes.LongText or FieldTypes.Url && (f.Filterable || f.Sortable)) m.Add(Err(p, "not_filterable", $"A {FieldTypes.Label(f.Type).ToLowerInvariant()} field can be searched but not filtered or sorted."));
            if (f.Type == FieldTypes.MultiChoice && f.Sortable) m.Add(Err(p, "not_sortable", "A multiple-choice field cannot be sorted."));
            if (f.Searchable && f.Type is not (FieldTypes.Text or FieldTypes.LongText or FieldTypes.Email or FieldTypes.Url or FieldTypes.SingleChoice or FieldTypes.MultiChoice or FieldTypes.Lookup))
                m.Add(Err(p, "not_searchable", $"A {FieldTypes.Label(f.Type).ToLowerInvariant()} field is filtered, not text-searched. Turn on 'filterable' instead."));
        }

        if (f.UsableInApprovals && f.Type is FieldTypes.MultiChoice or FieldTypes.LongText or FieldTypes.FileReference or FieldTypes.Url or FieldTypes.Calculated)
            m.Add(Err(p + ".usableInApprovals", "approval_type", $"A {FieldTypes.Label(f.Type).ToLowerInvariant()} field cannot feed an approval matrix: its value is not a single comparable fact."));

        // The default has to be a value the field would accept today.
        if (f.Default.Count > 0 && f.Type != FieldTypes.Calculated)
        {
            var parsed = ValueCodec.Parse(f, set, f.Default);
            if (!parsed.IsValid) m.Add(Err(p + ".default", "default_invalid", "The default value is not valid for this field: " + parsed.Errors[0]));
            else if (f.Type is FieldTypes.Person or FieldTypes.FileReference) m.Add(Err(p + ".default", "default_invalid", "A person or file reference has no default."));
        }
    }

    private static void ValidateCalculation(CustomFieldSet set, FieldDefinition f, string p, List<ValidationMessage> m)
    {
        var c = f.Calculated; if (c is null) return;
        if (!CalculationKinds.All.Contains(c.Kind)) { m.Add(Err(p + ".calculated.kind", "calculation_kind", $"The calculation '{c.Kind}' is not supported. Use one of: {string.Join(", ", CalculationKinds.All)}.")); return; }
        if (c.Decimals is < 0 or > 6) m.Add(Err(p + ".calculated.decimals", "decimals", "Decimal places must be between 0 and 6."));
        (string Type, bool Ok) Operand(string key)
        {
            if (set.Find(key) is { } cf) return (cf.Type, cf.Type != FieldTypes.Calculated && !cf.IsRetired);
            if (CoreFields.Find(key) is { } core) return (core.Type, true);
            return ("", false);
        }
        foreach (var o in c.Operands)
        {
            var (type, ok) = Operand(o);
            if (type == "") m.Add(Err(p + ".calculated.operands", "operand_unknown", $"'{o}' is not a field."));
            else if (!ok) m.Add(Err(p + ".calculated.operands", "operand_invalid", $"'{o}' is calculated or retired and cannot be used in a calculation."));
        }
        switch (c.Kind)
        {
            case CalculationKinds.Template:
                if (string.IsNullOrWhiteSpace(c.Template) || c.Template.Length > 300) { m.Add(Err(p + ".calculated.template", "template", "A template is up to 300 characters of text with {field_key} placeholders.")); break; }
                var stripped = Placeholder().Replace(c.Template, "");
                if (stripped.Contains('{') || stripped.Contains('}')) m.Add(Err(p + ".calculated.template", "template", "Use { and } only around a field key."));
                foreach (Match ph in Placeholder().Matches(c.Template)) if (Operand(ph.Groups[1].Value).Type == "") m.Add(Err(p + ".calculated.template", "operand_unknown", $"'{ph.Groups[1].Value}' is not a field."));
                break;
            case CalculationKinds.DaysBetween:
                if (c.Operands.Count != 2 || c.Operands.Any(o => Operand(o).Type is not (FieldTypes.Date or FieldTypes.DateTime))) m.Add(Err(p + ".calculated.operands", "operands", "Days between needs exactly two date fields."));
                break;
            case CalculationKinds.Difference or CalculationKinds.Ratio:
                if (c.Operands.Count != 2 || c.Operands.Any(o => !FieldTypes.IsNumeric(Operand(o).Type))) m.Add(Err(p + ".calculated.operands", "operands", $"{c.Kind} needs exactly two number fields."));
                break;
            default:
                if (c.Operands.Count is < 2 or > 10 || c.Operands.Any(o => !FieldTypes.IsNumeric(Operand(o).Type))) m.Add(Err(p + ".calculated.operands", "operands", $"{c.Kind} needs two to ten number fields."));
                break;
        }
    }

    /// <summary>Rules that depend on what is already published: a field's key and type never change, nothing in use is removed, and a change that affects existing data is reported.</summary>
    private static void CompareWithPrevious(CustomFieldSet now, CustomFieldSet before, FieldUsage usage, List<ValidationMessage> m)
    {
        foreach (var old in before.Fields)
        {
            var current = now.Find(old.Key);
            var path = $"$.fields[key={old.Key}]";
            if (current is null)
            {
                if (usage.Of(old.Key) > 0) m.Add(Err(path, "field_in_use", $"The field '{old.Key}' holds values on {usage.Of(old.Key)} contract(s) and cannot be removed. Retire it instead; its values are kept."));
                else m.Add(Warn(path, "field_removed", $"The field '{old.Key}' is removed. It has no values, so nothing is lost."));
                continue;
            }
            if (!string.Equals(old.Type, current.Type, StringComparison.Ordinal) && usage.Of(old.Key) > 0) m.Add(Err(path, "type_in_use", $"The type of '{old.Key}' cannot change while contracts hold values for it. Retire it and add a new field."));
            if (FieldTypes.UsesList(old.Type) && old.Type == current.Type)
                foreach (var member in before.ChoicesOf(old))
                {
                    var still = now.ChoicesOf(current).Any(c => string.Equals(c.Code, member.Code, StringComparison.OrdinalIgnoreCase));
                    var used = usage.Of(old.Key, member.Code);
                    if (!still && used > 0) m.Add(Err(path, "member_in_use", $"The choice '{member.Code}' of '{old.Key}' is used by {used} contract(s) and cannot be removed. Mark it inactive instead."));
                }
            if (!old.Required && current.Required && usage.Contracts > 0)
            {
                var missing = Math.Max(0, usage.Contracts - usage.Of(old.Key));
                if (missing > 0) m.Add(Warn(path, "required_backfill", $"'{old.Key}' becomes required. {missing} existing contract(s) have no value; they are not blocked, but must be completed the next time they are edited."));
            }
            if (!old.IsRetired && current.IsRetired) m.Add(Warn(path, "retired", $"'{old.Key}' is retired: it is hidden from new entry and kept on {usage.Of(old.Key)} contract(s)."));
        }
        // A list used by a field that goes on to lose members is checked above per field; a list that disappears while a field still needs it is caught by lookup_missing.
    }
}
