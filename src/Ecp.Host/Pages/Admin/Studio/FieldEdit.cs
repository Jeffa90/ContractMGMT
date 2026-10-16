using System.Globalization;
using Ecp.Metadata;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Ecp.Host.Pages.Admin.Studio;

/// <summary>A field definition as the Studio form posts it. All values are typed and checked here and again by the publish-time validator; nothing posted is ever treated as code or as an expression.</summary>
public sealed class FieldEdit
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public string? HelpText { get; set; }
    public string? Placeholder { get; set; }
    public string Type { get; set; } = FieldTypes.Text;
    public string Section { get; set; } = "general";
    public int Order { get; set; }
    public bool Required { get; set; }
    public string? Default { get; set; }
    public string? MinLength { get; set; }
    public string? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public string? PatternMessage { get; set; }
    public string? Min { get; set; }
    public string? Max { get; set; }
    public string? Decimals { get; set; }
    public string? MinDate { get; set; }
    public string? MaxDate { get; set; }
    public string? MinItems { get; set; }
    public string? MaxItems { get; set; }
    public string? Currency { get; set; }
    /// <summary>One choice per line: <c>CODE | Label | inactive</c> (the third part is optional).</summary>
    public string? Choices { get; set; }
    public string? Lookup { get; set; }
    public string? CalcKind { get; set; }
    public string? CalcOperands { get; set; }
    public string? CalcTemplate { get; set; }
    public string? CalcDecimals { get; set; }
    public bool Searchable { get; set; }
    public bool Filterable { get; set; }
    public bool Sortable { get; set; }
    public bool Reportable { get; set; }
    public bool UsableInApprovals { get; set; }
    public string Classification { get; set; } = "Internal";
    public List<string> VisibleToRoles { get; set; } = [];
    public List<string> EditableByRoles { get; set; } = [];
    public List<string> VisibleInStates { get; set; } = [];
    public List<string> EditableInStates { get; set; } = [];
    public List<string> ContractTypes { get; set; } = [];
    public string Status { get; set; } = FieldStatuses.Active;
    public string? EffectiveFrom { get; set; }
    public string? EffectiveTo { get; set; }

    private static List<string> Items(IEnumerable<string>? raw) => (raw ?? []).SelectMany(x => (x ?? "").Split(',', ';', '\n')).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static FieldEdit From(FieldDefinition d) => new()
    {
        Key = d.Key, Label = d.Label, Description = d.Description, HelpText = d.HelpText, Placeholder = d.Placeholder, Type = d.Type, Section = d.Section, Order = d.Order, Required = d.Required,
        Default = string.Join("\n", d.Default), MinLength = d.Validation.MinLength?.ToString(CultureInfo.InvariantCulture), MaxLength = d.Validation.MaxLength?.ToString(CultureInfo.InvariantCulture),
        Pattern = d.Validation.Pattern, PatternMessage = d.Validation.PatternMessage, Min = d.Validation.Min?.ToString(CultureInfo.InvariantCulture), Max = d.Validation.Max?.ToString(CultureInfo.InvariantCulture),
        Decimals = d.Validation.Decimals?.ToString(CultureInfo.InvariantCulture), MinDate = d.Validation.MinDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        MaxDate = d.Validation.MaxDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), MinItems = d.Validation.MinItems?.ToString(CultureInfo.InvariantCulture),
        MaxItems = d.Validation.MaxItems?.ToString(CultureInfo.InvariantCulture), Currency = d.Validation.Currency,
        Choices = string.Join("\n", d.Choices.Select(c => c.Active ? $"{c.Code} | {c.Label}" : $"{c.Code} | {c.Label} | inactive")), Lookup = d.Lookup,
        CalcKind = d.Calculated?.Kind, CalcOperands = d.Calculated is null ? null : string.Join(", ", d.Calculated.Operands), CalcTemplate = d.Calculated?.Template, CalcDecimals = d.Calculated?.Decimals?.ToString(CultureInfo.InvariantCulture),
        Searchable = d.Searchable, Filterable = d.Filterable, Sortable = d.Sortable, Reportable = d.Reportable, UsableInApprovals = d.UsableInApprovals, Classification = d.Classification,
        VisibleToRoles = [.. d.VisibleToRoles], EditableByRoles = [.. d.EditableByRoles], VisibleInStates = [.. d.VisibleInStates], EditableInStates = [.. d.EditableInStates], ContractTypes = [.. d.ContractTypes],
        Status = d.Status, EffectiveFrom = d.EffectiveFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EffectiveTo = d.EffectiveTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    /// <summary>Builds the definition. Anything that does not parse is reported against its field on the form; the full set of rules is applied when the draft is validated.</summary>
    public FieldDefinition? Build(ModelStateDictionary state)
    {
        var ok = true;
        int? Int(string? raw, string name, string label)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            state.AddModelError("Edit." + name, $"{label} must be a whole number."); ok = false; return null;
        }
        decimal? Dec(string? raw, string name, string label)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return v;
            state.AddModelError("Edit." + name, $"{label} must be a number."); ok = false; return null;
        }
        DateOnly? Date(string? raw, string name, string label)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)) return v;
            state.AddModelError("Edit." + name, $"{label} must be a date (yyyy-mm-dd)."); ok = false; return null;
        }
        var choices = new List<FieldChoice>();
        foreach (var line in (Choices ?? "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0) continue;
            var parts = t.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) { state.AddModelError("Edit.Choices", $"Each choice is written as CODE | Label (or CODE | Label | inactive). Check: {t}"); ok = false; continue; }
            choices.Add(new FieldChoice(parts[0], parts[1], !(parts.Length == 3 && parts[2].Equals("inactive", StringComparison.OrdinalIgnoreCase))));
        }
        var validation = new FieldValidation(Int(MinLength, nameof(MinLength), "Minimum length"), Int(MaxLength, nameof(MaxLength), "Maximum length"), string.IsNullOrWhiteSpace(Pattern) ? null : Pattern.Trim(),
            string.IsNullOrWhiteSpace(PatternMessage) ? null : PatternMessage.Trim(), Dec(Min, nameof(Min), "Minimum"), Dec(Max, nameof(Max), "Maximum"), Int(Decimals, nameof(Decimals), "Decimal places"),
            Date(MinDate, nameof(MinDate), "Earliest date"), Date(MaxDate, nameof(MaxDate), "Latest date"), Int(MinItems, nameof(MinItems), "Minimum selections"), Int(MaxItems, nameof(MaxItems), "Maximum selections"),
            string.IsNullOrWhiteSpace(Currency) ? null : Currency.Trim().ToUpperInvariant());
        CalculatedSpec? calc = Type == FieldTypes.Calculated
            ? new CalculatedSpec(CalcKind?.Trim() ?? "", Items([CalcOperands ?? ""]), string.IsNullOrWhiteSpace(CalcTemplate) ? null : CalcTemplate, Int(CalcDecimals, nameof(CalcDecimals), "Decimal places of the result")) : null;
        var from = Date(EffectiveFrom, nameof(EffectiveFrom), "Effective from"); var to = Date(EffectiveTo, nameof(EffectiveTo), "Effective to");
        if (!ok) return null;
        var defaults = (Default ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        return new FieldDefinition(Key.Trim(), Label.Trim(), (Description ?? "").Trim(), (HelpText ?? "").Trim(), (Placeholder ?? "").Trim(), Type, string.IsNullOrWhiteSpace(Section) ? "general" : Section.Trim().ToLowerInvariant(),
            Order, Required, defaults, validation == FieldValidation.None ? FieldValidation.None : validation, Type is FieldTypes.SingleChoice or FieldTypes.MultiChoice ? choices : [],
            Type == FieldTypes.Lookup && !string.IsNullOrWhiteSpace(Lookup) ? Lookup.Trim() : null, calc, Searchable, Filterable, Sortable, Reportable, Classification, Items(VisibleToRoles), Items(EditableByRoles),
            Items(VisibleInStates), Items(EditableInStates), Items(ContractTypes), Status, from, to, UsableInApprovals);
    }
}
