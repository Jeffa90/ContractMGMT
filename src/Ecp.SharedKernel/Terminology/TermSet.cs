using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ecp.SharedKernel;

/// <summary>Where a term entry came from, recorded when a pack's terminology is copied into an organisation's draft.</summary>
public sealed record TermSource(string PackKey, string PackVersion);

/// <summary>One labelled concept in one locale. The key is a stable machine identifier; every other member is data an administrator may change.</summary>
public sealed record TermEntry(string Key, string Locale, string Singular, string Plural, string? Short = null, string? Description = null, string? Help = null, TermSource? Source = null, bool Active = true);

/// <summary>Which layer supplied a label. A label's layer is informative only: it never changes what a person is allowed to do.</summary>
public enum TermLayer { Organisation, Pack, Platform, Unknown }

/// <summary>A fully resolved term for one locale.</summary>
public sealed record ResolvedTerm(string Key, string Singular, string Plural, string? Short, string? Description, string? Help, TermLayer Layer, TermSource? Source)
{
    public string ShortOrSingular => Short ?? Singular;
}

/// <summary>
/// The terminology in force for one organisation and one locale, resolved once. The order is: the organisation's published terminology, then the terminology of the solution pack it works from, then the
/// platform's neutral wording. A key that no layer knows resolves to its own key text, as a diagnostic only. A set is immutable, so it can be shared and cached.
/// </summary>
public sealed class TermSet
{
    private readonly IReadOnlyDictionary<string, ResolvedTerm> _terms;

    public string Locale { get; }
    /// <summary>Identifies exactly which organisation release and pack version this set was built from (used in cache keys and diagnostics).</summary>
    public string Fingerprint { get; }

    private TermSet(string locale, string fingerprint, IReadOnlyDictionary<string, ResolvedTerm> terms) { Locale = locale; Fingerprint = fingerprint; _terms = terms; }

    public IReadOnlyDictionary<string, ResolvedTerm> Terms => _terms;
    public bool Has(string key) => _terms.ContainsKey(key);
    public ResolvedTerm? Find(string key) => _terms.GetValueOrDefault(key);
    public TermLayer LayerOf(string key) => _terms.TryGetValue(key, out var t) ? t.Layer : TermLayer.Unknown;

    public string Singular(string key) => _terms.TryGetValue(key, out var t) ? t.Singular : key;
    public string Plural(string key) => _terms.TryGetValue(key, out var t) ? t.Plural : key;
    public string Short(string key) => _terms.TryGetValue(key, out var t) ? t.ShortOrSingular : key;
    public string? Description(string key) => _terms.TryGetValue(key, out var t) ? t.Description : null;
    public string? Help(string key) => _terms.TryGetValue(key, out var t) ? t.Help : null;

    /// <summary>Replaces <c>[[key]]</c> tokens in a text with this set's labels. Anything that is not a well-formed token is left alone, so a label can never inject another token.</summary>
    public string Expand(string text) => TermTokens.Expand(text, this);

    private static readonly Lazy<TermSet> NeutralEnglish = new(() => Build("en", null, null, null, null));

    /// <summary>Only the platform's neutral wording; used where no organisation is known and as the safe fall-back when configuration cannot be read.</summary>
    public static TermSet Neutral(string? locale = null)
        => string.IsNullOrEmpty(locale) || string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? NeutralEnglish.Value : Build(locale, null, null, null, null);

    public static TermSet Build(string locale, IEnumerable<TermEntry>? organisation, IEnumerable<TermEntry>? pack, string? organisationFingerprint, string? packFingerprint)
    {
        var chain = LocaleChain(locale);
        var keys = new HashSet<string>(TermCatalogue.ByKey.Keys, StringComparer.Ordinal);
        var organisationTerms = organisation?.ToList(); var packTerms = pack?.ToList();
        foreach (var d in new[] { organisationTerms, packTerms }) if (d is not null) foreach (var t in d) if (t.Active) keys.Add(t.Key);
        var result = new Dictionary<string, ResolvedTerm>(StringComparer.Ordinal);
        var orgIndex = Index(organisationTerms);
        var packIndex = Index(packTerms);
        foreach (var key in keys)
        {
            var layers = new List<(TermLayer Layer, TermEntry Entry)>();
            foreach (var l in chain) if (orgIndex.TryGetValue((key, l), out var e)) layers.Add((TermLayer.Organisation, e));
            foreach (var l in chain) if (packIndex.TryGetValue((key, l), out var e)) layers.Add((TermLayer.Pack, e));
            foreach (var l in chain) if (NeutralEntry(key, l) is { } n) layers.Add((TermLayer.Platform, n));
            if (layers.Count == 0) continue;
            var first = layers[0];
            string? Field(Func<TermEntry, string?> pick) => layers.Select(x => pick(x.Entry)).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            result[key] = new ResolvedTerm(key, first.Entry.Singular, first.Entry.Plural, first.Entry.Short ?? (layers.Where(x => x.Layer == first.Layer).Select(x => x.Entry.Short).FirstOrDefault(v => v is not null)),
                Field(x => x.Description), Field(x => x.Help), first.Layer, first.Entry.Source);
        }
        return new TermSet(locale, $"{locale}|{organisationFingerprint ?? "-"}|{packFingerprint ?? "-"}", result);
    }

