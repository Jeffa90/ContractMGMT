using System.Text.Json.Nodes;

namespace Ecp.Configuration.Forms;

/// <summary>The kinds of input a configuration form offers. Every one is a closed, typed control: nothing here accepts an expression, a script or a free-form document.</summary>
public enum FormFieldKind
{
    /// <summary>One line of text.</summary>
    Text,
    /// <summary>Several lines of text (message bodies).</summary>
    LongText,
    Integer,
    Decimal,
    Boolean,
    /// <summary>A calendar date, entered and stored as yyyy-MM-dd.</summary>
    Date,
    /// <summary>One choice from a closed list.</summary>
    Select,
    /// <summary>Any number of choices from a closed list.</summary>
    MultiSelect,
    /// <summary>A list of short texts, one per line.</summary>
    Lines,
    /// <summary>A list of whole numbers separated by commas.</summary>
    IntegerList,
    /// <summary>A group of related fields.</summary>
    Object,
    /// <summary>An ordered list of groups, each added, removed and moved with buttons.</summary>
    Rows,
    /// <summary>An ordered, named list: a name and a group of fields for each.</summary>
    Named,
}

public sealed record FormOption(string Value, string Label);

/// <summary>One field of a configuration form. The name is the property of the stored document, so the form and the document can only ever disagree about a value, never about a shape.</summary>
public sealed class FormField
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required FormFieldKind Kind { get; init; }
    /// <summary>What the field is for and what a wrong value does, in plain words.</summary>
    public string? Help { get; init; }
    public string? Example { get; init; }
    public bool Required { get; init; }
    public IReadOnlyList<FormOption> Options { get; init; } = [];
    /// <summary>The name of a list the host supplies at run time (permissions, classification levels ...). Used when <see cref="Options"/> is empty.</summary>
    public string? OptionsKey { get; init; }
    /// <summary>Options that depend on the document being edited (a transition's source states are the states defined above it).</summary>
    public Func<JsonObject, IReadOnlyList<FormOption>>? DerivedOptions { get; init; }
    /// <summary>Suggestions for a text field; any value may still be typed.</summary>
    public string? SuggestionsKey { get; init; }
    public IReadOnlyList<FormField> Children { get; init; } = [];
    /// <summary>The child whose value titles a row ("Draft" in "State 3: Draft").</summary>
    public string? RowLabelField { get; init; }
    /// <summary>What one row is called ("state", "transition").</summary>
    public string RowNoun { get; init; } = "item";
    public string KeyLabel { get; init; } = "Name";
    public int MaxRows { get; init; } = 500;
    /// <summary>The value a new row starts with (text, number or true/false).</summary>
    public string? Default { get; init; }
    /// <summary>For a list of choices: show a one-per-line box when no list is available to choose from.</summary>
    public bool LinesWhenNoOptions { get; init; }
    public string? Pattern { get; init; }
    public int? MaxLength { get; init; }
    /// <summary>Leave the property out of the document when nothing is entered (a document that says nothing means something different from one that says "none").</summary>
    public bool OmitWhenEmpty { get; init; }
    /// <summary>A choice stored as true or false that can also be left unanswered (the document then says nothing).</summary>
    public bool AsBoolean { get; init; }
    /// <summary>What the empty choice of a list says; "(not set)" when absent.</summary>
    public string? BlankLabel { get; init; }
    /// <summary>What a document that does not mention the field is read as, shown pre-selected so that saving does not change the meaning.</summary>
    public IReadOnlyList<string> WhenAbsent { get; init; } = [];
}

public sealed record FormSection(string Key, string Title, string? Help, IReadOnlyList<FormField> Fields);

/// <summary>The form for one kind of configuration: its sections and fields, in the order they are written to the document.</summary>
public sealed class FormSchema
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Intro { get; init; }
    public required IReadOnlyList<FormSection> Sections { get; init; }
    public IEnumerable<FormField> RootFields => Sections.SelectMany(s => s.Fields);
}

public interface IFormSchemaSource
{
    IEnumerable<FormSchema> Schemas();
}

/// <summary>Lists the host supplies at run time for the closed vocabularies that depend on the installation (permissions, classification levels, approver groups, published matrices, structure codes).</summary>
public interface IFormLookupProvider
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<FormOption>>> LookupsAsync(Guid organisationId, CancellationToken ct = default);
}

public sealed record FormProblem(string Path, string Message, bool IsError = true);
