using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ecp.Configuration.Forms;

/// <summary>
/// Turns what a person typed into a form into a configuration document, and a document back into the values a form shows. The document is built from the schema alone, field by field, so the only shapes that can be
/// produced are the ones the schema lists; nothing a person types is ever read as structure, a script or an expression. Values that cannot be read as the type the field wants are reported against that field.
/// </summary>
public static class FormBinder
{
    public const string Prefix = "f.";
    public const string KeyName = "__key";
    public const string CountName = "n";

    public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "." + name;
    public static string Name(string path) => Prefix + path;

    // ---- document to values ---------------------------------------------------------------------------------------------------------------------

    public static string Scalar(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v => v.ToJsonString().Trim('"'),
        _ => "",
    };

    public static bool Truth(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static IReadOnlyList<string> Strings(JsonNode? node)
        => node is JsonArray a ? a.Select(Scalar).Where(s => s.Length > 0).ToList() : [];

    // ---- document to posted values ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The values a browser would post if the person opened this document in the form and pressed Save without touching anything. It is what the form shows, so binding it again must give the same document;
    /// a test uses it to prove that no shipped document loses anything on its way through a form.
    /// </summary>
    public static Dictionary<string, string[]> Flatten(FormSchema schema, JsonObject document)
    {
        var posted = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (document["schemaVersion"] is { } sv) posted[Name("schemaVersion")] = [Scalar(sv)];
        foreach (var f in schema.RootFields) FlattenField(f, "", document[f.Name], posted);
        return posted;
    }

    private static void FlattenField(FormField f, string parent, JsonNode? node, Dictionary<string, string[]> posted)
    {
        var path = Join(parent, f.Name);
        switch (f.Kind)
        {
            case FormFieldKind.Boolean:
                if (node is null ? string.Equals(f.Default, "true", StringComparison.OrdinalIgnoreCase) : Truth(node)) posted[Name(path)] = ["true"];
                break;
            case FormFieldKind.MultiSelect:
                posted[Name(path)] = node is null ? [.. f.WhenAbsent] : [.. Strings(node)];
                break;
            case FormFieldKind.Lines:
                posted[Name(path)] = [string.Join('\n', Strings(node))];
                break;
            case FormFieldKind.IntegerList:
                posted[Name(path)] = [node is JsonArray a ? string.Join(", ", a.Select(Scalar)) : ""];
                break;
            case FormFieldKind.Object:
                foreach (var c in f.Children) FlattenField(c, path, (node as JsonObject)?[c.Name], posted);
                break;
            case FormFieldKind.Rows:
            {
                var rows = (node as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                posted[Name(Join(path, CountName))] = [rows.Count.ToString(CultureInfo.InvariantCulture)];
                for (var i = 0; i < rows.Count; i++)
                    foreach (var c in f.Children) FlattenField(c, Join(path, i.ToString(CultureInfo.InvariantCulture)), rows[i][c.Name], posted);
                break;
            }
            case FormFieldKind.Named:
            {
                var rows = (node as JsonObject)?.ToList() ?? [];
                posted[Name(Join(path, CountName))] = [rows.Count.ToString(CultureInfo.InvariantCulture)];
                for (var i = 0; i < rows.Count; i++)
                {
                    var rowPath = Join(path, i.ToString(CultureInfo.InvariantCulture));
                    posted[Name(Join(rowPath, KeyName))] = [rows[i].Key];
                    foreach (var c in f.Children) FlattenField(c, rowPath, (rows[i].Value as JsonObject)?[c.Name], posted);
                }
                break;
            }
            default:
                posted[Name(path)] = [Scalar(node)];
                break;
        }
    }

    // ---- values to document ---------------------------------------------------------------------------------------------------------------------

    public static (JsonObject Document, List<FormProblem> Problems) Bind(FormSchema schema, IReadOnlyDictionary<string, string[]> posted)
    {
        var problems = new List<FormProblem>();
        var root = new JsonObject();
        // The document's own version marker is not a field a person edits; it travels with the form.
        if (posted.TryGetValue(Name("schemaVersion"), out var sv) && int.TryParse(sv.FirstOrDefault(), out var v)) root["schemaVersion"] = v; else root["schemaVersion"] = 1;
        foreach (var field in schema.RootFields) BindField(field, "", posted, root, problems);
        return (root, problems);
    }

    private static string? One(IReadOnlyDictionary<string, string[]> posted, string path)
        => posted.TryGetValue(Name(path), out var values) ? values.FirstOrDefault() : null;

    private static void BindField(FormField f, string parent, IReadOnlyDictionary<string, string[]> posted, JsonObject target, List<FormProblem> problems)
    {
        var path = Join(parent, f.Name);
        var node = BindValue(f, path, posted, problems);
        if (node is not null) target[f.Name] = node;
    }

    private static JsonNode? BindValue(FormField f, string path, IReadOnlyDictionary<string, string[]> posted, List<FormProblem> problems)
    {
        switch (f.Kind)
        {
            case FormFieldKind.Text:
            case FormFieldKind.LongText:
            case FormFieldKind.Select:
            {
                var text = (One(posted, path) ?? "").Replace("\r\n", "\n").Trim();
                if (f.Kind != FormFieldKind.LongText) text = text.Replace("\n", " ");
                if (f.AsBoolean) return text switch { "true" => JsonValue.Create(true), "false" => JsonValue.Create(false), _ => null };
                return text.Length == 0 ? null : JsonValue.Create(text);
            }
            case FormFieldKind.Date:
            {
                var text = (One(posted, path) ?? "").Trim();
                if (text.Length == 0) return null;
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { problems.Add(new FormProblem(path, "Enter the date as year-month-day, for example 2027-01-31.")); return null; }
                return JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            case FormFieldKind.Integer:
            {
                var text = (One(posted, path) ?? "").Trim();
                if (text.Length == 0) return null;
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) { problems.Add(new FormProblem(path, "Enter a whole number, for example 30.")); return null; }
                return JsonValue.Create(n);
            }
            case FormFieldKind.Decimal:
            {
                var text = (One(posted, path) ?? "").Trim();
                if (text.Length == 0) return null;
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n)) { problems.Add(new FormProblem(path, "Enter a number, for example 250000 or 1.5.")); return null; }
                return JsonValue.Create(n);
            }
            case FormFieldKind.Boolean:
                return JsonValue.Create(posted.TryGetValue(Name(path), out var b) && b.Contains("true", StringComparer.Ordinal));
            case FormFieldKind.MultiSelect:
            case FormFieldKind.Lines:
            {
                var values = posted.TryGetValue(Name(path), out var raw) ? raw : [];
                var items = values.SelectMany(x => x.Replace("\r\n", "\n").Split('\n')).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (items.Count == 0 && f.OmitWhenEmpty) return null;
                return new JsonArray(items.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            }
            case FormFieldKind.IntegerList:
            {
                var text = One(posted, path) ?? "";
                var result = new JsonArray();
                foreach (var part in Regex.Split(text, @"[,\s;]+").Where(p => p.Length > 0))
                {
                    if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) { problems.Add(new FormProblem(path, $"'{part}' is not a whole number. Separate numbers with commas, for example 30, 14, 7.")); continue; }
                    result.Add(n);
                }
                return result;
            }
            case FormFieldKind.Object:
            {
                var o = new JsonObject();
                foreach (var c in f.Children) BindField(c, path, posted, o, problems);
                return o;
            }
            case FormFieldKind.Rows:
            {
                var list = new JsonArray();
                for (var i = 0; i < Count(posted, path, f.MaxRows); i++)
                {
                    var row = new JsonObject();
                    foreach (var c in f.Children) BindField(c, Join(path, i.ToString(CultureInfo.InvariantCulture)), posted, row, problems);
                    list.Add(row);
                }
                return list;
            }
            case FormFieldKind.Named:
            {
                var o = new JsonObject();
                for (var i = 0; i < Count(posted, path, f.MaxRows); i++)
                {
                    var rowPath = Join(path, i.ToString(CultureInfo.InvariantCulture));
                    var key = (One(posted, Join(rowPath, KeyName)) ?? "").Trim();
                    if (key.Length == 0) { problems.Add(new FormProblem(Join(rowPath, KeyName), $"Give this {f.RowNoun} a name.")); continue; }
                    if (o.ContainsKey(key)) { problems.Add(new FormProblem(Join(rowPath, KeyName), $"The name '{key}' is used twice. Names must be different.")); continue; }
                    var row = new JsonObject();
                    foreach (var c in f.Children) BindField(c, rowPath, posted, row, problems);
                    o[key] = row;
                }
                return o;
            }
            default:
                return null;
        }
    }

