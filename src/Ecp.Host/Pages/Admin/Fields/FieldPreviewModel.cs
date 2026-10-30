using Ecp.Contracts;
using Ecp.Metadata;

namespace Ecp.Host.Pages.Admin.Fields;

/// <summary>What the preview panel needs: the proposed field, the set it would sit in (with its section), and the plain-English review.</summary>
public sealed record FieldPreviewModel(FieldDefinition Definition, CustomFieldSet Set, string SectionKey, string SectionLabel, IReadOnlyList<string> Review, string ExampleDetails)
{
    /// <summary>The field in a set of its own, so the shared form partials draw exactly what the real forms draw for it.</summary>
    public CustomFieldForm AsForm(bool editable)
    {
        var sections = Set.Sections.Any(s => s.Key == SectionKey) || SectionKey == "general" ? Set.Sections : [.. Set.Sections, new FieldSection(SectionKey, SectionLabel, int.MaxValue)];
        var set = new CustomFieldSet { Sections = sections, Lookups = Set.Lookups, Fields = [Definition] };
        var raw = editable ? Definition.Default : [];      // the New form starts with the field's pre-filled value, as the real form does
        var display = editable ? "" : ExampleDetails;
        return new CustomFieldForm(0, set, [new CustomFieldEntry(Definition, raw, display, editable)], [], []);
    }
}
