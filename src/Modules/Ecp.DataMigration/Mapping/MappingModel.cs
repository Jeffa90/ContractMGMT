using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.DataMigration.Formats;

namespace Ecp.DataMigration.Mapping;

public sealed record FieldMapping(string SourceColumn, string TargetField, string Transformation, string? DefaultValue, bool Required, string? LookupMapKey, string? Validation, string? Notes);

public sealed record LookupEntry(string LookupMapKey, string SourceValue, string TargetCode, string? Notes);

/// <summary>Profile-level switches that are data, not code: whether unknown categories are created, the file naming rule, and which extension types legitimately have no end date.</summary>
public sealed record ProfileOptions(bool CreateMissingCategories = false, string? NamingRule = null, IReadOnlyList<string>? OpenEndedExtensionTypes = null);

public sealed record MappingDefinition(IReadOnlyList<FieldMapping> Fields, IReadOnlyList<LookupEntry> Lookups, ProfileOptions Options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static MappingDefinition Empty { get; } = new([], [], new ProfileOptions());

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static MappingDefinition FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<MappingDefinition>(json, Json) ?? Empty; }
        catch (JsonException) { return Empty; }
    }

    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    public bool Maps(string targetField) => Fields.Any(f => f.TargetField.Equals(targetField, StringComparison.Ordinal));
}

/// <summary>The target fields a mapping may name, and the closed catalogue of transformations (SOURCE_TO_TARGET_MAPPING). Nothing here is executable: a profile is data.</summary>
public static class MappingCatalogue
{
    public static readonly IReadOnlyList<string> CoreFields =
    [
        "sourceRecordId", "agreementReference", "contractTitle", "description", "status", "classification", "startDate", "effectiveDate", "registeredDate", "reviewDate", "endDate",
        "currencyCode", "estimatedValue", "extensionType", "legalReviewRequired"
    ];

    public static readonly IReadOnlyList<string> EntityFields = ["supplier.name", "supplier.vendorNumber", "category.name", "subcategory.name"];

    /// <summary>The fields every contract load must map.</summary>
    public static readonly IReadOnlyList<string> RequiredTargets = ["sourceRecordId", "agreementReference", "contractTitle", "status", "supplier.name", "category.name"];

    public static readonly IReadOnlyList<string> Transformations =
    [
        "Trim", "NormaliseWhitespace", "Upper", "Lower", "TitleCase", "ParseDate", "ParseDateTime", "ParseDecimal", "ParseInteger", "MapBoolean", "Concat", "Split", "FixedDefault", "LookupMap"
    ];

    public static readonly IReadOnlyList<string> ParameterKeys =
        ["MaxLength", "MinLength", "Length", "Min", "Max", "Format", "TimeZone", "Culture", "True", "False", "Columns", "Separator", "Index", "OnMissing", "Default", "ThousandsSeparator", "DecimalSeparator"];

    public static bool IsKnownTarget(string target) => CoreFields.Contains(target, StringComparer.Ordinal) || EntityFields.Contains(target, StringComparer.Ordinal)
        || (target.StartsWith("staging.", StringComparison.Ordinal) && Regex.IsMatch(target, "^staging\\.[A-Za-z][A-Za-z0-9]{0,40}$", RegexOptions.NonBacktracking));
}

/// <summary>The parameters carried in a mapping row's Validation column, for example <c>Format=yyyy-MM-dd;TimeZone=organisation</c> or <c>True=Yes;Y;1|False=No;N;0</c>.</summary>
public sealed class MappingParameters
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public static MappingParameters Parse(string? text)
    {
        var p = new MappingParameters();
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (var segment in text.Split('|'))
            {
                string? key = null; var value = new StringBuilder();
                void Flush() { if (key is not null) p._values[key] = value.ToString(); }
                foreach (var token in segment.Split(';'))
                {
                    var eq = token.IndexOf('=');
                    if (eq > 0)
                    {
                        Flush(); key = token[..eq].Trim(); value.Clear(); value.Append(token[(eq + 1)..]);
                        if (!MappingCatalogue.ParameterKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) problems.Add($"'{key}' is not a known parameter.");
                    }
                    else if (key is not null) value.Append(';').Append(token);
                    else if (token.Trim().Length > 0) problems.Add($"'{token.Trim()}' is not in the form name=value.");
                }
                Flush();
            }
        }
        p.Problems = problems;
        return p;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
    public bool Has(string key) => _values.ContainsKey(key);
    public int? GetInt(string key) => _values.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : null;
    public decimal? GetDecimal(string key) => _values.TryGetValue(key, out var v) && decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
    public IReadOnlyList<string> GetList(string key) => _values.TryGetValue(key, out var v) ? v.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];
}

public sealed record DefinitionIssue(bool IsError, string Field, string Message);

