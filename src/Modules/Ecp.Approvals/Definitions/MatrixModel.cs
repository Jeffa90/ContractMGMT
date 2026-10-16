using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ecp.Configuration;
using Ecp.SharedKernel;

namespace Ecp.Approvals.Definitions;

/// <summary>How the people of one step must respond. <c>FirstResponse</c>: the first valid response from the group, its backups or a delegate settles the step. <c>AllRequired</c>: every approver named when the plan was resolved must respond.</summary>
public static class StepModes
{
    public const string FirstResponse = "FirstResponse";
    public const string AllRequired = "AllRequired";
    public static readonly IReadOnlyList<string> All = [FirstResponse, AllRequired];
}

/// <summary>How the steps of one row relate: <c>Sequential</c> steps run one after another; <c>Parallel</c> steps are all open at the same time.</summary>
public static class RowFlows
{
    public const string Sequential = "Sequential";
    public const string Parallel = "Parallel";
    public static readonly IReadOnlyList<string> All = [Sequential, Parallel];
}

public static class RowKinds
{
    public const string Base = "Base";
    public const string Exception = "Exception";
    public const string AddSteps = "AddSteps";
    public const string ReplaceSteps = "ReplaceSteps";
}

/// <summary>The closed set of operators for a custom-field condition. There is no expression language.</summary>
public static class CustomOperators
{
    public const string Eq = "Equals", Ne = "NotEquals", In = "In", NotIn = "NotIn", AtLeast = "AtLeast", LessThan = "LessThan";
    public static readonly IReadOnlyList<string> All = [Eq, Ne, In, NotIn, AtLeast, LessThan];
}

public sealed record CustomCondition(string Field, string Operator, IReadOnlyList<string> Values);

/// <summary>The conditions of a row. An empty list on a dimension means "any". Value bounds are half-open <c>[ValueMin, ValueMax)</c> in the matrix currency; a null maximum is unbounded.</summary>
public sealed record RowConditions(
    decimal? ValueMin, decimal? ValueMax, IReadOnlyList<string> Categories, bool IncludeSubcategories, IReadOnlyList<string> ContractTypes, IReadOnlyList<string> LegalEntities,
    IReadOnlyList<string> BusinessUnits, IReadOnlyList<string> Sites, IReadOnlyList<string> Classifications, IReadOnlyList<string> RiskLevels, IReadOnlyList<string> SupplierTypes,
    IReadOnlyList<CustomCondition> Custom)
{
    public static RowConditions Any { get; } = new(null, null, [], true, [], [], [], [], [], [], [], []);
    public bool HasValueBounds => ValueMin is not null || ValueMax is not null;
}

public sealed record MatrixStep(string Key, string Display, string ApproverGroup, string? BackupGroup, string Mode, int? TimeoutDays, string? EscalateTo, bool AllowBackup);

public sealed record MatrixRow(
    string Code, string Description, string Kind, string? Effect, int Priority, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, RowConditions When, string Flow, IReadOnlyList<MatrixStep> Steps)
{
    public bool IsEffectiveOn(DateOnly date) => (EffectiveFrom is null || date >= EffectiveFrom) && (EffectiveTo is null || date <= EffectiveTo);
}

public sealed record RateEntry(string Currency, decimal Rate, DateOnly? EffectiveFrom, DateOnly? EffectiveTo)
{
    public bool IsEffectiveOn(DateOnly date) => (EffectiveFrom is null || date >= EffectiveFrom) && (EffectiveTo is null || date <= EffectiveTo);
}

public sealed record Matrix(string Code, string Name, string Description, string Currency, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, IReadOnlyList<RateEntry> Rates, IReadOnlyList<MatrixRow> Rows)
{
    public bool IsEffectiveOn(DateOnly date) => (EffectiveFrom is null || date >= EffectiveFrom) && (EffectiveTo is null || date <= EffectiveTo);
}

