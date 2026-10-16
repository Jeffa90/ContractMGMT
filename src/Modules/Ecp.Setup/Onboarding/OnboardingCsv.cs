using System.Text;
using Ecp.Identity.Domain;

namespace Ecp.Setup.Onboarding;

public sealed record CsvIssue(int Line, string Message);

public sealed record CsvResult<T>(IReadOnlyList<T> Items, IReadOnlyList<CsvIssue> Issues)
{
    public bool IsClean => Issues.Count == 0;
}

/// <summary>
/// Plain CSV import and export for the wizard's lists (taxonomy import). Comma separated, optional double quotes, an optional header row, at most
/// <see cref="MaxLines"/> lines of <see cref="MaxLineLength"/> characters. Multi-valued cells use ';'. Nothing is evaluated: cells are text.
/// </summary>
public static class OnboardingCsv
{
    public const int MaxLines = 5000;
    public const int MaxLineLength = 1000;

    public static IReadOnlyList<string> SplitLine(string line)
    {
        var cells = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; }
                else sb.Append(ch);
            }
            else if (ch == '"' && sb.Length == 0) quoted = true;
            else if (ch == ',') { cells.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        if (quoted) throw new FormatException("A quoted value is not closed.");
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    public static string Cell(string value) => value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    public static string Line(params string[] cells) => string.Join(',', cells.Select(Cell));
    private static IReadOnlyList<string> Multi(string cell) => cell.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string Join(IEnumerable<string> values) => string.Join(';', values);

    private static CsvResult<T> Read<T>(string? text, string firstHeader, int minCells, Func<IReadOnlyList<string>, T> build, string expected)
    {
        var items = new List<T>(); var issues = new List<CsvIssue>();
        if (string.IsNullOrWhiteSpace(text)) return new(items, issues);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length > MaxLines) return new(items, [new(0, $"Too many lines: at most {MaxLines} are accepted.")]);
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i]; var n = i + 1;
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#')) continue;
            if (raw.Length > MaxLineLength) { issues.Add(new(n, $"The line is longer than {MaxLineLength} characters.")); continue; }
            IReadOnlyList<string> cells;
            try { cells = SplitLine(raw); }
            catch (FormatException ex) { issues.Add(new(n, ex.Message)); continue; }
            if (items.Count == 0 && issues.Count == 0 && cells[0].Equals(firstHeader, StringComparison.OrdinalIgnoreCase)) continue; // header row
            if (cells.Count < minCells) { issues.Add(new(n, $"Expected {expected}.")); continue; }
            try { items.Add(build(cells)); }
            catch (Exception ex) when (ex is ArgumentException or FormatException) { issues.Add(new(n, ex.Message)); }
        }
        return new(items, issues);
    }

    private static string At(IReadOnlyList<string> c, int i) => i < c.Count ? c[i] : "";
    private static string? Opt(IReadOnlyList<string> c, int i) => string.IsNullOrWhiteSpace(At(c, i)) ? null : At(c, i);

    private static ViewerKind ParseKind(string cell)
        => Enum.TryParse<ViewerKind>(cell, ignoreCase: true, out var k) && Enum.IsDefined(k) ? k : throw new ArgumentException($"'{cell}' is not a viewer kind (AllContracts, Category, BusinessUnit, Site, Restricted).");

    private static bool ParseBool(string cell)
        => cell.Trim().ToLowerInvariant() switch { "" or "yes" or "y" or "true" or "1" => true, "no" or "n" or "false" or "0" => false, _ => throw new ArgumentException($"'{cell}' is not yes or no.") };

    // --- structure: type,key,name,parentKey,timeZone,registrationNumber ---------------------------------------------------------------------
    public const string StructureHeader = "type,key,name,parentKey,timeZone,registrationNumber";

    public static CsvResult<(string Type, StructureEntry Entry)> ReadStructure(string? text) => Read(text, "type", 3, c =>
    {
        var type = At(c, 0).ToLowerInvariant();
        if (type is not ("legalentity" or "division" or "businessunit" or "site")) throw new ArgumentException($"'{At(c, 0)}' is not a structure type (LegalEntity, Division, BusinessUnit, Site).");
        return (type, new StructureEntry(At(c, 1), At(c, 2), Opt(c, 3), Opt(c, 4), Opt(c, 5)));
    }, "type,key,name[,parentKey,timeZone,registrationNumber]");

    public static StructureSection ToSection(IEnumerable<(string Type, StructureEntry Entry)> rows)
    {
        var list = rows.ToList();
        IReadOnlyList<StructureEntry> Of(string t) => list.Where(r => r.Type == t).Select(r => r.Entry).ToList();
        return new StructureSection(Of("legalentity"), Of("division"), Of("businessunit"), Of("site"));
    }

    public static string WriteStructure(StructureSection s)
    {
        var sb = new StringBuilder(StructureHeader).Append('\n');
        foreach (var e in s.LegalEntities) sb.AppendLine(Line("LegalEntity", e.Key, e.Name, e.ParentKey ?? "", e.TimeZoneId ?? "", e.RegistrationNumber ?? ""));
        foreach (var e in s.Divisions) sb.AppendLine(Line("Division", e.Key, e.Name, "", "", ""));
        foreach (var e in s.BusinessUnits) sb.AppendLine(Line("BusinessUnit", e.Key, e.Name, e.ParentKey ?? "", "", ""));
        foreach (var e in s.Sites) sb.AppendLine(Line("Site", e.Key, e.Name, "", e.TimeZoneId ?? "", ""));
        return sb.ToString();
    }

    // --- categories: code,name,parentCode -----------------------------------------------------------------------------------------------
    public const string CategoryHeader = "code,name,parentCode";
    public static CsvResult<CategoryEntry> ReadCategories(string? text) => Read(text, "code", 2, c => new CategoryEntry(At(c, 0), At(c, 1), Opt(c, 2)), "code,name[,parentCode]");
    public static string WriteCategories(IEnumerable<CategoryEntry> items) => CategoryHeader + "\n" + string.Concat(items.Select(i => Line(i.Code, i.Name, i.ParentCode ?? "") + "\n"));

    // --- contract types: code,name -------------------------------------------------------------------------------------------------------
    public const string TypeHeader = "code,name";
    public static CsvResult<ContractTypeEntry> ReadTypes(string? text) => Read(text, "code", 2, c => new ContractTypeEntry(At(c, 0), At(c, 1)), "code,name");
    public static string WriteTypes(IEnumerable<ContractTypeEntry> items) => TypeHeader + "\n" + string.Concat(items.Select(i => Line(i.Code, i.Name) + "\n"));

    // --- owners: categoryCode,email,primary ----------------------------------------------------------------------------------------------
    public const string OwnerHeader = "categoryCode,email,primary";
    public static CsvResult<OwnerEntry> ReadOwners(string? text) => Read(text, "categoryCode", 2, c => new OwnerEntry(At(c, 0), At(c, 1), ParseBool(At(c, 2))), "categoryCode,email[,primary]");
    public static string WriteOwners(IEnumerable<OwnerEntry> items) => OwnerHeader + "\n" + string.Concat(items.Select(i => Line(i.CategoryCode, i.Email, i.IsPrimary ? "yes" : "no") + "\n"));

    // --- viewers: email,viewerKind,categories,businessUnits,sites,ceiling ---------------------------------------------------------------
    public const string ViewerHeader = "email,viewerKind,categories,businessUnits,sites,ceiling";
    public static CsvResult<ViewerEntry> ReadViewers(string? text) => Read(text, "email", 3, c => new ViewerEntry(At(c, 0), ParseKind(At(c, 1)), Multi(At(c, 2)), Multi(At(c, 3)), Multi(At(c, 4)), At(c, 5)),
        "email,viewerKind,categories,businessUnits,sites,ceiling (use ';' between several values)");
    public static string WriteViewers(IEnumerable<ViewerEntry> items) => ViewerHeader + "\n" + string.Concat(items.Select(i =>
        Line(i.Email, i.ViewerKind.ToString(), Join(i.CategoryCodes), Join(i.BusinessUnitKeys), Join(i.SiteKeys), i.Ceiling) + "\n"));

    // --- group mappings: providerKey,groupKey,role,viewerKind,categories,businessUnits,sites,ceiling ----------------------------------
    public const string MappingHeader = "providerKey,groupKey,role,viewerKind,categories,businessUnits,sites,ceiling";
    public static CsvResult<MappingEntry> ReadMappings(string? text) => Read(text, "providerKey", 3, c => new MappingEntry(At(c, 0), At(c, 1), At(c, 2),
        string.IsNullOrWhiteSpace(At(c, 3)) ? null : ParseKind(At(c, 3)), Multi(At(c, 4)), Multi(At(c, 5)), Multi(At(c, 6)), At(c, 7)),
        "providerKey,groupKey,role[,viewerKind,categories,businessUnits,sites,ceiling]");
    public static string WriteMappings(IEnumerable<MappingEntry> items) => MappingHeader + "\n" + string.Concat(items.Select(i =>
        Line(i.ProviderKey, i.GroupKey, i.RoleCode, i.ViewerKind?.ToString() ?? "", Join(i.CategoryCodes), Join(i.BusinessUnitKeys), Join(i.SiteKeys), i.Ceiling) + "\n"));
}