    private static int Count(IReadOnlyDictionary<string, string[]> posted, string path, int max)
        => int.TryParse(One(posted, Join(path, CountName)), out var n) ? Math.Clamp(n, 0, max) : 0;

    // ---- structural operations ------------------------------------------------------------------------------------------------------------------

    /// <summary>Applies "add|path", "remove|path|index", "up|path|index" or "down|path|index" to a document. Returns false when the operation does not name a list of this schema.</summary>
    public static bool Apply(FormSchema schema, JsonObject document, string? operation)
    {
        var parts = (operation ?? "").Split('|');
        if (parts.Length < 2) return false;
        var verb = parts[0]; var path = parts[1];
        var field = Find(schema, path);
        if (field is not { Kind: FormFieldKind.Rows or FormFieldKind.Named }) return false;
        var container = Navigate(schema, document, path, createLeaf: true);
        var index = parts.Length > 2 && int.TryParse(parts[2], out var i) ? i : -1;
        if (container is JsonArray array)
        {
            switch (verb)
            {
                case "add":
                    if (array.Count < field.MaxRows) array.Add(NewRow(field, array.Count + 1, null));
                    return true;
                case "remove" when index >= 0 && index < array.Count:
                    array.RemoveAt(index); return true;
                case "up" when index > 0 && index < array.Count:
                    Swap(array, index, index - 1); return true;
                case "down" when index >= 0 && index < array.Count - 1:
                    Swap(array, index, index + 1); return true;
            }
        }
        else if (container is JsonObject named)
        {
            var entries = named.Select(kv => (kv.Key, kv.Value)).ToList();
            switch (verb)
            {
                case "add":
                    if (entries.Count < field.MaxRows)
                    {
                        var n = entries.Count + 1;
                        var key = $"new-{n}";
                        while (named.ContainsKey(key)) key = $"new-{++n}";
                        named[key] = NewRow(field, n, key);
                    }
                    return true;
                case "remove" when index >= 0 && index < entries.Count:
                    named.Remove(entries[index].Key); return true;
                case "up" when index > 0 && index < entries.Count:
                    Reorder(named, entries, index, index - 1); return true;
                case "down" when index >= 0 && index < entries.Count - 1:
                    Reorder(named, entries, index, index + 1); return true;
            }
        }
        return false;
    }