    /// <summary>'en-AU' resolves as en-AU, then en. A locale with no region resolves as itself only.</summary>
    public static IReadOnlyList<string> LocaleChain(string locale)
    {
        var chain = new List<string>();
        if (!string.IsNullOrWhiteSpace(locale)) chain.Add(locale);
        var i = locale.IndexOf('-');
        if (i > 0) chain.Add(locale[..i]);
        if (!chain.Contains("en")) chain.Add("en");
        return chain;
    }

    private static Dictionary<(string, string), TermEntry> Index(IEnumerable<TermEntry>? terms)
    {
        var d = new Dictionary<(string, string), TermEntry>();
        if (terms is not null) foreach (var t in terms) if (t.Active) d[(t.Key, t.Locale)] = t;
        return d;
    }

    private static TermEntry? NeutralEntry(string key, string locale)
    {
        foreach (var (l, k, s, p) in TermCatalogue.Variants) if (k == key && l == locale) return new TermEntry(key, locale, s, p);
        if (locale != "en") return null;
        return TermCatalogue.ByKey.TryGetValue(key, out var d) ? new TermEntry(key, "en", d.Singular, d.Plural, d.Short, d.Description, d.Help) : null;
    }
}

/// <summary>
/// The constrained token language for text that mentions a business term: <c>[[contract]]</c>, <c>[[contract|plural]]</c>, <c>[[supplier|lower]]</c>. It is not a template language: there are no expressions, no
/// conditions, no members and no nesting. A token names a catalogue key and optionally any of the modifiers plural, short, lower, sentence, a (article) and cap (capitalise the first letter). Modifiers apply in a fixed order whatever order they are written in.
/// </summary>
public static partial class TermTokens
{
    private static readonly string[] Modifiers = ["plural", "short", "lower", "sentence", "a", "cap"];

    [GeneratedRegex(@"\[\[([a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*){0,4})((?:\|[a-z]+){0,4})\]\]", RegexOptions.NonBacktracking)]
    private static partial Regex Token();

    public static string Expand(string text, TermSet terms)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("[[", StringComparison.Ordinal)) return text;
        return Token().Replace(text, m => Render(m.Groups[1].Value, m.Groups[2].Value.Split('|', StringSplitOptions.RemoveEmptyEntries), terms) ?? m.Value);
    }

    /// <summary>Expands with the platform's neutral wording only (for text produced where no organisation is known).</summary>
    public static string ExpandNeutral(string text) => Expand(text, TermSet.Neutral());

    private static string? Render(string key, string[] modifiers, TermSet terms)
    {
        if (modifiers.Any(x => !Modifiers.Contains(x))) return null;
        var plural = modifiers.Contains("plural"); var shortForm = modifiers.Contains("short");
        var word = plural ? terms.Plural(key) : shortForm ? terms.Short(key) : terms.Singular(key);
        if (modifiers.Contains("lower")) word = word.ToLowerInvariant();
        else if (modifiers.Contains("sentence")) word = Sentence(word);
        if (modifiers.Contains("a")) word = Article(word) + " " + word;
        if (modifiers.Contains("cap") && word.Length > 0) word = char.ToUpperInvariant(word[0]) + word[1..];
        return word;
    }

    private static string Sentence(string value)
    {
        if (value.Length == 0) return value;
        var sb = new StringBuilder(value.ToLowerInvariant());
        sb[0] = char.ToUpperInvariant(value[0]);
        return sb.ToString();
    }

    /// <summary>A or an, by the first sound: vowels take 'an' except the common words that start with a vowel but sound like 'y' or 'w'; a few silent-h words take 'an'.</summary>
    public static string Article(string word)
    {
        var w = word.TrimStart().ToLowerInvariant();
        if (w.Length == 0) return "a";
        if (w.StartsWith("uni", StringComparison.Ordinal) || w.StartsWith("use", StringComparison.Ordinal) || w.StartsWith("one", StringComparison.Ordinal) || w.StartsWith("eu", StringComparison.Ordinal)) return "a";
        if (w.StartsWith("hour", StringComparison.Ordinal) || w.StartsWith("honest", StringComparison.Ordinal) || w.StartsWith("heir", StringComparison.Ordinal)) return "an";
        return "aeiou".Contains(w[0]) ? "an" : "a";
    }

    /// <summary>Problems with the tokens in a text: an unknown key, an unknown modifier, or a stray '[[' that is not a token. Used to refuse a message template at validation rather than at send time.</summary>
    public static IReadOnlyList<string> Problems(string text)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(text)) return problems;
        var rest = Token().Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            var mods = m.Groups[2].Value.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (!TermCatalogue.IsKnown(key)) problems.Add($"The term '{key}' is not one the platform knows.");
            foreach (var x in mods.Where(x => !Modifiers.Contains(x))) problems.Add($"The modifier '{x}' is not supported (use plural, short, lower, sentence, a or cap).");
            return "";
        });
        if (rest.Contains("[[", StringComparison.Ordinal) || rest.Contains("]]", StringComparison.Ordinal)) problems.Add("A '[[' or ']]' is not part of a valid term token such as [[contract]] or [[supplier|plural]].");
        return problems;
    }

    public static bool ContainsTokens(string text) => !string.IsNullOrEmpty(text) && text.Contains("[[", StringComparison.Ordinal);
}
