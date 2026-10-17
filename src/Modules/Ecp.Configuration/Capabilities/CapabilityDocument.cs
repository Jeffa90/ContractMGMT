using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Configuration;

/// <summary>The configuration kind name for capability switches ("feature flags that gate compiled capabilities"; docs/CAPABILITIES.md).</summary>
public static class CapabilityKinds
{
    public const string Name = "Capabilities";
}

/// <summary>
/// A capability document: the content of the <c>Capabilities</c> configuration kind and of a pack's capabilities document. It is a list of statements, each a capability key and whether it is on. In a pack (<c>defaults</c>) it
/// gives the starting position for the organisations that use the pack; in an organisation (<c>overrides</c>) it is that organisation's own choice, which wins. A capability the document does not mention is not decided by it.
/// Plain data: there are no conditions, expressions or code, and only the platform's own capability keys mean anything.
/// </summary>
public sealed record CapabilityDocument(string Mode, IReadOnlyList<CapabilityEntry> Capabilities)
{
    public const string Defaults = "defaults";
    public const string Overrides = "overrides";
    public const int MaxNote = 300;

    private static readonly Regex KeyPattern = new(@"^[a-z][a-z0-9]{0,23}(\.[a-z][a-z0-9]{0,23}){0,3}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex Markup = new(@"<|>|&[#a-zA-Z0-9]+;|javascript\s*:|vbscript\s*:|\bon[a-z]+\s*=|\{\{|\}\}|\$\{|<%|%>|@\(|\bexpr\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static CapabilityDocument Empty => new(Overrides, []);

    public static CapabilityDocument Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DomainException("invalid_json", "The capabilities document must be a JSON object.");
            var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? Overrides : Overrides;
            var items = new List<CapabilityEntry>();
            if (root.TryGetProperty("capabilities", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) throw new DomainException("invalid_capabilities", "Each capability must be an object.");
                    var key = e.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
                    var enabled = !(e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
                    var note = e.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString() : null;
                    items.Add(new CapabilityEntry(key, enabled, note));
                }
            return new CapabilityDocument(mode, items);
        }
        catch (JsonException) { throw new DomainException("invalid_json", "The capabilities document is not valid JSON."); }
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        mode = Mode,
        capabilities = Capabilities.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new { key = c.Key, enabled = c.Enabled, note = c.Note }).ToList()
    }, Json);

    /// <summary>Checks the document on its own. Whether the result is coherent with the rest of the organisation's configuration is checked by the kind, which knows the organisation.</summary>
    public IReadOnlyList<ValidationMessage> Check()
    {
        var found = new List<ValidationMessage>();
        void Err(string path, string code, string text) => found.Add(new(ValidationSeverity.Error, path, code, text));
        void Warn(string path, string code, string text) => found.Add(new(ValidationSeverity.Warning, path, code, text));
        if (Mode is not (Defaults or Overrides)) Err("$.mode", "capability_mode", "The mode must be 'defaults' or 'overrides'.");
        if (Capabilities.Count > 100) Err("$.capabilities", "capability_count", "A capabilities document may hold at most 100 statements.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Capabilities.Take(100))
        {
            var p = $"$.capabilities['{(c.Key.Length > 30 ? c.Key[..30] + "..." : c.Key)}']";
            if (!KeyPattern.IsMatch(c.Key)) { Err(p + ".key", "capability_key", "A capability key is lower-case words separated by dots, for example 'legal.review'."); continue; }
            if (!seen.Add(c.Key)) Err(p, "capability_duplicate", $"The capability '{c.Key}' appears more than once.");
            if (!CapabilityCatalogue.IsKnown(c.Key)) Warn(p + ".key", "capability_unknown", $"The capability '{c.Key}' is not one this version of the platform has, so it has no effect.");
            if (c.Note is { } note)
            {
                if (note.Length > MaxNote) Err(p + ".note", "capability_note_length", $"The note must be at most {MaxNote} characters.");
                if (note != note.Trim() || note.Contains('\n') || note.Contains('\r') || note.Any(char.IsControl)) Err(p + ".note", "capability_note_space", "The note has leading or trailing spaces, line breaks or control characters.");
                if (Markup.IsMatch(note)) Err(p + ".note", "capability_note_markup", "The note must be plain text: no HTML, script, template or expression syntax.");
            }
        }
        return found;
    }

    /// <summary>The set that results from this document alone over the platform default (used to check a pack document by itself).</summary>
    public CapabilitySet AsSet() => CapabilitySet.Build(Capabilities, null, "document");

    /// <summary>Checks that a document does not leave an enabled capability without one it needs, as a pack document would apply to a fresh organisation.</summary>
    public IReadOnlyList<ValidationMessage> CheckCoherence()
        => AsSet().Unmet().Select(u => new ValidationMessage(ValidationSeverity.Error, $"$.capabilities['{u.Capability}']", "capability_requires",
            $"'{u.Capability}' needs '{u.Requires}', which is off. Turn '{u.Requires}' on, or turn '{u.Capability}' off.")).ToList();
}
