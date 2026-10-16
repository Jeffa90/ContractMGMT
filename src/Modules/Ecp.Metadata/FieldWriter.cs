using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecp.Metadata;

/// <summary>
/// Writes a custom field set back to its document form. The Studio edits the typed model and writes it here, so what is saved always passes through the same strict reader that validates and publishes it;
/// nothing a person types is ever placed into the document as text of their own.
/// </summary>
public static class FieldWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string ToJson(CustomFieldSet set) => JsonSerializer.Serialize(new
    {
        schemaVersion = set.SchemaVersion == 0 ? 1 : set.SchemaVersion,
        sections = set.Sections.Select(s => new { key = s.Key, label = s.Label, order = s.Order }),
        lookups = set.Lookups.Select(l => new { name = l.Name, label = l.Label, members = l.Members.Select(Choice) }),
        fields = set.Fields.Select(Field)
    }, Options);

    private static object Choice(FieldChoice c) => new { code = c.Code, label = c.Label, active = c.Active };

    private static object? Default(FieldDefinition f) => f.Default.Count switch { 0 => null, 1 when f.Type != FieldTypes.MultiChoice => f.Default[0], _ => f.Default };

    private static object Field(FieldDefinition f) => new
    {
        key = f.Key, label = f.Label, description = f.Description, helpText = f.HelpText, placeholder = f.Placeholder, type = f.Type, section = f.Section, order = f.Order, required = f.Required,
        @default = Default(f),
        validation = f.Validation == FieldValidation.None ? null : new
        {
            minLength = f.Validation.MinLength, maxLength = f.Validation.MaxLength, pattern = f.Validation.Pattern, patternMessage = f.Validation.PatternMessage, min = f.Validation.Min, max = f.Validation.Max,
            decimals = f.Validation.Decimals, minDate = f.Validation.MinDate, maxDate = f.Validation.MaxDate, minItems = f.Validation.MinItems, maxItems = f.Validation.MaxItems, currency = f.Validation.Currency
        },
        choices = f.Choices.Count == 0 ? null : f.Choices.Select(Choice),
        lookup = f.Lookup,
        calculated = f.Calculated is null ? null : new { kind = f.Calculated.Kind, operands = f.Calculated.Operands, template = f.Calculated.Template, decimals = f.Calculated.Decimals },
        searchable = f.Searchable, filterable = f.Filterable, sortable = f.Sortable, reportable = f.Reportable, classification = f.Classification,
        visibleToRoles = f.VisibleToRoles, editableByRoles = f.EditableByRoles, visibleInStates = f.VisibleInStates, editableInStates = f.EditableInStates, contractTypes = f.ContractTypes,
        status = f.Status, effectiveFrom = f.EffectiveFrom, effectiveTo = f.EffectiveTo, usableInApprovals = f.UsableInApprovals
    };
}
