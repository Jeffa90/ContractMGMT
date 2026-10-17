using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Configuration;

/// <summary>
/// A terminology document: the content of the <c>Terminology</c> configuration kind and of a pack's terminology document. <see cref="Mode"/> is <c>complete</c> (the document must carry every required key for each locale it
/// declares, as a pack does) or <c>overrides</c> (an organisation's draft: only what it changes; everything else is inherited). Labels are plain text. HTML, script and templates are refused.
/// </summary>
public sealed record TerminologyDocument(string Mode, IReadOnlyList<TermEntry> Terms)
{
    public const string Complete = "complete";
    public const string Overrides = "overrides";
    public const int MaxTerms = 600;
    public const int MaxLabel = 80;
    public const int MaxShort = 30;
    public const int MaxDescription = 300;
    public const int MaxHelp = 600;

    private static readonly Regex KeyPattern = new(@"^[a-z][a-z0-9]{0,23}(\.[a-z][a-z0-9]{0,23}){0,4}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex LocalePattern = new(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$", RegexOptions.Compiled | RegexOptions.NonBacktracking);
    private static readonly Regex Markup = new(@"<|>|&[#a-zA-Z0-9]+;|javascript\s*:|vbscript\s*:|\bon[a-z]+\s*=|\{\{|\}\}|\$\{|<%|%>|@\(|\bexpr\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static TerminologyDocument Empty => new(Overrides, []);

    public static TerminologyDocument Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DomainException("invalid_json", "The terminology document must be a JSON object.");
            var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? Complete : Complete;
            var terms = new List<TermEntry>();
            if (root.TryGetProperty("terms", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var e in t.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) throw new DomainException("invalid_terminology", "Each term must be an object.");
                    string? S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    TermSource? source = e.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object
                        ? new TermSource(s.TryGetProperty("packKey", out var pk) ? pk.GetString() ?? "" : "", s.TryGetProperty("packVersion", out var pv) ? pv.GetString() ?? "" : "") : null;
                    var active = !(e.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False);
                    terms.Add(new TermEntry(S("key") ?? "", S("locale") ?? "", S("singular") ?? "", S("plural") ?? "", Blank(S("short")), Blank(S("description")), Blank(S("help")), source, active));
                }
            return new TerminologyDocument(mode, terms);
        }
        catch (JsonException) { throw new DomainException("invalid_json", "The terminology document is not valid JSON."); }
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    public string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        mode = Mode,
        terms = Terms.OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Locale, StringComparer.Ordinal)
            .Select(x => new { key = x.Key, locale = x.Locale, singular = x.Singular, plural = x.Plural, @short = x.Short, description = x.Description, help = x.Help, source = x.Source is null ? null : new { packKey = x.Source.PackKey, packVersion = x.Source.PackVersion }, active = x.Active ? (bool?)null : false })
    }, Json);

    /// <summary>The same content stamped as copied from a pack, so an administrator can see which entries still match the pack and which they changed.</summary>
    public TerminologyDocument WithSource(string packKey, string packVersion, string mode)
        => new(mode, Terms.Select(x => x with { Source = new TermSource(packKey, packVersion) }).ToList());

    /// <summary>Checks the document. Errors block publication; warnings do not.</summary>
    public IReadOnlyList<ValidationMessage> Check()
    {
        var found = new List<ValidationMessage>();
        void Err(string path, string code, string text) => found.Add(new(ValidationSeverity.Error, path, code, text));
        void Warn(string path, string code, string text) => found.Add(new(ValidationSeverity.Warning, path, code, text));

        if (Mode is not (Complete or Overrides)) Err("$.mode", "term_mode", "The mode must be 'complete' or 'overrides'.");
        if (Terms.Count > MaxTerms) Err("$.terms", "term_count", $"A terminology document may hold at most {MaxTerms} terms.");

        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < Terms.Count && i < MaxTerms; i++)
        {
            var t = Terms[i];
            var p = $"$.terms['{Trim(t.Key)}','{Trim(t.Locale)}']";   // named by key and locale, so a person reads which term, not a position
            if (!KeyPattern.IsMatch(t.Key)) { Err(p + ".key", "term_key", "A term key is lower-case words separated by dots, for example 'contract.type'."); continue; }
            if (!LocalePattern.IsMatch(t.Locale)) Err(p + ".locale", "term_locale", $"'{Trim(t.Locale)}' is not a locale such as 'en' or 'en-AU'.");
            if (!seen.Add((t.Key, t.Locale))) Err(p, "term_duplicate", $"The key '{t.Key}' appears more than once for the locale '{t.Locale}'.");
            Label(p + ".singular", "singular", t.Singular, MaxLabel, true);
            Label(p + ".plural", "plural", t.Plural, MaxLabel, true);
            Label(p + ".short", "short", t.Short, MaxShort, false);
            Label(p + ".description", "description", t.Description, MaxDescription, false);
            Label(p + ".help", "help", t.Help, MaxHelp, false);
            if (!TermCatalogue.IsKnown(t.Key)) Warn(p + ".key", "term_unknown_key", $"The key '{t.Key}' is not one this version of the platform uses, so it has no effect.");
            if (t.Source is { } s && (s.PackKey.Length > 64 || s.PackVersion.Length > 40)) Err(p + ".source", "term_source", "The pack reference is too long.");
        }

        if (Mode == Complete)
        {
            var locales = Terms.Select(x => x.Locale).Where(l => LocalePattern.IsMatch(l)).Distinct(StringComparer.Ordinal).ToList();
            if (locales.Count == 0) Err("$.terms", "term_locale_missing", "A complete terminology document needs terms for at least one locale.");
            foreach (var locale in locales.Where(l => IsBaseLocale(l) || !locales.Contains(l.Split('-')[0])))
                foreach (var key in TermCatalogue.RequiredKeys)
                    if (!seen.Contains((key, locale))) Err("$.terms", "term_required_missing", $"The required key '{key}' has no term for the locale '{locale}'.");
        }
        return found;

        void Label(string path, string field, string? value, int max, bool required)
        {
            if (string.IsNullOrWhiteSpace(value)) { if (required) Err(path, "term_" + field + "_missing", $"The {field} label is required."); return; }
            if (value.Length > max) Err(path, "term_" + field + "_length", $"The {field} label must be at most {max} characters.");
            var singleLine = field is "singular" or "plural" or "short";
            if (value != value.Trim() || (singleLine && (value.Contains('\n') || value.Contains('\r')))) Err(path, "term_" + field + "_space", $"The {field} label has leading or trailing spaces or line breaks.");
            if (HasControl(value, singleLine)) Err(path, "term_" + field + "_control", $"The {field} label contains control characters.");
            if (Markup.IsMatch(value)) Err(path, "term_" + field + "_markup", $"The {field} label must be plain text: no HTML, script, template or expression syntax.");
        }
    }

    /// <summary>A locale that has no region ('en') is the base for its regional variants ('en-AU'), which only need what differs.</summary>
    private static bool IsBaseLocale(string locale) => !locale.Contains('-');

    private static bool HasControl(string v, bool singleLine) => v.Any(c => char.IsControl(c) && (singleLine || c is not ('\n' or '\r')));
    private static string Trim(string v) => v.Length > 20 ? v[..20] + "..." : v;
}
