using Ecp.Host.Pages;
using Ecp.Approvals.Definitions;

namespace Ecp.Host.Pages.Admin.Approvals;


/// <summary>Plain-language descriptions of matrix rows for the administration pages.</summary>
public static class MatrixText
{
    public static IReadOnlyList<string> Conditions(RowConditions w)
    {
        var parts = new List<string>();
        if (w.HasValueBounds)
            parts.Add(w.ValueMin is { } lo && w.ValueMax is { } hi ? $"value from {lo:#,0.##} up to but not including {hi:#,0.##}" : w.ValueMin is { } min ? $"value {min:#,0.##} or more" : $"value below {w.ValueMax:#,0.##}");
        if (w.Categories.Count > 0) parts.Add($"[[category|lower]] {string.Join(" or ", w.Categories)}{(w.IncludeSubcategories ? " (and [[subcategory|plural|lower]])" : " (exactly)")}");
        if (w.ContractTypes.Count > 0) parts.Add($"[[contract.type|lower]] {string.Join(" or ", w.ContractTypes)}");
        if (w.LegalEntities.Count > 0) parts.Add($"[[legal.entity|lower]] {string.Join(" or ", w.LegalEntities)}");
        if (w.BusinessUnits.Count > 0) parts.Add($"[[business.unit|lower]] {string.Join(" or ", w.BusinessUnits)}");
        if (w.Sites.Count > 0) parts.Add($"[[site|lower]] {string.Join(" or ", w.Sites)}");
        if (w.Classifications.Count > 0) parts.Add($"classification {string.Join(" or ", w.Classifications)}");
        if (w.RiskLevels.Count > 0) parts.Add($"risk level {string.Join(" or ", w.RiskLevels)}");
        if (w.SupplierTypes.Count > 0) parts.Add($"[[supplier|lower]] type {string.Join(" or ", w.SupplierTypes)}");
        foreach (var c in w.Custom) parts.Add($"{c.Field} {c.Operator} {string.Join("/", c.Values)}");
        return parts.Count == 0 ? ["anything"] : parts;
    }

    public static string Window(PageFormat fmt, DateOnly? from, DateOnly? to) => from is null && to is null ? "always" : $"{fmt.Date(from, "…")} to {fmt.Date(to, "…")}";

    public static string Effect(MatrixRow r) => r.Kind == RowKinds.Base ? "Base" : r.Effect == RowKinds.ReplaceSteps ? "Exception: replaces the steps" : "Exception: adds steps";
}