    private static void Swap(JsonArray array, int a, int b)
    {
        var first = array[a]; var second = array[b];
        array[a] = null; array[b] = null;
        array[a] = second; array[b] = first;
    }

    private static void Reorder(JsonObject obj, List<(string Key, JsonNode? Value)> entries, int from, int to)
    {
        var moved = entries[from];
        entries.RemoveAt(from);
        entries.Insert(to, moved);
        foreach (var (k, _) in entries) obj.Remove(k);
        foreach (var (k, v) in entries) obj[k] = v;
    }

    public static JsonObject NewRow(FormField list, int number, string? key)
    {
        var row = new JsonObject();
        foreach (var c in list.Children) { var node = Default(c); if (node is not null) row[c.Name] = node; }
        return row;
    }

    private static JsonNode? Default(FormField c) => c.Kind switch
    {
        FormFieldKind.Boolean => JsonValue.Create(string.Equals(c.Default, "true", StringComparison.OrdinalIgnoreCase)),
        FormFieldKind.Integer => int.TryParse(c.Default, out var n) ? JsonValue.Create(n) : null,
        FormFieldKind.Decimal => decimal.TryParse(c.Default, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : null,
        FormFieldKind.Text or FormFieldKind.LongText or FormFieldKind.Select or FormFieldKind.Date => string.IsNullOrEmpty(c.Default) ? null : JsonValue.Create(c.Default),
        FormFieldKind.MultiSelect or FormFieldKind.Lines or FormFieldKind.IntegerList or FormFieldKind.Rows => new JsonArray(),
        FormFieldKind.Named => new JsonObject(),
        FormFieldKind.Object => ObjectDefault(c),
        _ => null,
    };

    private static JsonObject ObjectDefault(FormField c)
    {
        var o = new JsonObject();
        foreach (var child in c.Children) { var node = Default(child); if (node is not null) o[child.Name] = node; }
        return o;
    }

    // ---- paths ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The field a form path such as <c>transitions.2.guards</c> names (row numbers skipped).</summary>
    public static FormField? Find(FormSchema schema, string path)
    {
        FormField? current = null;
        IEnumerable<FormField> scope = schema.RootFields;
        foreach (var segment in path.Split('.'))
        {
            if (int.TryParse(segment, out _) || segment == KeyName) continue;
            current = scope.FirstOrDefault(x => x.Name == segment);
            if (current is null) return null;
            scope = current.Children;
        }
        return current;
    }

    private static JsonNode? Navigate(FormSchema schema, JsonObject root, string path, bool createLeaf)
    {
        JsonNode? node = root;
        IEnumerable<FormField> scope = schema.RootFields;
        FormField? field = null;
        var segments = path.Split('.');
        for (var s = 0; s < segments.Length; s++)
        {
            var segment = segments[s];
            if (int.TryParse(segment, out var index))
            {
                if (node is JsonArray a && index >= 0 && index < a.Count) node = a[index];
                else if (node is JsonObject named && field is { Kind: FormFieldKind.Named } && index >= 0 && index < named.Count) node = named.ElementAt(index).Value;
                else return null;
                continue;
            }
            field = scope.FirstOrDefault(x => x.Name == segment);
            if (field is null || node is not JsonObject o) return null;
            if (o[segment] is null && createLeaf && s == segments.Length - 1) o[segment] = field.Kind == FormFieldKind.Named ? new JsonObject() : new JsonArray();
            node = o[segment];
            scope = field.Children;
        }
        return node;
    }

    // ---- problems --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Maps a validation path such as <c>$.transitions[2].guards</c> or <c>$.templates.reminder.subject</c> to the form path of the field it is about (<c>transitions.2.guards</c>), or to the nearest field above it when the
    /// exact one is not on the form. Returns null when the message is about the document as a whole.
    /// </summary>
    public static string? FormPathOf(FormSchema schema, JsonObject document, string validationPath)
    {
        var p = validationPath.TrimStart('$').TrimStart('.');
        if (p.Length == 0) return null;
        p = Regex.Replace(p, @"\['([^']*)'\]", ".$1");
        p = Regex.Replace(p, @"\[(\d+)\]", ".$1");
        var segments = p.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        IEnumerable<FormField> scope = schema.RootFields;
        FormField? list = null;                       // a list field that is waiting for its row segment
        JsonNode? node = document;
        foreach (var segment in segments)
        {
            if (list is not null)
            {
                if (list.Kind == FormFieldKind.Rows)
                {
                    if (!int.TryParse(segment, out var row)) break;
                    result.Add(segment);
                    node = node is JsonArray a && row >= 0 && row < a.Count ? a[row] : null;
                }
                else
                {
                    // A named list is numbered on the form but addressed by name in the document.
                    var names = (node as JsonObject)?.Select(kv => kv.Key).ToList() ?? [];
                    var at = names.IndexOf(segment);
                    if (at < 0) break;
                    result.Add(at.ToString(CultureInfo.InvariantCulture));
                    node = ((JsonObject)node!)[segment];
                }
                scope = list.Children; list = null;
                continue;
            }
            var next = scope.FirstOrDefault(x => x.Name == segment);
            if (next is null) break;
            result.Add(segment);
            node = (node as JsonObject)?[segment];
            if (next.Kind is FormFieldKind.Rows or FormFieldKind.Named) list = next; else scope = next.Children;
        }
        return result.Count == 0 ? null : string.Join('.', result);
    }
}
