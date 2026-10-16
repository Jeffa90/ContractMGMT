using System.Globalization;
using System.Text.RegularExpressions;

namespace Ecp.DataMigration.Mapping;

/// <summary>A message about one value. Codes are the stable catalogue (MIGRATION_SUPPLEMENT section 8 and the additions recorded in docs/MIGRATION.md).</summary>
public sealed record RowMessage(bool IsError, string Code, string Field, string Text);

/// <summary>What happened to one mapping row for one source row: the original, the transformed value and whether a default, a change or a problem was involved. Used for the preview.</summary>
public sealed record MappingTrace(string SourceColumn, string TargetField, string Original, string Transformed, bool Changed, bool DefaultApplied, bool Failed);

public sealed class MappedRow
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public List<RowMessage> Messages { get; } = [];
    public List<MappingTrace> Trace { get; } = [];
    public bool HasErrors => Messages.Any(m => m.IsError);
}

/// <summary>Context for the transformations that depend on the organisation: its time zone and whether the source is a spreadsheet (whose date cells arrive as ISO text).</summary>
public sealed record MappingContext(TimeZoneInfo OrganisationTimeZone, bool SourceIsSpreadsheet);

/// <summary>
/// Applies a mapping definition to a source row. Every transformation is from a closed catalogue; nothing is evaluated as code. A value longer than its limit is an
/// error (MIG-E009) and is never cut. A value that is not understood is an error, never a guess.
/// </summary>
public static class MappingEngine
{
    private static readonly string[] DefaultTrue = ["Yes", "Y", "True", "T", "1"];
    private static readonly string[] DefaultFalse = ["No", "N", "False", "F", "0"];

