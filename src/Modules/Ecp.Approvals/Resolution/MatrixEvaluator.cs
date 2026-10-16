using System.Globalization;
using Ecp.Approvals.Definitions;

namespace Ecp.Approvals.Resolution;

/// <summary>What is known about the thing being approved. A fact that is absent (null or missing) never satisfies a condition on it: resolution fails closed rather than guessing.</summary>
public sealed record ApprovalFacts(
    decimal? Value, string? Currency, IReadOnlyList<string> CategoryPath, string? ContractType, string? LegalEntity, string? BusinessUnit, string? Site, string? Classification, string? RiskLevel,
    string? SupplierType, IReadOnlyDictionary<string, string?> Custom)
{
    public static ApprovalFacts Empty { get; } = new(null, null, [], null, null, null, null, null, null, null, new Dictionary<string, string?>());
    public string? Category => CategoryPath.Count > 0 ? CategoryPath[0] : null;
}

public static class TraceStages
{
    public const string Matrix = "Matrix", Currency = "Currency", Row = "Row", Selection = "Selection", Exception = "Exception", Plan = "Plan", Step = "Step", Approver = "Approver", Delegation = "Delegation", Result = "Result";
}

public static class TraceOutcomes
{
    public const string Info = "Info", Matched = "Matched", NotMatched = "NotMatched", Skipped = "Skipped", Selected = "Selected", Applied = "Applied", Blocked = "Blocked", Included = "Included", Excluded = "Excluded";
}

public sealed record TraceEntry(int Sequence, string Stage, string Code, string Outcome, string Message);

/// <summary>An append-only record of how a resolution was reached, in the order it happened.</summary>
public sealed class ResolutionTrace
{
    private readonly List<TraceEntry> _entries = [];
    public IReadOnlyList<TraceEntry> Entries => _entries;
    public void Add(string stage, string code, string outcome, string message) => _entries.Add(new TraceEntry(_entries.Count + 1, stage, code, outcome, message));
}

/// <summary>A step chosen by the matrix, before people are attached.</summary>
public sealed record PlannedStep(string Key, string RowCode, MatrixStep Step, int Stage);

public sealed record Selection(
    bool Success, string? BlockCode, string? BlockMessage, Matrix? Matrix, MatrixRow? BaseRow, MatrixRow? ReplaceRow, IReadOnlyList<MatrixRow> AddRows, decimal? ConvertedValue, IReadOnlyList<PlannedStep> Steps)
{
    public IReadOnlyList<string> RowCodes => new[] { BaseRow, ReplaceRow }.Where(r => r is not null).Select(r => r!.Code).Concat(AddRows.Select(r => r.Code)).Distinct().ToList();
    public static Selection Blocked(string code, string message, Matrix? matrix = null) => new(false, code, message, matrix, null, null, [], null, []);
}