/// <summary>A published approval matrix document: one or more matrices, versioned as a whole by the configuration release that carries it.</summary>
public sealed class MatrixSet
{
    public required int SchemaVersion { get; init; }
    public required IReadOnlyList<Matrix> Matrices { get; init; }
    public Matrix? Find(string? code) => Matrices.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reference data used only to sharpen validation (unknown codes are warnings, category hierarchy refines overlap detection). Optional.</summary>
public sealed record MatrixReferenceData(
    IReadOnlyDictionary<string, IReadOnlyList<string>> CategoryAncestry, IReadOnlySet<string> BusinessUnits, IReadOnlySet<string> Sites, IReadOnlySet<string> LegalEntities, IReadOnlySet<string> ContractTypes)
{
    public static MatrixReferenceData Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>());
}

/// <summary>Reads and checks approval matrix documents. Unknown members are errors, so a typo is found when the draft is validated and never silently ignored.</summary>
public static partial class MatrixParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
        NumberHandling = JsonNumberHandling.Strict
    };

    public const int MaxMatrices = 50, MaxRowsPerMatrix = 500, MaxStepsPerRow = 10, MaxListItems = 100;

    private sealed class RawSet { public int SchemaVersion { get; set; } public List<RawMatrix>? Matrices { get; set; } }
    private sealed class RawMatrix
    {
        public string? Code { get; set; } public string? Name { get; set; } public string? Description { get; set; } public string? Currency { get; set; }
        public DateOnly? EffectiveFrom { get; set; } public DateOnly? EffectiveTo { get; set; } public List<RawRate>? Rates { get; set; } public List<RawRow>? Rows { get; set; }
    }
    private sealed class RawRate { public string? Currency { get; set; } public decimal Rate { get; set; } public DateOnly? EffectiveFrom { get; set; } public DateOnly? EffectiveTo { get; set; } }
    private sealed class RawRow
    {
        public string? Code { get; set; } public string? Description { get; set; } public string? Kind { get; set; } public string? Effect { get; set; } public int Priority { get; set; }
        public DateOnly? EffectiveFrom { get; set; } public DateOnly? EffectiveTo { get; set; } public RawWhen? When { get; set; } public string? Flow { get; set; } public List<RawStep>? Steps { get; set; }
    }
    private sealed class RawWhen
    {
        public decimal? ValueMin { get; set; } public decimal? ValueMax { get; set; } public List<string>? Categories { get; set; } public bool IncludeSubcategories { get; set; } = true;
        public List<string>? ContractTypes { get; set; } public List<string>? LegalEntities { get; set; } public List<string>? BusinessUnits { get; set; } public List<string>? Sites { get; set; }
        public List<string>? Classifications { get; set; } public List<string>? RiskLevels { get; set; } public List<string>? SupplierTypes { get; set; } public List<RawCustom>? Custom { get; set; }
    }
    private sealed class RawCustom { public string? Field { get; set; } public string? Operator { get; set; } public List<string>? Values { get; set; } }
    private sealed class RawStep
    {
        public string? Key { get; set; } public string? Display { get; set; } public string? ApproverGroup { get; set; } public string? BackupGroup { get; set; } public string? Mode { get; set; }
        public int? TimeoutDays { get; set; } public string? EscalateTo { get; set; } public bool? AllowBackup { get; set; }
    }

    public static (MatrixSet? Set, List<ValidationMessage> Messages) Parse(string json)
    {
        var messages = new List<ValidationMessage>();
        RawSet? raw;
        try { raw = JsonSerializer.Deserialize<RawSet>(json, Options); }
        catch (JsonException ex)
        {
            messages.Add(Err("$", "invalid_json", "The approval matrix could not be read: " + ex.Message.Split('\n')[0]));
            return (null, messages);
        }
        if (raw is null) { messages.Add(Err("$", "invalid_json", "The approval matrix document is empty.")); return (null, messages); }

        static IReadOnlyList<string> L(List<string>? l) => (l ?? []).Select(x => x?.Trim() ?? "").ToList();
        var matrices = (raw.Matrices ?? []).Select(m => new Matrix(
            m.Code?.Trim() ?? "", m.Name?.Trim() ?? "", m.Description?.Trim() ?? "", m.Currency?.Trim().ToUpperInvariant() ?? "", m.EffectiveFrom, m.EffectiveTo,
            (m.Rates ?? []).Select(r => new RateEntry(r.Currency?.Trim().ToUpperInvariant() ?? "", r.Rate, r.EffectiveFrom, r.EffectiveTo)).ToList(),
            (m.Rows ?? []).Select(r =>
            {
                var w = r.When;
                var when = w is null ? RowConditions.Any : new RowConditions(w.ValueMin, w.ValueMax, L(w.Categories), w.IncludeSubcategories, L(w.ContractTypes), L(w.LegalEntities), L(w.BusinessUnits), L(w.Sites),
                    L(w.Classifications), L(w.RiskLevels), L(w.SupplierTypes), (w.Custom ?? []).Select(c => new CustomCondition(c.Field?.Trim() ?? "", c.Operator?.Trim() ?? "", L(c.Values))).ToList());
                return new MatrixRow(r.Code?.Trim() ?? "", r.Description?.Trim() ?? "", r.Kind?.Trim() ?? RowKinds.Base, r.Effect?.Trim(), r.Priority, r.EffectiveFrom, r.EffectiveTo, when, r.Flow?.Trim() ?? RowFlows.Sequential,
                    (r.Steps ?? []).Select(s =>
                    {
                        var mode = s.Mode?.Trim() ?? StepModes.FirstResponse;
                        return new MatrixStep(s.Key?.Trim() ?? "", s.Display?.Trim() ?? s.Key?.Trim() ?? "", s.ApproverGroup?.Trim() ?? "", string.IsNullOrWhiteSpace(s.BackupGroup) ? null : s.BackupGroup.Trim(), mode, s.TimeoutDays,
                            string.IsNullOrWhiteSpace(s.EscalateTo) ? null : s.EscalateTo.Trim(), s.AllowBackup ?? (mode == StepModes.FirstResponse));
                    }).ToList());
            }).ToList())).ToList();
        return (new MatrixSet { SchemaVersion = raw.SchemaVersion, Matrices = matrices }, messages);
    }

    private static ValidationMessage Err(string path, string code, string message) => new(ValidationSeverity.Error, path, code, message);
    private static ValidationMessage Warn(string path, string code, string message) => new(ValidationSeverity.Warning, path, code, message);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,49}$", RegexOptions.NonBacktracking)] private static partial Regex CodePattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.NonBacktracking)] private static partial Regex GroupPattern();
    [GeneratedRegex("^[a-z][A-Za-z0-9_]{0,49}$", RegexOptions.NonBacktracking)] private static partial Regex FieldPattern();
    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.NonBacktracking)] private static partial Regex CurrencyPattern();

    /// <summary>Checks a parsed document: vocabularies, bounds, windows, rates, value-range gaps and overlaps, priority ties. Reference data (when available) refines category overlap and adds unknown-code warnings.</summary>
    public static IReadOnlyList<ValidationMessage> Validate(MatrixSet set, ClassificationScale scale, MatrixReferenceData? reference = null)
    {
        var m = new List<ValidationMessage>();
        reference ??= MatrixReferenceData.Empty;
        if (set.SchemaVersion != 1) m.Add(Err("$.schemaVersion", "schema_version", "Only schema version 1 is supported."));
        if (set.Matrices.Count == 0) m.Add(Err("$.matrices", "matrices_required", "Define at least one matrix."));
        if (set.Matrices.Count > MaxMatrices) m.Add(Err("$.matrices", "limit", $"At most {MaxMatrices} matrices."));
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < set.Matrices.Count; i++) ValidateMatrix(set.Matrices[i], $"$.matrices[{i}]", codes, scale, reference, m);
        return m;
    }

    private static void ValidateMatrix(Matrix x, string path, HashSet<string> codes, ClassificationScale scale, MatrixReferenceData reference, List<ValidationMessage> m)
    {
        if (!CodePattern().IsMatch(x.Code)) m.Add(Err(path + ".code", "matrix_code", "A matrix code is lower-case letters, digits and hyphens (up to 50)."));
        else if (!codes.Add(x.Code)) m.Add(Err(path + ".code", "matrix_duplicate", $"Matrix '{x.Code}' is defined twice."));
        if (x.Name.Length is 0 or > 200) m.Add(Err(path + ".name", "matrix_name", "A matrix needs a name of up to 200 characters."));
        if (x.Description.Length > 1000) m.Add(Err(path + ".description", "matrix_description", "A description is at most 1000 characters."));
        if (!CurrencyPattern().IsMatch(x.Currency)) m.Add(Err(path + ".currency", "currency", "The matrix currency is a three-letter ISO 4217 code."));
        if (x.EffectiveFrom is { } f && x.EffectiveTo is { } t && t < f) m.Add(Err(path + ".effectiveTo", "window_order", "The end of the effective window is before its start."));

        for (var i = 0; i < x.Rates.Count; i++)
        {
            var r = x.Rates[i]; var rp = $"{path}.rates[{i}]";
            if (!CurrencyPattern().IsMatch(r.Currency)) m.Add(Err(rp + ".currency", "rate_currency", "A rate names a three-letter currency."));
            else if (r.Currency == x.Currency) m.Add(Err(rp + ".currency", "rate_currency", "A rate to the matrix's own currency is not needed."));
            if (r.Rate <= 0 || r.Rate > 1_000_000_000m) m.Add(Err(rp + ".rate", "rate_value", "A rate is greater than zero and at most 1,000,000,000 (units of the matrix currency per unit of the foreign currency)."));
            if (r.EffectiveFrom is { } rf && r.EffectiveTo is { } rt && rt < rf) m.Add(Err(rp + ".effectiveTo", "window_order", "The end of the effective window is before its start."));
        }
        foreach (var group in x.Rates.Select((r, i) => (r, i)).Where(p => CurrencyPattern().IsMatch(p.r.Currency)).GroupBy(p => p.r.Currency))
            foreach (var (a, b) in Pairs(group.ToList()))
                if (Overlap(a.r.EffectiveFrom, a.r.EffectiveTo, b.r.EffectiveFrom, b.r.EffectiveTo))
                    m.Add(Err($"{path}.rates[{b.i}]", "rate_overlap", $"Two {group.Key} rates are in force on the same day. A rate table has one rate for each currency on any date."));

        if (x.Rows.Count == 0) m.Add(Err(path + ".rows", "rows_required", "A matrix needs at least one row."));
        if (x.Rows.Count > MaxRowsPerMatrix) m.Add(Err(path + ".rows", "limit", $"At most {MaxRowsPerMatrix} rows in a matrix."));
        if (x.Rows.Count > 0 && !x.Rows.Any(r => r.Kind == RowKinds.Base)) m.Add(Err(path + ".rows", "base_required", "A matrix needs at least one Base row; exceptions only add to or replace what a base row resolves."));

        var rowCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < x.Rows.Count; i++) ValidateRow(x.Rows[i], $"{path}.rows[{i}]", rowCodes, scale, reference, x, m);
        CheckOverlapsAndGaps(x, path, reference, m);
    }

    private static void ValidateRow(MatrixRow r, string path, HashSet<string> rowCodes, ClassificationScale scale, MatrixReferenceData reference, Matrix matrix, List<ValidationMessage> m)
    {
        if (!CodePattern().IsMatch(r.Code)) m.Add(Err(path + ".code", "row_code", "A row code is lower-case letters, digits and hyphens (up to 50)."));
        else if (!rowCodes.Add(r.Code)) m.Add(Err(path + ".code", "row_duplicate", $"Row '{r.Code}' is defined twice."));
        if (r.Description.Length > 500) m.Add(Err(path + ".description", "row_description", "A row description is at most 500 characters."));
        if (r.Kind is not (RowKinds.Base or RowKinds.Exception)) m.Add(Err(path + ".kind", "row_kind", "A row is a Base row or an Exception."));
        if (r.Kind == RowKinds.Base && r.Effect is not null) m.Add(Err(path + ".effect", "row_effect", "Only an Exception has an effect."));
        if (r.Kind == RowKinds.Exception && r.Effect is not (RowKinds.AddSteps or RowKinds.ReplaceSteps)) m.Add(Err(path + ".effect", "row_effect", "An exception adds steps (AddSteps) or replaces the base steps (ReplaceSteps)."));
        if (r.Priority is < 0 or > 10000) m.Add(Err(path + ".priority", "priority", "Priority is between 0 and 10000; the highest priority wins."));
        if (r.EffectiveFrom is { } f && r.EffectiveTo is { } t && t < f) m.Add(Err(path + ".effectiveTo", "window_order", "The end of the effective window is before its start."));
        if (!RowFlows.All.Contains(r.Flow)) m.Add(Err(path + ".flow", "row_flow", "The flow is Sequential or Parallel."));

        if (r.Steps.Count == 0) m.Add(Err(path + ".steps", "steps_required", "A row needs at least one step."));
        if (r.Steps.Count > MaxStepsPerRow) m.Add(Err(path + ".steps", "limit", $"At most {MaxStepsPerRow} steps in a row."));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < r.Steps.Count; i++)
        {
            var s = r.Steps[i]; var sp = $"{path}.steps[{i}]";
            if (!CodePattern().IsMatch(s.Key)) m.Add(Err(sp + ".key", "step_key", "A step key is lower-case letters, digits and hyphens (up to 50)."));
            else if (!keys.Add(s.Key)) m.Add(Err(sp + ".key", "step_duplicate", $"Step '{s.Key}' is defined twice in this row."));
            if (s.Display.Length is 0 or > 200) m.Add(Err(sp + ".display", "step_display", "A step needs a display name of up to 200 characters."));
            if (!GroupPattern().IsMatch(s.ApproverGroup)) m.Add(Err(sp + ".approverGroup", "approver_group", "Name the approver group: letters, digits, '.', '_' or '-' (up to 100). Groups hold people; a matrix never names a person."));
            if (s.BackupGroup is not null && !GroupPattern().IsMatch(s.BackupGroup)) m.Add(Err(sp + ".backupGroup", "backup_group", "The backup group is named like an approver group."));
            if (s.BackupGroup is not null && string.Equals(s.BackupGroup, s.ApproverGroup, StringComparison.OrdinalIgnoreCase)) m.Add(Err(sp + ".backupGroup", "backup_group", "The backup group must differ from the approver group; backup approvers of the same group are included automatically."));
            if (s.EscalateTo is not null && !GroupPattern().IsMatch(s.EscalateTo)) m.Add(Err(sp + ".escalateTo", "escalate_group", "The escalation group is named like an approver group."));
            if (s.Mode is not (StepModes.FirstResponse or StepModes.AllRequired)) m.Add(Err(sp + ".mode", "step_mode", "The mode is FirstResponse or AllRequired."));
            if (s.TimeoutDays is < 0 or > 365) m.Add(Err(sp + ".timeoutDays", "timeout", "A timeout is between 0 and 365 days."));
        }

        var w = r.When;
        if (w.ValueMin is < 0) m.Add(Err(path + ".when.valueMin", "value_min", "The lower value bound cannot be negative."));
        if (w.ValueMin is { } lo && w.ValueMax is { } hi && hi <= lo) m.Add(Err(path + ".when.valueMax", "value_range", "The upper bound must be greater than the lower bound (ranges are half-open: lower bound included, upper excluded)."));
        void CheckList(IReadOnlyList<string> l, string name)
        {
            if (l.Count > MaxListItems) m.Add(Err($"{path}.when.{name}", "limit", $"At most {MaxListItems} items."));
            if (l.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 100)) m.Add(Err($"{path}.when.{name}", "list_item", "Items are not blank and are at most 100 characters."));
            if (l.Count != l.Distinct(StringComparer.OrdinalIgnoreCase).Count()) m.Add(Warn($"{path}.when.{name}", "list_duplicate", "The list repeats a value."));
        }
        CheckList(w.Categories, "categories"); CheckList(w.ContractTypes, "contractTypes"); CheckList(w.LegalEntities, "legalEntities"); CheckList(w.BusinessUnits, "businessUnits");
        CheckList(w.Sites, "sites"); CheckList(w.Classifications, "classifications"); CheckList(w.RiskLevels, "riskLevels"); CheckList(w.SupplierTypes, "supplierTypes");
        foreach (var c in w.Classifications.Where(c => !string.IsNullOrWhiteSpace(c) && !scale.IsKnown(c))) m.Add(Err(path + ".when.classifications", "classification_unknown", $"'{c}' is not a classification level of this tenant."));
        void Known(IReadOnlyList<string> values, IReadOnlySet<string> known, string name)
        {
            if (known.Count == 0) return;
            foreach (var v in values.Where(v => !string.IsNullOrWhiteSpace(v) && !known.Contains(v, StringComparer.OrdinalIgnoreCase)))
                m.Add(Warn($"{path}.when.{name}", "reference_unknown", $"'{v}' is not a known {name} code for this organisation. The row cannot match until it exists."));
        }
        Known(w.BusinessUnits, reference.BusinessUnits, "businessUnits"); Known(w.Sites, reference.Sites, "sites"); Known(w.LegalEntities, reference.LegalEntities, "legalEntities");
        Known(w.ContractTypes, reference.ContractTypes, "contractTypes");
        if (reference.CategoryAncestry.Count > 0)
            foreach (var c in w.Categories.Where(c => !string.IsNullOrWhiteSpace(c) && !reference.CategoryAncestry.Keys.Contains(c, StringComparer.OrdinalIgnoreCase)))
                m.Add(Warn(path + ".when.categories", "reference_unknown", $"'{c}' is not a known category code for this organisation. The row cannot match until it exists."));
        if (w.Custom.Count > MaxListItems) m.Add(Err(path + ".when.custom", "limit", $"At most {MaxListItems} custom conditions."));
        for (var i = 0; i < w.Custom.Count; i++)
        {
            var c = w.Custom[i]; var cp = $"{path}.when.custom[{i}]";
            if (!FieldPattern().IsMatch(c.Field)) m.Add(Err(cp + ".field", "custom_field", "A custom field code starts with a lower-case letter and has letters, digits or '_' (up to 50)."));
            if (!CustomOperators.All.Contains(c.Operator)) m.Add(Err(cp + ".operator", "custom_operator", $"The operator is one of: {string.Join(", ", CustomOperators.All)}."));
            else if (c.Operator is CustomOperators.AtLeast or CustomOperators.LessThan)
            {
                if (c.Values.Count != 1 || !decimal.TryParse(c.Values[0], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _))
                    m.Add(Err(cp + ".values", "custom_values", $"{c.Operator} compares with one number."));
            }
            else if (c.Values.Count == 0 || (c.Operator is CustomOperators.Eq or CustomOperators.Ne && c.Values.Count != 1)) m.Add(Err(cp + ".values", "custom_values", $"{c.Operator} needs {(c.Operator is CustomOperators.Eq or CustomOperators.Ne ? "exactly one value" : "at least one value")}."));
            if (c.Values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 200)) m.Add(Err(cp + ".values", "custom_values", "Values are not blank and are at most 200 characters."));
        }
    }

    // ---- overlaps, gaps and ties (V-005) ------------------------------------------------------------------------------------------------------------

    private static bool Overlap(DateOnly? aFrom, DateOnly? aTo, DateOnly? bFrom, DateOnly? bTo)
        => (aTo is null || bFrom is null || bFrom <= aTo) && (bTo is null || aFrom is null || aFrom <= bTo);

    private static IEnumerable<(T A, T B)> Pairs<T>(IReadOnlyList<T> l) { for (var i = 0; i < l.Count; i++) for (var j = i + 1; j < l.Count; j++) yield return (l[i], l[j]); }

    private static string Signature(RowConditions w)
    {
        static string S(IReadOnlyList<string> l) => string.Join(",", l.Select(x => x.ToUpperInvariant()).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        var custom = string.Join(";", w.Custom.Select(c => $"{c.Field}|{c.Operator}|{S(c.Values)}").OrderBy(x => x, StringComparer.Ordinal));
        return string.Join("/", S(w.Categories) + (w.Categories.Count > 0 ? (w.IncludeSubcategories ? "+" : "=") : ""), S(w.ContractTypes), S(w.LegalEntities), S(w.BusinessUnits), S(w.Sites), S(w.Classifications), S(w.RiskLevels), S(w.SupplierTypes), custom);
    }

    private static bool ListsMayOverlap(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Count == 0 || b.Count == 0 || a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();

    private static IEnumerable<string> PathOf(string category, MatrixReferenceData reference)
        => reference.CategoryAncestry.TryGetValue(category, out var path) ? path : [category];

    private static bool CategoriesMayOverlap(RowConditions a, RowConditions b, MatrixReferenceData reference)
    {
        if (a.Categories.Count == 0 || b.Categories.Count == 0) return true;
        if (a.Categories.Intersect(b.Categories, StringComparer.OrdinalIgnoreCase).Any()) return true;
        if (reference.CategoryAncestry.Count == 0) return false;
        // Without a shared code two rows can still meet on a subcategory: look for any category that both rows match.
        foreach (var c in reference.CategoryAncestry.Keys)
        {
            var path = PathOf(c, reference).ToList();
            bool Matches(RowConditions w) => w.IncludeSubcategories ? w.Categories.Any(x => path.Contains(x, StringComparer.OrdinalIgnoreCase)) : w.Categories.Contains(c, StringComparer.OrdinalIgnoreCase);
            if (Matches(a) && Matches(b)) return true;
        }
        return false;
    }

    private static bool CustomMayOverlap(RowConditions a, RowConditions b)
    {
        foreach (var ca in a.Custom)
            foreach (var cb in b.Custom.Where(x => string.Equals(x.Field, ca.Field, StringComparison.Ordinal)))
            {
                static bool Positive(string op) => op is CustomOperators.Eq or CustomOperators.In;
                if (Positive(ca.Operator) && Positive(cb.Operator) && !ca.Values.Intersect(cb.Values, StringComparer.OrdinalIgnoreCase).Any()) return false;
                if (ca.Operator == CustomOperators.Eq && cb.Operator == CustomOperators.Ne && ca.Values[0].Equals(cb.Values[0], StringComparison.OrdinalIgnoreCase)) return false;
                if (cb.Operator == CustomOperators.Eq && ca.Operator == CustomOperators.Ne && cb.Values[0].Equals(ca.Values[0], StringComparison.OrdinalIgnoreCase)) return false;
            }
        return true;
    }

    private static bool ValueRangesOverlap(RowConditions a, RowConditions b)
    {
        var aMin = a.ValueMin ?? decimal.MinValue; var bMin = b.ValueMin ?? decimal.MinValue;
        var aMax = a.ValueMax ?? decimal.MaxValue; var bMax = b.ValueMax ?? decimal.MaxValue;
        return aMin < bMax && bMin < aMax;
    }

    /// <summary>True when some record could satisfy both rows' conditions (conservative: unknowns count as "may").</summary>
    internal static bool MayBothMatch(MatrixRow a, MatrixRow b, MatrixReferenceData reference)
    {
        if (!Overlap(a.EffectiveFrom, a.EffectiveTo, b.EffectiveFrom, b.EffectiveTo)) return false;
        var x = a.When; var y = b.When;
        return ValueRangesOverlap(x, y) && CategoriesMayOverlap(x, y, reference) && ListsMayOverlap(x.ContractTypes, y.ContractTypes) && ListsMayOverlap(x.LegalEntities, y.LegalEntities)
            && ListsMayOverlap(x.BusinessUnits, y.BusinessUnits) && ListsMayOverlap(x.Sites, y.Sites) && ListsMayOverlap(x.Classifications, y.Classifications)
            && ListsMayOverlap(x.RiskLevels, y.RiskLevels) && ListsMayOverlap(x.SupplierTypes, y.SupplierTypes) && CustomMayOverlap(x, y);
    }

    private static void CheckOverlapsAndGaps(Matrix matrix, string path, MatrixReferenceData reference, List<ValidationMessage> m)
    {
        var indexed = matrix.Rows.Select((r, i) => (Row: r, Index: i)).ToList();
        var bases = indexed.Where(p => p.Row.Kind == RowKinds.Base).ToList();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        // 1. Ties: two base rows (or two replacing exceptions) that can both match the same record with the same priority have no winner.
        void Ties(List<(MatrixRow Row, int Index)> rows, string what)
        {
            foreach (var (a, b) in Pairs(rows))
                if (a.Row.Priority == b.Row.Priority && MayBothMatch(a.Row, b.Row, reference) && reported.Add($"tie:{a.Index}:{b.Index}"))
                    m.Add(Err($"{path}.rows[{b.Index}]", "row_tie", $"{what} '{a.Row.Code}' and '{b.Row.Code}' can match the same record at the same priority ({a.Row.Priority}), so neither wins. Change a priority or narrow a condition."));
        }
        Ties(bases, "Base rows");
        Ties(indexed.Where(p => p.Row.Kind == RowKinds.Exception && p.Row.Effect == RowKinds.ReplaceSteps).ToList(), "Replacing exceptions");

        // 2. Value ranges: rows that share every other condition (and are in force on the same day) must tile the value axis with no overlap and no gap.
        var boundaries = bases.SelectMany(p => new[] { p.Row.EffectiveFrom, p.Row.EffectiveTo?.AddDays(1) }).Where(d => d is not null).Select(d => d!.Value).Distinct().OrderBy(d => d).ToList();
        var representatives = new List<DateOnly?> { null };
        representatives.AddRange(boundaries.Select(d => (DateOnly?)d));
        foreach (var day in representatives)
        {
            var active = bases.Where(p => day is null ? p.Row.EffectiveFrom is null : p.Row.IsEffectiveOn(day.Value)).ToList();
            foreach (var group in active.GroupBy(p => Signature(p.Row.When)))
            {
                var rows = group.Where(p => p.Row.When.HasValueBounds).OrderBy(p => p.Row.When.ValueMin ?? decimal.MinValue).ToList();
                var unbounded = group.Where(p => !p.Row.When.HasValueBounds).ToList();
                if (rows.Count == 0) continue;
                foreach (var (a, b) in Pairs(rows))
                    if (ValueRangesOverlap(a.Row.When, b.Row.When) && reported.Add($"ov:{a.Index}:{b.Index}"))
                        m.Add(Err($"{path}.rows[{b.Index}]", "range_overlap", $"Rows '{a.Row.Code}' and '{b.Row.Code}' have the same conditions and their value ranges overlap. Ranges are half-open [minimum, maximum): the upper bound of one row is the lower bound of the next."));
                foreach (var u in unbounded)
                    if (reported.Add($"ovu:{u.Index}")) m.Add(Err($"{path}.rows[{u.Index}]", "range_overlap", $"Row '{u.Row.Code}' has no value range but shares its conditions with rows that do, so it overlaps all of them."));
                if (unbounded.Count > 0) continue;
                var cursor = 0m; var first = true;
                foreach (var p in rows)
                {
                    var lo = p.Row.When.ValueMin ?? 0m;
                    if (first) { if (lo > 0 && reported.Add($"lo:{p.Index}")) m.Add(Warn($"{path}.rows[{p.Index}]", "range_lower", $"Nothing covers values below {lo:0.##} for these conditions (row '{p.Row.Code}' starts there), so such a submission is blocked.")); first = false; }
                    else if (lo > cursor && reported.Add($"gap:{p.Index}")) m.Add(Err($"{path}.rows[{p.Index}]", "range_gap", $"There is a gap between {cursor:0.##} and {lo:0.##} for the conditions of row '{p.Row.Code}': values in it match no row. Ranges are half-open, so the next row starts where the previous one ends."));
                    if (p.Row.When.ValueMax is null) { cursor = decimal.MaxValue; break; }
                    cursor = Math.Max(cursor, p.Row.When.ValueMax.Value);
                }
                if (cursor != decimal.MaxValue && reported.Add($"hi:{group.Key}:{day}")) m.Add(Warn($"{path}.rows[{rows[^1].Index}]", "range_upper", $"Nothing covers values from {cursor:0.##} upwards for the conditions of row '{rows[^1].Row.Code}' (its maximum is not unbounded), so such a submission is blocked."));
            }
        }
    }
}
