using System.Text.Json.Nodes;
using Ecp.Configuration.Forms;

namespace Ecp.Host.Pages.Admin.Forms;

/// <summary>Everything the form partials need to draw one document: the document being shown, the lists to choose from, the problems by field and whether it can be changed.</summary>
public sealed class FormPageContext(FormSchema schema, JsonObject document, IReadOnlyDictionary<string, IReadOnlyList<FormOption>> lookups, IReadOnlyList<FormProblem> problems, bool readOnly)
{
    public FormSchema Schema { get; } = schema;
    public JsonObject Document { get; } = document;
    public IReadOnlyDictionary<string, IReadOnlyList<FormOption>> Lookups { get; } = lookups;
    public bool ReadOnly { get; } = readOnly;
    public IReadOnlyList<FormProblem> Problems { get; } = problems;
    public HashSet<string> SuggestionKeys { get; } = new(StringComparer.Ordinal);

    public static string IdOf(string path) => "f-" + path.Replace('.', '-');

    /// <summary>Problems about exactly this field, or about a list's rows as a whole when asked for the list.</summary>
    public IEnumerable<FormProblem> About(string path) => Problems.Where(p => p.Path == path);

    public int ErrorCountUnder(string path) => Problems.Count(p => p.IsError && (p.Path == path || p.Path.StartsWith(path + ".", StringComparison.Ordinal)));

    /// <summary>The choices for a field: the fixed list, the installation's list, anything derived from the document, and any value the document already holds that is not on the list (shown, so that opening a form never changes it).</summary>
    public IReadOnlyList<FormOption> OptionsFor(FormField f, IEnumerable<string> current)
    {
        var options = new List<FormOption>(f.Options);
        if (f.OptionsKey is not null && Lookups.TryGetValue(f.OptionsKey, out var supplied)) foreach (var o in supplied) if (options.All(x => x.Value != o.Value)) options.Add(o);
        if (f.DerivedOptions is not null) foreach (var o in f.DerivedOptions(Document)) if (options.All(x => x.Value != o.Value)) options.Add(o);
        foreach (var value in current.Where(v => v.Length > 0))
            if (options.All(x => x.Value != value)) options.Add(new FormOption(value, value + " (not on the list)"));
        return options;
    }

    public IReadOnlyList<FormOption> Suggestions(string key) => Lookups.TryGetValue(key, out var l) ? l : [];
}

/// <summary>One field to draw: the field, its current value in the document, its path in the form, and the context.</summary>
public sealed record FormFieldView(FormField Field, JsonNode? Value, string Path, FormPageContext Context, string? RowTitle = null);
