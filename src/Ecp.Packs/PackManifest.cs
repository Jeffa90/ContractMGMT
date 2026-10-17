using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ecp.Packs;

/// <summary>Text in one or more cultures (display names and descriptions are data, not resource strings). Resolution prefers the exact culture, then its language, then "en", then any.</summary>
public sealed record LocalisedText(IReadOnlyDictionary<string, string> Values)
{
    public string Resolve(string? culture)
    {
        if (!string.IsNullOrWhiteSpace(culture))
        {
            if (Values.TryGetValue(culture, out var exact)) return exact;
            var language = culture.Split('-')[0];
            if (Values.TryGetValue(language, out var lang)) return lang;
            var sameLanguage = Values.FirstOrDefault(v => v.Key.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase));
            if (sameLanguage.Key is not null) return sameLanguage.Value;
        }
        if (Values.TryGetValue("en", out var en)) return en;
        var english = Values.FirstOrDefault(v => v.Key.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
        return english.Key is not null ? english.Value : Values.OrderBy(v => v.Key, StringComparer.Ordinal).First().Value;
    }
}

public sealed record PackPublisher(string Id, string Name);
public sealed record PackCompatibility(string MinPlatformVersion, string? MaxPlatformVersion, bool IgnoreUnknownOptionalKinds);
public sealed record PackContentEntry(string Path, string Kind, string Scope, int SchemaVersion, bool Required, string Sha256);
public sealed record PackDependency(string PackKey, string? MinVersion, string? MaxVersion);

public static class PackScopes
{
    public const string Catalogue = "catalogue";
    public const string Tenant = "tenant";
    public const string Organisation = "organisation";
    public static readonly IReadOnlyList<string> All = [Catalogue, Tenant, Organisation];
}

public static class SignatureRequirements
{
    /// <summary>The package must carry a valid signature from a trusted key in every environment.</summary>
    public const string Required = "Required";
    /// <summary>The publisher says an unsigned copy is acceptable in Demo and Test when the environment explicitly allows synthetic packs. Production never accepts it.</summary>
    public const string AllowUnsignedSynthetic = "AllowUnsignedSynthetic";
}

/// <summary>The manifest of a package (format version 1). It is the signed object: it lists the SHA-256 of every content document.</summary>
public sealed record PackManifest(
    int FormatVersion, string PackageId, string PackKey, LocalisedText DisplayName, LocalisedText Description, string Version, PackPublisher Publisher, DateTime CreatedUtc,
    PackCompatibility Compatibility, IReadOnlyList<string> RequiredKinds, IReadOnlyList<string> OptionalKinds, IReadOnlyList<PackContentEntry> Content,
    IReadOnlyList<string> Capabilities, IReadOnlyList<PackDependency> Dependencies, string SignatureRequirement, bool Synthetic)
{
    public const int CurrentFormatVersion = 1;
}

