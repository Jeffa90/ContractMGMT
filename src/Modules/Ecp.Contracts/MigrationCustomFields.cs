using Ecp.Metadata;

namespace Ecp.Contracts;

/// <summary>What a migration may do with one configured field of an organisation. A field a migration cannot fill (calculated, person, file reference) says why.</summary>
public sealed record MigrationFieldRule(string Key, string Label, string Type, bool IsRetired, bool IsCurrent, bool Supported, string? UnsupportedReason);

public sealed record CustomValueProblem(string Key, string Code, string Message);

/// <summary>The outcome of checking migrated values: each acceptable value in its canonical stored text, and every problem found (all of them, not the first).</summary>
public sealed record CustomValueCheck(IReadOnlyDictionary<string, string?> Canonical, IReadOnlyList<CustomValueProblem> Problems)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Checks source values against the organisation's published field definitions, with no database access: it is built once per batch and used for every row. A value that is not a defined code (or label) of a
/// choice or lookup is a problem, never a guess; a retired or not-yet-effective field takes no new values; a value over a field's limit is refused, never cut.
/// </summary>
public interface IMigrationCustomChecker
{
    IReadOnlyCollection<string> Keys { get; }
    /// <summary>The release of the field definitions the check used (recorded with the batch, so a later change is visible).</summary>
    int Release { get; }
    MigrationFieldRule? Rule(string key);
    CustomValueCheck Check(IReadOnlyDictionary<string, string?> raw);
}

internal sealed class MigrationCustomChecker(CustomFieldSet set, DateOnly today) : IMigrationCustomChecker
{
    public IReadOnlyCollection<string> Keys => set.Fields.Select(f => f.Key).ToList();
    public int Release => set.ReleaseNumber;

    public MigrationFieldRule? Rule(string key)
    {
        if (set.Find(key) is not { } d) return null;
        var reason = d.Type switch
        {
            FieldTypes.Calculated => "it is calculated, so nothing can be loaded into it",
            FieldTypes.Person => "a person cannot be matched from a source file in this release; load the value into a staging field and enter it afterwards",
            FieldTypes.FileReference => "a file reference points at a document of the contract; attach documents through the document step",
            _ => null
        };
        return new MigrationFieldRule(d.Key, d.Label, d.Type, d.IsRetired, FieldRules.IsCurrent(d, today), reason is null, reason);
    }

    public CustomValueCheck Check(IReadOnlyDictionary<string, string?> raw)
    {
        var canonical = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<CustomValueProblem>();
        foreach (var (key, value) in raw)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var d = set.Find(key);
            var rule = Rule(key);
            if (d is null || rule is null) { problems.Add(new(key, "MIG-E020", $"'{key}' is not an additional field of this organisation.")); continue; }
            if (d.IsRetired) { problems.Add(new(key, "MIG-E021", $"{d.Label} is retired, so it takes no new values.")); continue; }
            if (!rule.IsCurrent) { problems.Add(new(key, "MIG-E021", $"{d.Label} is not in force today, so it takes no new values.")); continue; }
            if (!rule.Supported) { problems.Add(new(key, "MIG-E025", $"{d.Label} cannot be loaded: {rule.UnsupportedReason}.")); continue; }
            var items = FieldTypes.IsMulti(d.Type) ? value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [value];
            // Choices and lookups: the source value must be a code or a label that is defined and active; the stored value is always the code.
            if (FieldTypes.UsesList(d.Type))
            {
                var codes = new List<string>(); var bad = false;
                foreach (var item in items)
                {
                    var choice = set.FindChoice(d, item);
                    if (choice is null) { problems.Add(new(key, "MIG-E022", $"'{item}' is not a choice of {d.Label}. Map it with a lookup entry to one of its codes.")); bad = true; }
                    else if (!choice.Active) { problems.Add(new(key, "MIG-E022", $"The choice '{choice.Label}' of {d.Label} is no longer offered, so it cannot be loaded.")); bad = true; }
                    else codes.Add(choice.Code);
                }
                if (bad) continue;
                items = codes.ToArray();
            }
            var parsed = ValueCodec.Parse(d, set, items);
            if (!parsed.IsValid) { foreach (var e in parsed.Errors) problems.Add(new(key, "MIG-E023", e)); continue; }
            canonical[d.Key] = parsed.IsEmpty ? null : string.Join("; ", ValueCodec.ToRaw(d, parsed.Values));
        }
        return new CustomValueCheck(canonical, problems);
    }
}