/// <summary>Checks a definition before it can be published: every problem is reported together, with the mapping row it concerns.</summary>
public static class MappingDefinitionValidator
{
    private static readonly HashSet<string> NeedsFormat = new(StringComparer.Ordinal) { "ParseDate", "ParseDateTime" };

    public static IReadOnlyList<DefinitionIssue> Validate(MappingDefinition d)
    {
        var issues = new List<DefinitionIssue>();
        if (d.Fields.Count == 0) issues.Add(new(true, "$", "The profile has no field mappings."));
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < d.Fields.Count; i++)
        {
            var f = d.Fields[i]; var where = $"mapping {i + 1} ({f.SourceColumn} → {f.TargetField})";
            if (string.IsNullOrWhiteSpace(f.SourceColumn)) issues.Add(new(true, where, "The source column is empty."));
            if (f.TargetField.StartsWith("custom.", StringComparison.Ordinal))
                issues.Add(new(true, where, "Custom fields are not available in this release (Future). Map the value to a core field or to a staging field."));
            else if (!MappingCatalogue.IsKnownTarget(f.TargetField))
                issues.Add(new(true, where, $"'{f.TargetField}' is not a field a migration can set. Core fields are: {string.Join(", ", MappingCatalogue.CoreFields)}; entity fields: {string.Join(", ", MappingCatalogue.EntityFields)}; or staging.<name>."));
            else if (!seenTargets.Add(f.TargetField)) issues.Add(new(true, where, $"The target field '{f.TargetField}' is mapped more than once."));

            var parameters = MappingParameters.Parse(f.Validation);
            foreach (var problem in parameters.Problems) issues.Add(new(true, where, problem));
            var chain = Chain(f.Transformation);
            if (chain.Count == 0) issues.Add(new(true, where, "Name a transformation (Trim is the plain copy)."));
            foreach (var t in chain)
            {
                if (!MappingCatalogue.Transformations.Contains(t, StringComparer.Ordinal))
                { issues.Add(new(true, where, $"'{t}' is not a safe transformation. Choose from: {string.Join(", ", MappingCatalogue.Transformations)}.")); continue; }
                if (NeedsFormat.Contains(t) && string.IsNullOrWhiteSpace(parameters.Get("Format")))
                    issues.Add(new(true, where, $"{t} needs an explicit Format (for example Format=yyyy-MM-dd). The wizard never guesses a day/month order."));
                if (t == "LookupMap")
                {
                    if (string.IsNullOrWhiteSpace(f.LookupMapKey)) issues.Add(new(true, where, "LookupMap needs a LookupMapKey."));
                    else if (!d.Lookups.Any(l => l.LookupMapKey.Equals(f.LookupMapKey, StringComparison.OrdinalIgnoreCase)))
                        issues.Add(new(false, where, $"No lookup entries exist yet for the key '{f.LookupMapKey}'. Every value will be reported as unmapped."));
                    if (parameters.Get("OnMissing") is { } m && m is not ("Error" or "Default" or "Skip")) issues.Add(new(true, where, "OnMissing must be Error, Default or Skip."));
                    if (parameters.Get("OnMissing") == "Default" && !parameters.Has("Default")) issues.Add(new(true, where, "OnMissing=Default needs a Default value."));
                }
                if (t == "Concat" && parameters.GetList("Columns").Count == 0) issues.Add(new(true, where, "Concat needs Columns=A;B (the columns to join)."));
                if (t == "Split" && (!parameters.Has("Separator") || parameters.GetInt("Index") is null)) issues.Add(new(true, where, "Split needs Separator and Index."));
            }
            if (f.Required && string.IsNullOrWhiteSpace(f.SourceColumn)) issues.Add(new(true, where, "A required mapping needs a source column."));
        }
        foreach (var target in MappingCatalogue.RequiredTargets)
            if (!d.Maps(target)) issues.Add(new(true, "$", $"The profile must map '{target}'."));
        foreach (var group in d.Lookups.GroupBy(l => (l.LookupMapKey.ToUpperInvariant(), l.SourceValue.Trim().ToUpperInvariant())).Where(g => g.Count() > 1))
            issues.Add(new(true, $"lookup {group.First().LookupMapKey}", $"The source value '{group.First().SourceValue}' appears more than once, so its target is ambiguous."));
        if (!string.IsNullOrWhiteSpace(d.Options.NamingRule))
        {
            var rule = NamingRule.Compile(d.Options.NamingRule, out var problem);
            if (rule is null) issues.Add(new(true, "naming rule", problem ?? "The naming rule is not valid."));
        }
        return issues;
    }

    public static IReadOnlyList<string> Chain(string? transformation)
        => (transformation ?? "").Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// The configured naming rule for attachment matching (MATCH method 3). It is a regular expression evaluated by the non-backtracking engine with a timeout, so a hostile