/// <summary>Reads a manifest strictly: unknown members, wrong types, out-of-range values and unsafe identifiers are errors with a stable code and a plain message.</summary>
public static partial class PackManifestParser
{
    [GeneratedRegex(@"^[a-z][a-z0-9-]{1,48}[a-z0-9]$", RegexOptions.NonBacktracking)] private static partial Regex KeyPattern();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*){1,6}$", RegexOptions.NonBacktracking)] private static partial Regex PackageIdPattern();
    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$", RegexOptions.NonBacktracking)] private static partial Regex CulturePattern();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{1,59}$", RegexOptions.NonBacktracking)] private static partial Regex KindPattern();
    [GeneratedRegex(@"^[a-f0-9]{64}$", RegexOptions.NonBacktracking)] private static partial Regex HashPattern();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9.]{1,59}$", RegexOptions.NonBacktracking)] private static partial Regex CapabilityPattern();

    private static readonly HashSet<string> TopLevel = new(StringComparer.Ordinal)
    {
        "formatVersion", "packageId", "packKey", "displayName", "description", "version", "publisher", "createdUtc", "compatibility", "requiredKinds", "optionalKinds",
        "content", "capabilities", "dependencies", "signature", "synthetic"
    };

    public static (PackManifest? Manifest, List<PackIssue> Issues) Parse(ReadOnlySpan<byte> bytes, PackLimits limits)
    {
        var issues = new List<PackIssue>();
        JsonDocument doc;
        try { doc = CanonicalJson.Parse(bytes, limits.MaxJsonDepth); }
        catch (FormatException ex)
        {
            issues.Add(new PackIssue(PackCodes.ManifestInvalid, PackSeverity.Error, "The manifest is not valid JSON. " + ex.Message, PackPaths.Manifest, "Rebuild the package with the pack tool."));
            return (null, issues);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new PackIssue(PackCodes.ManifestInvalid, PackSeverity.Error, "The manifest must be a JSON object.", PackPaths.Manifest));
                return (null, issues);
            }
            void Bad(string field, string message) => issues.Add(new PackIssue(PackCodes.ManifestField, PackSeverity.Error, $"Manifest field '{field}': {message}", PackPaths.Manifest, "Correct the pack source and rebuild the package."));

            foreach (var p in root.EnumerateObject()) if (!TopLevel.Contains(p.Name)) Bad(p.Name, "is not a recognised field.");

            var formatVersion = Int(root, "formatVersion", Bad);
            if (formatVersion is { } fv && fv != PackManifest.CurrentFormatVersion)
            {
                issues.Add(new PackIssue(PackCodes.FormatUnsupported, PackSeverity.Error, $"The package format version {fv} is not supported by this platform (it supports {PackManifest.CurrentFormatVersion}).", PackPaths.Manifest,
                    "Use a package built for this platform version, or upgrade the platform."));
                return (null, issues);
            }

            var packageId = Str(root, "packageId", Bad, 120, PackageIdPattern(), "a lower-case dotted identifier such as org.example.pack.sample");
            var packKey = Str(root, "packKey", Bad, 50, KeyPattern(), "3 to 50 lower-case letters, digits and hyphens, starting with a letter");
            var displayName = Localised(root, "displayName", Bad, 120);
            var description = Localised(root, "description", Bad, 1000);
            var version = Str(root, "version", Bad, 40, null, null);
            if (version is not null && !SemVer.TryParse(version, out _)) Bad("version", "must be a semantic version such as 1.2.3.");
            var created = Str(root, "createdUtc", Bad, 40, null, null);
            DateTime createdUtc = default;
            if (created is not null && (!created.EndsWith('Z') || !DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out createdUtc))) Bad("createdUtc", "must be a UTC timestamp ending in Z.");

            PackPublisher? publisher = null;
            if (Obj(root, "publisher", Bad) is { } pub)
            {
                Unknown(pub, ["id", "name"], "publisher", Bad);
                var id = Str(pub, "id", Bad, 100, PackageIdPattern(), "a lower-case dotted identifier");
                var name = Str(pub, "name", Bad, 120, null, null);
                if (id is not null && name is not null) publisher = new PackPublisher(id, name);
            }

            PackCompatibility? compatibility = null;
            if (Obj(root, "compatibility", Bad) is { } comp)
            {
                Unknown(comp, ["minPlatformVersion", "maxPlatformVersion", "ignoreUnknownOptionalKinds"], "compatibility", Bad);
                var min = Str(comp, "minPlatformVersion", Bad, 40, null, null);
                if (min is not null && !SemVer.TryParse(min, out _)) Bad("compatibility.minPlatformVersion", "must be a semantic version.");
                string? max = null;
                if (comp.TryGetProperty("maxPlatformVersion", out var mx) && mx.ValueKind != JsonValueKind.Null)
                {
                    max = mx.ValueKind == JsonValueKind.String ? mx.GetString() : null;
                    if (max is null || !SemVer.TryParse(max, out _)) Bad("compatibility.maxPlatformVersion", "must be a semantic version or null.");
                }
                var ignore = comp.TryGetProperty("ignoreUnknownOptionalKinds", out var ig) && ig.ValueKind is JsonValueKind.True or JsonValueKind.False && ig.GetBoolean();
                if (min is not null) compatibility = new PackCompatibility(min, max, ignore);
            }

            var required = StringList(root, "requiredKinds", Bad, KindPattern(), 40);
            var optional = StringList(root, "optionalKinds", Bad, KindPattern(), 40);
            var capabilities = StringList(root, "capabilities", Bad, CapabilityPattern(), 40);

            var content = new List<PackContentEntry>();
            if (!root.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array) Bad("content", "is required and must be a list.");
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var index = 0;
                foreach (var item in contentElement.EnumerateArray())
                {
                    var where = $"content[{index++}]";
                    if (item.ValueKind != JsonValueKind.Object) { Bad(where, "must be an object."); continue; }
                    Unknown(item, ["path", "kind", "scope", "schemaVersion", "required", "sha256"], where, Bad);
                    var path = Str(item, "path", Bad, limits.MaxPathLength, null, null, where + ".");
                    var kind = Str(item, "kind", Bad, 60, KindPattern(), "letters and digits, starting with a letter", where + ".");
                    var scope = Str(item, "scope", Bad, 20, null, null, where + ".");
                    var schema = Int(item, "schemaVersion", Bad, where + ".");
                    var req = item.TryGetProperty("required", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : (bool?)null;
                    if (req is null) Bad(where + ".required", "is required and must be true or false.");
                    var sha = Str(item, "sha256", Bad, 64, HashPattern(), "64 lower-case hexadecimal characters", where + ".");
                    if (scope is not null && !PackScopes.All.Contains(scope)) Bad(where + ".scope", $"must be one of {string.Join(", ", PackScopes.All)}.");
                    if (path is not null)
                    {
                        var (_, reason) = PackPaths.Validate(path, limits);
                        if (reason is not null) Bad(where + ".path", reason);
                        else if (!PackPaths.IsAllowedLocation(path) || path is PackPaths.Manifest or PackPaths.Signature) Bad(where + ".path", "content documents live under content/ and end in .json.");
                        else if (!seen.Add(path)) issues.Add(new PackIssue(PackCodes.ContentDuplicate, PackSeverity.Error, "The manifest lists the same content path twice.", path, "List each content document once."));
                    }
                    if (path is not null && kind is not null && scope is not null && schema is { } sv && req is { } rq && sha is not null)
                    {
                        if (sv is < 1 or > 1000) Bad(where + ".schemaVersion", "must be between 1 and 1000.");
                        content.Add(new PackContentEntry(path, kind, scope, sv, rq, sha));
                    }
                }
                if (content.Count == 0 && issues.Count == 0) Bad("content", "must list at least one document.");
            }

            var dependencies = new List<PackDependency>();
            if (root.TryGetProperty("dependencies", out var depElement))
            {
                if (depElement.ValueKind != JsonValueKind.Array) Bad("dependencies", "must be a list.");
                else
                {
                    var index = 0;
                    foreach (var d in depElement.EnumerateArray())
                    {
                        var where = $"dependencies[{index++}]";
                        if (d.ValueKind != JsonValueKind.Object) { Bad(where, "must be an object."); continue; }
                        Unknown(d, ["packKey", "minVersion", "maxVersion"], where, Bad);
                        var key = Str(d, "packKey", Bad, 50, KeyPattern(), "a pack key", where + ".");
                        var lo = OptionalString(d, "minVersion"); var hi = OptionalString(d, "maxVersion");
                        if (lo is not null && !SemVer.TryParse(lo, out _)) Bad(where + ".minVersion", "must be a semantic version.");
                        if (hi is not null && !SemVer.TryParse(hi, out _)) Bad(where + ".maxVersion", "must be a semantic version.");
                        if (key is not null) dependencies.Add(new PackDependency(key, lo, hi));
                    }
                }
            }

            string? requirement = null;
            if (Obj(root, "signature", Bad) is { } sig)
            {
                Unknown(sig, ["requirement"], "signature", Bad);
                requirement = Str(sig, "requirement", Bad, 40, null, null, "signature.");
                if (requirement is not null && requirement is not (SignatureRequirements.Required or SignatureRequirements.AllowUnsignedSynthetic))
                    Bad("signature.requirement", $"must be {SignatureRequirements.Required} or {SignatureRequirements.AllowUnsignedSynthetic}.");
            }
            bool? synthetic = root.TryGetProperty("synthetic", out var syn) && syn.ValueKind is JsonValueKind.True or JsonValueKind.False ? syn.GetBoolean() : null;
            if (synthetic is null) Bad("synthetic", "is required and must be true or false.");

            // Consistency between the kind lists and the content entries.
            foreach (var k in required) if (optional.Contains(k)) Bad("requiredKinds", $"kind '{k}' is also listed as optional.");
            foreach (var entry in content)
            {
                if (!required.Contains(entry.Kind) && !optional.Contains(entry.Kind))
                    issues.Add(new PackIssue(PackCodes.KindListMismatch, PackSeverity.Error, $"The content kind '{entry.Kind}' is in neither requiredKinds nor optionalKinds.", entry.Path, "List every content kind as required or optional."));
                else if (required.Contains(entry.Kind) != entry.Required)
                    issues.Add(new PackIssue(PackCodes.KindListMismatch, PackSeverity.Error, $"The content entry for '{entry.Kind}' disagrees with the kind lists about whether it is required.", entry.Path, "Make the entry's 'required' flag match requiredKinds."));
            }
            foreach (var k in required)
                if (!content.Any(c => c.Kind == k)) issues.Add(new PackIssue(PackCodes.RequiredKindMissing, PackSeverity.Error, $"The package declares the required kind '{k}' but has no content for it.", null, "Add the document or remove the kind from requiredKinds."));

            if (issues.Any(i => i.IsError) || packageId is null || packKey is null || displayName is null || description is null || version is null || publisher is null || compatibility is null
                || formatVersion is null || requirement is null || synthetic is null || created is null)
                return (null, issues);
            return (new PackManifest(formatVersion.Value, packageId, packKey, displayName, description, version, publisher, createdUtc, compatibility, required, optional, content, capabilities, dependencies, requirement, synthetic.Value), issues);
        }
    }

    private static string? OptionalString(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Unknown(JsonElement o, string[] allowed, string where, Action<string, string> bad)
    {
        foreach (var p in o.EnumerateObject()) if (!allowed.Contains(p.Name)) bad($"{where}.{p.Name}", "is not a recognised field.");
    }

    private static JsonElement? Obj(JsonElement parent, string name, Action<string, string> bad)
    {
        if (!parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) { bad(name, "is required and must be an object."); return null; }
        return v;
    }

    private static string? Str(JsonElement o, string name, Action<string, string> bad, int max, Regex? pattern, string? expectation, string prefix = "")
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) { bad(prefix + name, "is required and must be text."); return null; }
        var s = v.GetString()!;
        if (s.Length == 0 || s.Length > max) { bad(prefix + name, $"must be 1 to {max} characters."); return null; }
        if (s.Any(char.IsControl)) { bad(prefix + name, "must not contain control characters."); return null; }
        if (pattern is not null && !pattern.IsMatch(s)) { bad(prefix + name, $"must be {expectation}."); return null; }
        return s;
    }

    private static int? Int(JsonElement o, string name, Action<string, string> bad, string prefix = "")
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n)) { bad(prefix + name, "is required and must be a whole number."); return null; }
        return n;
    }

    private static List<string> StringList(JsonElement o, string name, Action<string, string> bad, Regex pattern, int maxItems)
    {
        var list = new List<string>();
        if (!o.TryGetProperty(name, out var v)) return list;
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > maxItems) { bad(name, $"must be a list of at most {maxItems} items."); return list; }
        foreach (var item in v.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (s is null || !pattern.IsMatch(s)) { bad(name, $"contains an invalid item{(s is null ? "" : " ('" + (s.Length > 30 ? s[..30] : s) + "')")}."); continue; }
            if (list.Contains(s)) { bad(name, $"lists '{s}' more than once."); continue; }
            list.Add(s);
        }
        return list;
    }

    private static LocalisedText? Localised(JsonElement o, string name, Action<string, string> bad, int maxLength)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) { bad(name, "is required and must map a culture to text."); return null; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            if (!CulturePattern().IsMatch(p.Name)) { bad($"{name}.{p.Name}", "is not a culture such as en or en-AU."); continue; }
            var text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : "";
            if (text.Length == 0 || text.Length > maxLength || text.Any(c => char.IsControl(c) && c is not '\n')) { bad($"{name}.{p.Name}", $"must be 1 to {maxLength} characters of plain text."); continue; }
            values[p.Name] = text;
        }
        if (values.Count == 0) { bad(name, "needs at least one culture."); return null; }
        return new LocalisedText(values);
    }
}
