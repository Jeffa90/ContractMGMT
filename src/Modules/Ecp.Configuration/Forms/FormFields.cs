namespace Ecp.Configuration.Forms;

/// <summary>Short constructors for the fields of a form schema, so a schema reads as a list of questions.</summary>
public static class FormFields
{
    public static FormField Text(string name, string label, string? help = null, string? example = null, bool required = false, string? suggestions = null, int? maxLength = null, string? @default = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Text, Help = help, Example = example, Required = required, SuggestionsKey = suggestions, MaxLength = maxLength, Default = @default };

    public static FormField LongText(string name, string label, string? help = null, string? example = null, bool required = false, int? maxLength = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.LongText, Help = help, Example = example, Required = required, MaxLength = maxLength };

    public static FormField Integer(string name, string label, string? help = null, string? example = null, bool required = false, string? @default = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Integer, Help = help, Example = example, Required = required, Default = @default };

    public static FormField Decimal(string name, string label, string? help = null, string? example = null, bool required = false)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Decimal, Help = help, Example = example, Required = required };

    public static FormField Boolean(string name, string label, string? help = null, bool @default = false)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Boolean, Help = help, Default = @default ? "true" : "false" };

    public static FormField Date(string name, string label, string? help = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Date, Help = help, Example = "2027-01-31" };

    public static FormField Select(string name, string label, IEnumerable<FormOption> options, string? help = null, bool required = false, string? @default = null, string? blankLabel = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Select, Help = help, Required = required, Options = options.ToList(), Default = @default, BlankLabel = blankLabel };

    public static FormField Select(string name, string label, IEnumerable<string> values, string? help = null, bool required = false, string? @default = null, string? blankLabel = null)
        => Select(name, label, values.Select(v => new FormOption(v, v)), help, required, @default, blankLabel);

    /// <summary>A yes/no/unanswered choice stored as true or false; unanswered leaves the property out so the document's own default applies.</summary>
    public static FormField YesNo(string name, string label, string unansweredLabel, string? help = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Select, Help = help, AsBoolean = true, Options = [new FormOption("true", "Yes"), new FormOption("false", "No")], BlankLabel = unansweredLabel };

    public static FormField SelectFrom(string name, string label, string optionsKey, string? help = null, bool required = false)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Select, Help = help, Required = required, OptionsKey = optionsKey };

    public static FormField Many(string name, string label, IEnumerable<FormOption> options, string? help = null, IEnumerable<string>? whenAbsent = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.MultiSelect, Help = help, Options = options.ToList(), WhenAbsent = whenAbsent?.ToList() ?? [] };

    public static FormField Many(string name, string label, IEnumerable<string> values, string? help = null, IEnumerable<string>? whenAbsent = null)
        => Many(name, label, values.Select(v => new FormOption(v, v)), help, whenAbsent);

    public static FormField ManyFrom(string name, string label, string optionsKey, string? help = null, bool linesWhenNoOptions = false)
        => new() { Name = name, Label = label, Kind = FormFieldKind.MultiSelect, Help = help, OptionsKey = optionsKey, LinesWhenNoOptions = linesWhenNoOptions };

    public static FormField ManyDerived(string name, string label, Func<System.Text.Json.Nodes.JsonObject, IReadOnlyList<FormOption>> derive, string? help = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.MultiSelect, Help = help, DerivedOptions = derive };

    public static FormField SelectDerived(string name, string label, Func<System.Text.Json.Nodes.JsonObject, IReadOnlyList<FormOption>> derive, string? help = null, bool required = false)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Select, Help = help, Required = required, DerivedOptions = derive };

    public static FormField Lines(string name, string label, string? help = null, string? example = null, bool omitWhenEmpty = false)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Lines, Help = help, Example = example, OmitWhenEmpty = omitWhenEmpty };

    public static FormField Numbers(string name, string label, string? help = null, string? example = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.IntegerList, Help = help, Example = example };

    public static FormField Group(string name, string label, IEnumerable<FormField> children, string? help = null)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Object, Help = help, Children = children.ToList() };

    public static FormField Rows(string name, string label, string noun, string? rowLabelField, IEnumerable<FormField> children, string? help = null, int max = 500)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Rows, Help = help, RowNoun = noun, RowLabelField = rowLabelField, Children = children.ToList(), MaxRows = max };

    public static FormField Named(string name, string label, string noun, string keyLabel, IEnumerable<FormField> children, string? help = null, int max = 200)
        => new() { Name = name, Label = label, Kind = FormFieldKind.Named, Help = help, RowNoun = noun, KeyLabel = keyLabel, Children = children.ToList(), MaxRows = max };
}