    public static MappedRow MapRow(MappingDefinition definition, IReadOnlyDictionary<string, string> source, MappingContext context)
    {
        var lookups = definition.Lookups.GroupBy(l => l.LookupMapKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.SourceValue.Trim(), x => x.TargetCode, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var result = new MappedRow();
        foreach (var mapping in definition.Fields)
        {
            var parameters = MappingParameters.Parse(mapping.Validation);
            source.TryGetValue(mapping.SourceColumn, out var rawValue);
            var original = rawValue ?? "";
            var value = original;
            var defaultApplied = false; var failed = false;
            var errorsBefore = result.Messages.Count(m => m.IsError);

            var chain = MappingDefinitionValidator.Chain(mapping.Transformation);
            var concatenates = chain.Contains("Concat");
            if (concatenates)
            {
                var parts = parameters.GetList("Columns").Select(c => source.TryGetValue(c, out var v) ? v.Trim() : "").Where(v => v.Length > 0);
                value = string.Join(parameters.Get("Separator") ?? " ", parts);
                original = string.Join(" | ", parameters.GetList("Columns").Select(c => source.TryGetValue(c, out var v) ? v : ""));
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (!string.IsNullOrWhiteSpace(mapping.DefaultValue))
                {
                    value = mapping.DefaultValue!; defaultApplied = true;
                    result.Messages.Add(new(false, "MIG-W002", mapping.TargetField, $"The default '{value}' was applied to {mapping.TargetField} because the source value is empty."));
                }
                else value = "";
            }

            if (value.Length > 0)
            {
                foreach (var step in chain.Where(s => s != "Concat" && s != "FixedDefault"))
                {
                    var (next, error) = Apply(step, value, mapping, parameters, lookups, context, source);
                    if (error is not null) { result.Messages.Add(new(true, error.Value.Code, mapping.TargetField, error.Value.Text)); failed = true; value = ""; break; }
                    if (step is "Trim" or "NormaliseWhitespace" or "Upper" or "Lower" or "TitleCase" && !string.Equals(next, value, StringComparison.Ordinal))
                        result.Messages.Add(new(false, "MIG-W001", mapping.TargetField, $"The value of {mapping.SourceColumn} was normalised ({step})."));
                    value = next;
                }
            }

            if (!failed && value.Length > 0) failed = !Check(mapping, parameters, value, result);
            if (!failed && value.Length == 0 && mapping.Required)
            {
                result.Messages.Add(new(true, "MIG-E001", mapping.TargetField, $"{mapping.SourceColumn} is required and has no value."));
                failed = true;
            }
            var transformed = value;
            result.Trace.Add(new MappingTrace(mapping.SourceColumn, mapping.TargetField, original, transformed, !string.Equals(original, transformed, StringComparison.Ordinal), defaultApplied,
                failed || result.Messages.Count(m => m.IsError) > errorsBefore));
            if (!failed) result.Values[mapping.TargetField] = value;
        }
        return result;
    }

    private static bool Check(FieldMapping mapping, MappingParameters p, string value, MappedRow result)
    {
        var ok = true;
        if (p.GetInt("MaxLength") is { } max && value.Length > max)
        { result.Messages.Add(new(true, "MIG-E009", mapping.TargetField, $"{mapping.SourceColumn} is {value.Length} characters; the most allowed is {max}. The value is not shortened.")); ok = false; }
        if (p.GetInt("MinLength") is { } min && value.Length < min)
        { result.Messages.Add(new(true, "MIG-E015", mapping.TargetField, $"{mapping.SourceColumn} is {value.Length} characters; at least {min} are needed.")); ok = false; }
        if (p.GetInt("Length") is { } exact && value.Length != exact)
        { result.Messages.Add(new(true, "MIG-E015", mapping.TargetField, $"{mapping.SourceColumn} must be exactly {exact} characters.")); ok = false; }
        if ((p.Has("Min") || p.Has("Max")) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            if (p.GetDecimal("Min") is { } lo && number < lo) { result.Messages.Add(new(true, "MIG-E015", mapping.TargetField, $"{mapping.SourceColumn} must not be less than {lo}.")); ok = false; }
            if (p.GetDecimal("Max") is { } hi && number > hi) { result.Messages.Add(new(true, "MIG-E015", mapping.TargetField, $"{mapping.SourceColumn} must not be more than {hi}.")); ok = false; }
        }
        return ok;
    }

    private static (string Value, (string Code, string Text)? Error) Apply(string step, string value, FieldMapping mapping, MappingParameters p,
        Dictionary<string, Dictionary<string, string>> lookups, MappingContext context, IReadOnlyDictionary<string, string> source)
    {
        var culture = CultureFor(p);
        switch (step)
        {
            case "Trim": return (value.Trim(), null);
            case "NormaliseWhitespace": return (Regex.Replace(value, "\\s+", " ", RegexOptions.NonBacktracking).Trim(), null);
            case "Upper": return (value.ToUpper(culture), null);
            case "Lower": return (value.ToLower(culture), null);
            case "TitleCase": return (culture.TextInfo.ToTitleCase(value.ToLower(culture)), null);

            case "ParseDate":
            {
                var text = value.Trim();
                var format = p.Get("Format")!;
                if (DateOnly.TryParseExact(text, format, culture, DateTimeStyles.None, out var d)) return (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
                if (context.SourceIsSpreadsheet && TryIso(text, out var iso)) return (iso.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
                return ("", ("MIG-E002", $"'{value}' is not a date in the format {format}."));
            }
            case "ParseDateTime":
            {
                var text = value.Trim();
                var format = p.Get("Format")!;
                DateTime local;
                if (!DateTime.TryParseExact(text, format, culture, DateTimeStyles.None, out local))
                {
                    if (context.SourceIsSpreadsheet && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso) && text.Length >= 10 && text[4] == '-') local = iso;
                    else return ("", ("MIG-E002", $"'{value}' is not a date and time in the format {format}."));
                }
                TimeZoneInfo zone;
                var zoneName = p.Get("TimeZone");
                if (string.IsNullOrWhiteSpace(zoneName) || zoneName.Equals("organisation", StringComparison.OrdinalIgnoreCase)) zone = context.OrganisationTimeZone;
                else if (zoneName.Equals("UTC", StringComparison.OrdinalIgnoreCase)) zone = TimeZoneInfo.Utc;
                else
                {
                    try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneName); }
                    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return ("", ("MIG-E002", $"The time zone '{zoneName}' is not known.")); }
                }
                local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) return ("", ("MIG-E002", $"'{value}' is not a single clear instant in {zone.Id} (it falls in a daylight-saving change)."));
                var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                return (utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), null);
            }
            case "ParseDecimal":
            {
                var text = value.Trim();
                if (p.Get("ThousandsSeparator") is { Length: > 0 } t) text = text.Replace(t, "");
                if (p.Get("DecimalSeparator") is { Length: > 0 } ds && ds != ".") text = text.Replace(".", "").Replace(ds, ".");
                var inv = p.Has("ThousandsSeparator") || p.Has("DecimalSeparator") ? CultureInfo.InvariantCulture : culture;
                if (decimal.TryParse(text, NumberStyles.Number, inv, out var d)) return (d.ToString(CultureInfo.InvariantCulture), null);
                return ("", ("MIG-E003", $"'{value}' is not a number."));
            }
            case "ParseInteger":
                return long.TryParse(value.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, culture, out var n)
                    ? (n.ToString(CultureInfo.InvariantCulture), null) : ("", ("MIG-E003", $"'{value}' is not a whole number."));
            case "MapBoolean":
            {
                var yes = p.Has("True") ? p.GetList("True") : DefaultTrue; var no = p.Has("False") ? p.GetList("False") : DefaultFalse;
                var text = value.Trim();
                if (yes.Contains(text, StringComparer.OrdinalIgnoreCase)) return ("true", null);
                if (no.Contains(text, StringComparer.OrdinalIgnoreCase)) return ("false", null);
                return ("", ("MIG-E004", $"'{value}' is not one of the accepted yes/no values."));
            }
            case "Split":
            {
                var parts = value.Split(p.Get("Separator")!);
                var index = p.GetInt("Index")!.Value;
                return index >= 0 && index < parts.Length ? (parts[index].Trim(), null) : ("", ("MIG-E001", $"The value has no part {index}."));
            }
            case "LookupMap":
            {
                var key = mapping.LookupMapKey ?? "";
                var text = value.Trim();
                if (lookups.TryGetValue(key, out var map) && map.TryGetValue(text, out var target)) return (target, null);
                return (p.Get("OnMissing") ?? "Error") switch
                {
                    "Default" => (p.Get("Default") ?? "", null),
                    "Skip" => ("", null),
                    _ => ("", ("MIG-E004", $"'{value}' is not in the '{key}' lookup map."))
                };
            }
            default: return (value, null);
        }
    }

    private static bool TryIso(string text, out DateOnly date)
    {
        if (text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        date = default; return false;
    }

    private static CultureInfo CultureFor(MappingParameters p)
    {
        var name = p.Get("Culture");
        if (string.IsNullOrWhiteSpace(name)) return CultureInfo.InvariantCulture;
        try { return CultureInfo.GetCultureInfo(name); } catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }
}
