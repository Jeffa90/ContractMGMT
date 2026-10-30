using System.Globalization;
using System.Text.RegularExpressions;
using Ecp.Metadata;
using Ecp.Host.Pages.Admin.Studio;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>One option of a choice field as the editor posts it. A new option has no stored code yet; the code is generated from its label when the field is saved and never changes afterwards.</summary>
public sealed class OptionRow
{
    public string? Code { get; set; }
    public string? Label { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>A field a calculation can use, shown by its label (the key is never shown).</summary>
public sealed record OperandChoice(string Key, string Label, string Type);

/// <summary>What the person needs the form to know in order to turn it into a definition without asking for any machine name.</summary>
public sealed record FormContext(CustomFieldSet Working, IReadOnlyList<OperandChoice> Operands, string DefaultClassification);

/// <summary>
/// The guided Add/Edit Field form as it is posted. Only the settings that belong to the chosen type are read; the rest are ignored even if a stale value arrives. The form is turned into a
/// <see cref="FieldDefinition"/> through the same parser the older Studio form uses, so the engine sees exactly what it always has, and the full set of rules is applied again when the draft is validated.
/// </summary>
public sealed class FieldForm
{
    public string? Key { get; set; }
    public string? CustomKey { get; set; }
    [ValidateNever] public string Status { get; set; } = FieldStatuses.Active;
    public int Order { get; set; }

    // 1. Basic information
    [ValidateNever] public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? HelpText { get; set; }
    public string? Placeholder { get; set; }

    // 2. Type and its settings
    [ValidateNever] public string Type { get; set; } = FieldTypes.Text;
    public string? MinLength { get; set; }
    public string? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public string? PatternMessage { get; set; }
    public string? Min { get; set; }
    public string? Max { get; set; }
    public string? Decimals { get; set; }
    public string? Currency { get; set; }
    public string? MinDate { get; set; }
    public string? MaxDate { get; set; }
    public string? MinItems { get; set; }
    public string? MaxItems { get; set; }
    public string? DefaultValue { get; set; }
    public string? Lookup { get; set; }
    public string? CalcKind { get; set; }
    public string? CalcFirst { get; set; }
    public string? CalcSecond { get; set; }
    public List<string> CalcMore { get; set; } = [];
    public string? CalcTemplate { get; set; }
    public string? CalcDecimals { get; set; }

    // 3. Choices
    public List<OptionRow> Options { get; set; } = [];

    // 4. Placement
    [ValidateNever] public string Section { get; set; } = "general";
    public string? NewSectionName { get; set; }
    [ValidateNever] public string Position { get; set; } = "end";
    public string? AfterKey { get; set; }

    // 5. Rules
    public bool Required { get; set; }
    public bool Searchable { get; set; }
    public bool Filterable { get; set; }
    public bool Sortable { get; set; }
    public bool Reportable { get; set; }
    public bool UsableInApprovals { get; set; }
    [ValidateNever] public string Classification { get; set; } = "";
    public List<string> VisibleToRoles { get; set; } = [];
    public List<string> EditableByRoles { get; set; } = [];
    public List<string> VisibleInStates { get; set; } = [];
    public List<string> EditableInStates { get; set; } = [];
    public List<string> ContractTypes { get; set; } = [];
    public string? EffectiveFrom { get; set; }
    public string? EffectiveTo { get; set; }

    public bool IsNew => string.IsNullOrEmpty(Key);

    /// <summary>A form for a new field of the given type, with the standard rules a new field of that type starts with.</summary>
    public static FieldForm New(string type, string defaultClassification)
    {
        var (s, f, o, r) = FieldGuide.Defaults(type);
        return new FieldForm { Type = type, Searchable = s, Filterable = f, Sortable = o, Reportable = r, Classification = defaultClassification, Position = "end" };
    }

    public static FieldForm From(FieldDefinition d, CustomFieldSet set)
    {
        var c = d.Calculated;
        var operands = c?.Operands ?? [];
        return new FieldForm
        {
            Key = d.Key, Status = d.Status, Order = d.Order, Name = d.Label, Description = d.Description, HelpText = d.HelpText, Placeholder = d.Placeholder, Type = d.Type,
            MinLength = d.Validation.MinLength?.ToString(CultureInfo.InvariantCulture), MaxLength = d.Validation.MaxLength?.ToString(CultureInfo.InvariantCulture), Pattern = d.Validation.Pattern, PatternMessage = d.Validation.PatternMessage,
            Min = d.Validation.Min?.ToString(CultureInfo.InvariantCulture), Max = d.Validation.Max?.ToString(CultureInfo.InvariantCulture), Decimals = d.Validation.Decimals?.ToString(CultureInfo.InvariantCulture), Currency = d.Validation.Currency,
            MinDate = d.Validation.MinDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), MaxDate = d.Validation.MaxDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            MinItems = d.Validation.MinItems?.ToString(CultureInfo.InvariantCulture), MaxItems = d.Validation.MaxItems?.ToString(CultureInfo.InvariantCulture),
            DefaultValue = d.Type == FieldTypes.SingleChoice ? set.ChoicesOf(d).FirstOrDefault(x => x.Code == d.Default.FirstOrDefault())?.Label : d.Default.FirstOrDefault(),
            Lookup = d.Lookup, CalcKind = c?.Kind, CalcFirst = operands.Count > 0 ? operands[0] : null, CalcSecond = operands.Count > 1 ? operands[1] : null, CalcMore = operands.Skip(2).ToList(),
            CalcTemplate = c?.Template, CalcDecimals = c?.Decimals?.ToString(CultureInfo.InvariantCulture),
            Options = d.Choices.Select(x => new OptionRow { Code = x.Code, Label = x.Label, Active = x.Active }).ToList(),
            Section = d.Section, Position = "keep", Required = d.Required, Searchable = d.Searchable, Filterable = d.Filterable, Sortable = d.Sortable, Reportable = d.Reportable, UsableInApprovals = d.UsableInApprovals,
            Classification = d.Classification, VisibleToRoles = [.. d.VisibleToRoles], EditableByRoles = [.. d.EditableByRoles], VisibleInStates = [.. d.VisibleInStates], EditableInStates = [.. d.EditableInStates],
            ContractTypes = [.. d.ContractTypes], EffectiveFrom = d.EffectiveFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EffectiveTo = d.EffectiveTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>The options a person typed, with blank rows dropped and every new option given its stored code.</summary>
    public List<FieldChoice> FinalOptions()
    {
        var kept = Options.Where(o => !string.IsNullOrWhiteSpace(o.Label)).ToList();
        var codes = kept.Where(o => !string.IsNullOrWhiteSpace(o.Code)).Select(o => o.Code!.Trim()).ToList();
        var result = new List<FieldChoice>();
        foreach (var o in kept)
        {
            var code = string.IsNullOrWhiteSpace(o.Code) ? FieldGuide.CodeFromLabel(o.Label!, codes) : o.Code.Trim();
            if (string.IsNullOrWhiteSpace(o.Code)) codes.Add(code);
            result.Add(new FieldChoice(code, o.Label!.Trim(), o.Active));
        }
        return result;
    }

    /// <summary>
    /// Checks what was typed in plain language and builds the definition. Anything wrong is added to <paramref name="state"/> against the form's own field names; null is returned when the form cannot be turned into a
    /// definition. <paramref name="sectionKey"/> is the key of the (possibly new) section the field goes into.
    /// </summary>
    public FieldDefinition? Build(ModelStateDictionary state, FormContext context, string key, string sectionKey, FieldDefinition? existing)
    {
        var before = state.ErrorCount;
        var label = (Name ?? "").Trim();
        if (label.Length == 0) state.AddModelError("Form.Name", "Give the field a name.");
        else if (label.Length > 100) state.AddModelError("Form.Name", "The name is too long. Use 100 characters or fewer.");
        else if (context.Working.Fields.Any(f => !string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase) && string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase)))
            state.AddModelError("Form.Name", $"There is already a field called \"{label}\". Choose a different name, or edit that field instead.");
        if ((Description ?? "").Trim().Length > 300) state.AddModelError("Form.Description", "The description is too long. Use 300 characters or fewer.");
        if ((HelpText ?? "").Trim().Length > 300) state.AddModelError("Form.HelpText", "The help text is too long. Use 300 characters or fewer.");

        var type = FieldTypes.IsKnown(Type) ? Type : FieldTypes.Text;
        var options = FinalOptions();
        if (FieldGuide.HasOptions(type))
        {
            if (options.Count == 0) state.AddModelError("Form.Options", "Add at least one option for people to choose from.");
            else if (options.All(o => !o.Active)) state.AddModelError("Form.Options", "At least one option has to be offered. Switch an option back on, or add another.");
            foreach (var dup in options.GroupBy(o => o.Label.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                state.AddModelError("Form.Options", $"The option \"{dup.Key}\" appears more than once. Each option needs its own wording.");
            if (options.Any(o => o.Label.Length > 100)) state.AddModelError("Form.Options", "An option is too long. Use 100 characters or fewer.");
        }
        if (type == FieldTypes.Lookup && string.IsNullOrWhiteSpace(Lookup)) state.AddModelError("Form.Lookup", "Choose the shared list this field picks from.");

        CalculatedSpec? calc = null;
        if (type == FieldTypes.Calculated)
        {
            var kind = CalcKind?.Trim() ?? "";
            if (!CalculationKinds.All.Contains(kind)) state.AddModelError("Form.CalcKind", "Choose what to work out.");
            else if (kind == CalculationKinds.Template)
            {
                var map = context.Operands.GroupBy(o => o.Label, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
                var unknown = new List<string>();
                var text = Regex.Replace(CalcTemplate ?? "", @"\{([^{}]*)\}", m => map.TryGetValue(m.Groups[1].Value.Trim(), out var k) ? "{" + k + "}" : Unknown(m.Groups[1].Value, unknown), RegexOptions.NonBacktracking);
                if (string.IsNullOrWhiteSpace(CalcTemplate)) state.AddModelError("Form.CalcTemplate", "Write the text to show, putting a field's name in { } where its value should appear.");
                foreach (var u in unknown) state.AddModelError("Form.CalcTemplate", $"\"{u}\" is not the name of a field you can use here.");
                calc = new CalculatedSpec(kind, [], text, Int(CalcDecimals, "Form.CalcDecimals", "Decimal places of the result", state));
            }
            else
            {
                var operands = new List<string>();
                if (!string.IsNullOrWhiteSpace(CalcFirst)) operands.Add(CalcFirst);
                if (!string.IsNullOrWhiteSpace(CalcSecond)) operands.Add(CalcSecond);
                if (kind is CalculationKinds.Sum or CalculationKinds.Product) operands.AddRange(CalcMore.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (operands.Count < 2) state.AddModelError("Form.CalcFirst", "Choose the fields to work from. This needs at least two.");
                else if (operands.Distinct(StringComparer.OrdinalIgnoreCase).Count() != operands.Count) state.AddModelError("Form.CalcFirst", "Choose different fields; the same field cannot be used twice.");
                calc = new CalculatedSpec(kind, operands.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), null, Int(CalcDecimals, "Form.CalcDecimals", "Decimal places of the result", state));
            }
        }

        string? Pick(bool applies, string? v) => applies ? v : null;
        var edit = new FieldEdit
        {
            Key = key, Label = label, Description = Description, HelpText = HelpText, Placeholder = Placeholder, Type = type, Section = sectionKey, Order = Order, Required = Required && FieldGuide.CanRequire(type),
            MinLength = Pick(FieldGuide.HasLength(type), MinLength), MaxLength = Pick(FieldGuide.HasLength(type), MaxLength), Pattern = Pick(FieldGuide.HasPattern(type), Pattern), PatternMessage = Pick(FieldGuide.HasPattern(type), PatternMessage),
            Min = Pick(FieldGuide.HasRange(type), Min), Max = Pick(FieldGuide.HasRange(type), Max), Decimals = Pick(FieldGuide.HasDecimals(type), Decimals), Currency = Pick(FieldGuide.HasCurrency(type), Currency),
            MinDate = Pick(FieldGuide.HasDateRange(type), MinDate), MaxDate = Pick(FieldGuide.HasDateRange(type), MaxDate), MinItems = Pick(FieldGuide.HasItemCount(type), MinItems), MaxItems = Pick(FieldGuide.HasItemCount(type), MaxItems),
            Lookup = Pick(FieldGuide.HasList(type), Lookup),
            Searchable = Searchable && FieldGuide.CanSearch(type), Filterable = Filterable && FieldGuide.CanFilter(type), Sortable = Sortable && FieldGuide.CanSort(type), Reportable = Reportable,
            UsableInApprovals = UsableInApprovals && FieldGuide.CanUseInApprovals(type), Classification = string.IsNullOrWhiteSpace(Classification) ? context.DefaultClassification : Classification,
            VisibleToRoles = type == FieldTypes.Calculated ? VisibleToRoles : VisibleToRoles, EditableByRoles = type == FieldTypes.Calculated ? [] : EditableByRoles,
            VisibleInStates = VisibleInStates, EditableInStates = type == FieldTypes.Calculated ? [] : EditableInStates, ContractTypes = ContractTypes, Status = Status, EffectiveFrom = EffectiveFrom, EffectiveTo = EffectiveTo
        };
        // A pre-filled value: a choice is remembered by its stored code, a Yes/No by true/false.
        if (type == FieldTypes.SingleChoice && !string.IsNullOrWhiteSpace(DefaultValue))
        {
            var chosen = options.FirstOrDefault(o => string.Equals(o.Label, DefaultValue.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(o.Code, DefaultValue.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosen is null) state.AddModelError("Form.DefaultValue", "The pre-selected option is not one of the options.");
            else edit.Default = chosen.Code;
        }
        else if (FieldGuide.HasPrefill(type) || FieldGuide.HasYesNoPrefill(type)) edit.Default = DefaultValue;

        var temp = new ModelStateDictionary();
        var def = edit.Build(temp);
        foreach (var (k, entry) in temp)
            foreach (var err in entry.Errors) state.AddModelError(k.Replace("Edit.", "Form.", StringComparison.Ordinal), err.ErrorMessage);
        if (def is null || state.ErrorCount > before) return null;
        return def with { Choices = FieldGuide.HasOptions(type) ? options : [], Calculated = type == FieldTypes.Calculated ? calc : null };
    }

    private static string Unknown(string text, List<string> unknown) { unknown.Add(text.Trim()); return "{" + text + "}"; }

    private static int? Int(string? raw, string name, string label, ModelStateDictionary state)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        state.AddModelError(name, $"{label} must be a whole number.");
        return null;
    }
}