/// pattern cannot stall a batch (ASM-13). It names the value to extract with a group called <c>agreementReference</c> or <c>sourceRecordId</c>.
/// </summary>
public static class NamingRule
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    public static Regex? Compile(string pattern, out string? problem, TimeSpan? timeout = null)
    {
        problem = null;
        if (pattern.Length > 300) { problem = "The naming rule is longer than 300 characters."; return null; }
        try
        {
            var regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, timeout ?? Timeout);
            var names = regex.GetGroupNames();
            if (!names.Contains("agreementReference") && !names.Contains("sourceRecordId")) { problem = "The naming rule must contain a group named agreementReference or sourceRecordId, for example (?<agreementReference>[A-Z0-9-]+)/."; return null; }
            return regex;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { problem = "The naming rule is not a valid pattern for the safe (non-backtracking) engine: " + ex.Message; return null; }
    }
}

/// <summary>Reads and writes mapping definitions as the CSV files used in the samples and in the wizard.</summary>
public static class ProfileCsv
{
    public static IReadOnlyList<FieldMapping> ParseFieldMappings(Stream csv, out IReadOnlyList<string> problems)
    {
        var table = CsvReader.Read(csv);
        var issues = new List<string>();
        int Col(string name) => table.Headers.ToList().FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        var cSource = Col("SourceColumn"); var cTarget = Col("TargetField");
        if (cSource < 0 || cTarget < 0) throw new SourceFormatException("MIG-E014", "A field-mapping file needs the columns SourceColumn and TargetField.");
        int cTrans = Col("Transformation"), cDef = Col("DefaultValue"), cReq = Col("Required"), cLookup = Col("LookupMapKey"), cVal = Col("Validation"), cNotes = Col("Notes");
        string At(SourceRow r, int c) => c >= 0 && c < r.Fields.Count ? r.Fields[c].Trim() : "";
        var list = new List<FieldMapping>();
        foreach (var row in table.Rows)
        {
            if (row.Ragged) { issues.Add($"Row {row.RowNumber} has a different number of values from the header and was not read."); continue; }
            var req = At(row, cReq);
            var required = req.Equals("Yes", StringComparison.OrdinalIgnoreCase) || req.Equals("Y", StringComparison.OrdinalIgnoreCase) || req.Equals("True", StringComparison.OrdinalIgnoreCase) || req == "1";
            list.Add(new FieldMapping(At(row, cSource), At(row, cTarget), At(row, cTrans).Length == 0 ? "Trim" : At(row, cTrans), Blank(At(row, cDef)), required,
                Blank(At(row, cLookup)), Blank(At(row, cVal)), Blank(At(row, cNotes))));
        }
        problems = issues;
        return list;
    }

    public static IReadOnlyList<LookupEntry> ParseLookups(Stream csv, out IReadOnlyList<string> problems)
    {
        var table = CsvReader.Read(csv);
        var issues = new List<string>();
        int Col(string name) => table.Headers.ToList().FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int cKey = Col("LookupMapKey"), cSource = Col("SourceValue"), cTarget = Col("TargetCode"), cNotes = Col("Notes");
        if (cKey < 0 || cSource < 0 || cTarget < 0) throw new SourceFormatException("MIG-E014", "A lookup file needs the columns LookupMapKey, SourceValue and TargetCode.");
        string At(SourceRow r, int c) => c >= 0 && c < r.Fields.Count ? r.Fields[c].Trim() : "";
        var list = new List<LookupEntry>();
        foreach (var row in table.Rows)
        {
            if (row.Ragged) { issues.Add($"Row {row.RowNumber} has a different number of values from the header and was not read."); continue; }
            if (At(row, cKey).Length == 0 || At(row, cSource).Length == 0 || At(row, cTarget).Length == 0) { issues.Add($"Row {row.RowNumber} is missing a key, a source value or a target code."); continue; }
            list.Add(new LookupEntry(At(row, cKey), At(row, cSource), At(row, cTarget), Blank(At(row, cNotes))));
        }
        problems = issues;
        return list;
    }

    public static byte[] ExportFieldMappings(MappingDefinition d)
        => CsvWriter.Write(new[] { (IReadOnlyList<object?>)new object?[] { "SourceColumn", "TargetField", "Transformation", "DefaultValue", "Required", "LookupMapKey", "Validation", "Notes" } }
            .Concat(d.Fields.Select(f => (IReadOnlyList<object?>)new object?[] { f.SourceColumn, f.TargetField, f.Transformation, f.DefaultValue, f.Required ? "Yes" : "No", f.LookupMapKey, f.Validation, f.Notes })));

    public static byte[] ExportLookups(MappingDefinition d)
        => CsvWriter.Write(new[] { (IReadOnlyList<object?>)new object?[] { "LookupMapKey", "SourceValue", "TargetCode", "Notes" } }
            .Concat(d.Lookups.Select(l => (IReadOnlyList<object?>)new object?[] { l.LookupMapKey, l.SourceValue, l.TargetCode, l.Notes })));

    private static string? Blank(string s) => s.Length == 0 ? null : s;
}