/// <summary>
/// The matching half of the resolver: which rows apply to these facts on this date, and which steps they make. Pure and deterministic; the caller attaches people to the steps afterwards.
/// Precedence: among matching base rows the highest priority wins; a replacing exception (highest priority) swaps the base steps for its own; adding exceptions apply after, highest priority first.
/// </summary>
public static class MatrixEvaluator
{
    public static Selection Select(MatrixSet set, string matrixCode, ApprovalFacts facts, DateOnly asOf, ResolutionTrace trace)
    {
        var matrix = set.Find(matrixCode);
        if (matrix is null)
        {
            trace.Add(TraceStages.Matrix, matrixCode, TraceOutcomes.Blocked, $"There is no matrix '{matrixCode}' in the approval matrix document.");
            return Selection.Blocked("matrix_not_found", $"There is no approval matrix '{matrixCode}'. An administrator needs to publish it or correct the workflow's reference to it.");
        }
        if (!matrix.IsEffectiveOn(asOf))
        {
            trace.Add(TraceStages.Matrix, matrix.Code, TraceOutcomes.Blocked, $"Matrix '{matrix.Code}' is not in force on {asOf:yyyy-MM-dd} (effective {Window(matrix.EffectiveFrom, matrix.EffectiveTo)}).");
            return Selection.Blocked("matrix_not_effective", $"Approval matrix '{matrix.Code}' is not in force on {asOf:yyyy-MM-dd}.", matrix);
        }
        trace.Add(TraceStages.Matrix, matrix.Code, TraceOutcomes.Info, $"Using matrix '{matrix.Name}' ({matrix.Currency}), in force on {asOf:yyyy-MM-dd}.");

        // Value in the matrix currency. There is never an implicit conversion: a foreign currency needs a rate in force on the date, otherwise nothing is guessed.
        decimal? value = null;
        if (facts.Value is { } raw)
        {
            if (string.IsNullOrWhiteSpace(facts.Currency))
            {
                trace.Add(TraceStages.Currency, "currency", TraceOutcomes.Blocked, "A value was given without a currency.");
                return Selection.Blocked("currency_missing", "The value has no currency, so it cannot be compared with the matrix.", matrix);
            }
            var cur = facts.Currency.Trim().ToUpperInvariant();
            if (cur == matrix.Currency) { value = raw; trace.Add(TraceStages.Currency, cur, TraceOutcomes.Info, $"Value {Fmt(raw)} {cur} is in the matrix currency."); }
            else
            {
                var rate = matrix.Rates.Where(r => r.Currency == cur && r.IsEffectiveOn(asOf)).OrderByDescending(r => r.EffectiveFrom ?? DateOnly.MinValue).FirstOrDefault();
                if (rate is null)
                {
                    trace.Add(TraceStages.Currency, cur, TraceOutcomes.Blocked, $"The value is in {cur} but the matrix is in {matrix.Currency} and has no {cur} rate in force on {asOf:yyyy-MM-dd}. No conversion is assumed.");
                    return Selection.Blocked("no_rate", $"The value is in {cur}, the matrix is in {matrix.Currency}, and there is no {cur} rate in force on {asOf:yyyy-MM-dd}. An administrator must add a rate or the submission must use {matrix.Currency}.", matrix);
                }
                value = raw * rate.Rate;
                trace.Add(TraceStages.Currency, cur, TraceOutcomes.Info, $"Converted {Fmt(raw)} {cur} at {rate.Rate.ToString("0.######", CultureInfo.InvariantCulture)} ({Window(rate.EffectiveFrom, rate.EffectiveTo)}) = {Fmt(value.Value)} {matrix.Currency}.");
            }
        }
        else trace.Add(TraceStages.Currency, "value", TraceOutcomes.Info, "No value is known; rows with a value range cannot match.");

        var matched = new List<MatrixRow>();
        foreach (var row in matrix.Rows)
        {
            if (!row.IsEffectiveOn(asOf))
            {
                trace.Add(TraceStages.Row, row.Code, TraceOutcomes.Skipped, $"Row '{row.Code}' is not in force on {asOf:yyyy-MM-dd} (effective {Window(row.EffectiveFrom, row.EffectiveTo)}).");
                continue;
            }
            var misses = Misses(row.When, facts, value);
            if (misses.Count == 0) { matched.Add(row); trace.Add(TraceStages.Row, row.Code, TraceOutcomes.Matched, $"{row.Kind} row '{row.Code}' (priority {row.Priority}) matches every condition."); }
            else trace.Add(TraceStages.Row, row.Code, TraceOutcomes.NotMatched, $"{row.Kind} row '{row.Code}' does not match: {string.Join("; ", misses)}.");
        }

        var bases = matched.Where(r => r.Kind == RowKinds.Base).OrderByDescending(r => r.Priority).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
        if (bases.Count == 0)
        {
            trace.Add(TraceStages.Selection, "base", TraceOutcomes.Blocked, "No base row matches, so there is nobody to ask. The submission is blocked rather than defaulted.");
            return Selection.Blocked("no_match", "No row of the approval matrix matches this submission, so it is blocked until an administrator corrects the matrix or the submission.", matrix);
        }
        if (bases.Count > 1 && bases[0].Priority == bases[1].Priority)
        {
            trace.Add(TraceStages.Selection, "base", TraceOutcomes.Blocked, $"Base rows '{bases[0].Code}' and '{bases[1].Code}' both match at priority {bases[0].Priority}: no winner.");
            return Selection.Blocked("priority_tie", $"Rows '{bases[0].Code}' and '{bases[1].Code}' match at the same priority, so the matrix does not say who approves. An administrator must fix the priorities.", matrix);
        }
        var baseRow = bases[0];
        trace.Add(TraceStages.Selection, baseRow.Code, TraceOutcomes.Selected, bases.Count == 1
            ? $"Base row '{baseRow.Code}' is the only match."
            : $"Base row '{baseRow.Code}' wins on priority {baseRow.Priority} over {string.Join(", ", bases.Skip(1).Select(b => $"'{b.Code}' ({b.Priority})"))}.");

        var replaces = matched.Where(r => r.Kind == RowKinds.Exception && r.Effect == RowKinds.ReplaceSteps).OrderByDescending(r => r.Priority).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
        if (replaces.Count > 1 && replaces[0].Priority == replaces[1].Priority)
        {
            trace.Add(TraceStages.Exception, "replace", TraceOutcomes.Blocked, $"Replacing exceptions '{replaces[0].Code}' and '{replaces[1].Code}' both match at priority {replaces[0].Priority}.");
            return Selection.Blocked("priority_tie", $"Exceptions '{replaces[0].Code}' and '{replaces[1].Code}' both replace the steps at the same priority. An administrator must fix the priorities.", matrix);
        }
        var replace = replaces.FirstOrDefault();
        if (replace is not null) trace.Add(TraceStages.Exception, replace.Code, TraceOutcomes.Applied, $"Exception '{replace.Code}' replaces the steps of '{baseRow.Code}' with its own" + (replaces.Count > 1 ? $" (it outranks {string.Join(", ", replaces.Skip(1).Select(r => $"'{r.Code}'"))})." : "."));
        var adds = matched.Where(r => r.Kind == RowKinds.Exception && r.Effect == RowKinds.AddSteps).OrderByDescending(r => r.Priority).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
        foreach (var a in adds) trace.Add(TraceStages.Exception, a.Code, TraceOutcomes.Applied, $"Exception '{a.Code}' adds {a.Steps.Count} step(s) after the base steps.");

        // Stages: a sequential row makes one stage per step; a parallel row makes one stage for all its steps. Rows follow one another: the base (or replacing) row first, then each adding exception.
        var steps = new List<PlannedStep>();
        var stage = 0;
        void Add(MatrixRow row)
        {
            if (row.Flow == RowFlows.Parallel) { foreach (var s in row.Steps) steps.Add(new PlannedStep(Key(row, s, steps), row.Code, s, stage)); stage++; }
            else foreach (var s in row.Steps) { steps.Add(new PlannedStep(Key(row, s, steps), row.Code, s, stage)); stage++; }
        }
        Add(replace ?? baseRow);
        foreach (var a in adds) Add(a);
        foreach (var g in steps.GroupBy(s => s.Stage))
            trace.Add(TraceStages.Plan, $"stage-{g.Key + 1}", TraceOutcomes.Info, $"Stage {g.Key + 1}: {string.Join(" and ", g.Select(s => $"{s.Step.Display} ({s.Step.Mode})"))}.");
        return new Selection(true, null, null, matrix, baseRow, replace, adds, value, steps);
    }

    private static string Key(MatrixRow row, MatrixStep s, List<PlannedStep> existing) => existing.Any(e => e.Key == $"{row.Code}/{s.Key}") ? $"{row.Code}/{s.Key}#{existing.Count}" : $"{row.Code}/{s.Key}";

    private static string Fmt(decimal v) => v.ToString("#,0.##", CultureInfo.InvariantCulture);
    private static string Window(DateOnly? from, DateOnly? to) => from is null && to is null ? "always" : $"{from?.ToString("yyyy-MM-dd") ?? "…"} to {to?.ToString("yyyy-MM-dd") ?? "…"}";

    /// <summary>The conditions a row's requirements fail on these facts (empty when it matches).</summary>
    public static IReadOnlyList<string> Misses(RowConditions w, ApprovalFacts f, decimal? value)
    {
        var misses = new List<string>();
        if (w.HasValueBounds)
        {
            if (value is null) misses.Add("it has a value range but no value is known");
            else
            {
                if (w.ValueMin is { } lo && value < lo) misses.Add($"value {Fmt(value.Value)} is below the minimum {Fmt(lo)}");
                if (w.ValueMax is { } hi && value >= hi) misses.Add($"value {Fmt(value.Value)} is not below the maximum {Fmt(hi)} (the maximum is excluded)");
            }
        }
        if (w.Categories.Count > 0)
        {
            var ok = f.CategoryPath.Count > 0 && (w.IncludeSubcategories ? w.Categories.Any(c => f.CategoryPath.Contains(c, StringComparer.OrdinalIgnoreCase)) : w.Categories.Contains(f.CategoryPath[0], StringComparer.OrdinalIgnoreCase));
            if (!ok) misses.Add(f.CategoryPath.Count == 0 ? "the category is not known" : $"category {f.CategoryPath[0]} is not {(w.IncludeSubcategories ? "in or under " : "")}{string.Join("/", w.Categories)}");
        }
        void One(IReadOnlyList<string> allowed, string? actual, string label)
        {
            if (allowed.Count == 0) return;
            if (string.IsNullOrWhiteSpace(actual)) misses.Add($"the {label} is not known");
            else if (!allowed.Contains(actual, StringComparer.OrdinalIgnoreCase)) misses.Add($"{label} {actual} is not one of {string.Join("/", allowed)}");
        }
        One(w.ContractTypes, f.ContractType, "contract type"); One(w.LegalEntities, f.LegalEntity, "legal entity"); One(w.BusinessUnits, f.BusinessUnit, "business unit");
        One(w.Sites, f.Site, "site"); One(w.Classifications, f.Classification, "classification"); One(w.RiskLevels, f.RiskLevel, "risk level"); One(w.SupplierTypes, f.SupplierType, "supplier type");
        foreach (var c in w.Custom)
        {
            f.Custom.TryGetValue(c.Field, out var actual);
            if (string.IsNullOrWhiteSpace(actual)) { misses.Add($"custom field {c.Field} is not known"); continue; }
            var ok = c.Operator switch
            {
                CustomOperators.Eq => string.Equals(actual, c.Values[0], StringComparison.OrdinalIgnoreCase),
                CustomOperators.Ne => !string.Equals(actual, c.Values[0], StringComparison.OrdinalIgnoreCase),
                CustomOperators.In => c.Values.Contains(actual, StringComparer.OrdinalIgnoreCase),
                CustomOperators.NotIn => !c.Values.Contains(actual, StringComparer.OrdinalIgnoreCase),
                CustomOperators.AtLeast => Num(actual) is { } a1 && Num(c.Values[0]) is { } b1 && a1 >= b1,
                CustomOperators.LessThan => Num(actual) is { } a2 && Num(c.Values[0]) is { } b2 && a2 < b2,
                _ => false
            };
            if (!ok) misses.Add($"custom field {c.Field} = {actual} fails {c.Operator} {string.Join("/", c.Values)}");
        }
        return misses;
    }

    private static decimal? Num(string s) => decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
}
