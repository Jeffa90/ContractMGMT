using System.Globalization;
using System.Text.RegularExpressions;

namespace Ecp.Metadata;

/// <summary>
/// Works out a calculated display field from the values of other fields. The operations are a closed list (sum, difference, product, ratio, days between, text template); a definition cannot
/// supply an expression, so there is nothing to inject. A missing operand gives no result rather than a guess.
/// </summary>
public static partial class Calculator
{
    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.NonBacktracking)] private static partial Regex Placeholder();

    public static string? Evaluate(FieldDefinition d, Func<string, decimal?> number, Func<string, DateOnly?> date, Func<string, string?> text, CultureInfo culture)
    {
        if (d.Calculated is not { } c) return null;
        var decimals = c.Decimals ?? 2;
        string Show(decimal n) => Math.Round(n, decimals, MidpointRounding.AwayFromZero).ToString("#,0." + new string('#', decimals), culture);
        switch (c.Kind)
        {
            case CalculationKinds.Template:
                return c.Template is null ? null : Placeholder().Replace(c.Template, m => text(m.Groups[1].Value) ?? "");
            case CalculationKinds.DaysBetween:
                if (c.Operands.Count != 2 || date(c.Operands[0]) is not { } from || date(c.Operands[1]) is not { } to) return null;
                return (to.DayNumber - from.DayNumber).ToString("N0", culture);
        }
        var values = new List<decimal>();
        foreach (var o in c.Operands) { if (number(o) is not { } n) return null; values.Add(n); }
        try
        {
            return c.Kind switch
            {
                CalculationKinds.Sum => Show(values.Sum()),
                CalculationKinds.Difference => values.Count == 2 ? Show(values[0] - values[1]) : null,
                CalculationKinds.Product => Show(values.Aggregate(1m, (a, b) => a * b)),
                CalculationKinds.Ratio => values.Count == 2 && values[1] != 0 ? Show(values[0] / values[1]) : null,
                _ => null
            };
        }
        catch (OverflowException) { return null; }
    }
}
