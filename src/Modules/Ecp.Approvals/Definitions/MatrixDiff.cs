using System.Text.Json;

namespace Ecp.Approvals.Definitions;

/// <summary>What changed between two approval matrix documents, row by row, for the audit history.</summary>
public sealed record MatrixChanges(IReadOnlyList<string> MatricesAdded, IReadOnlyList<string> MatricesRemoved, IReadOnlyList<string> RowsAdded, IReadOnlyList<string> RowsRemoved, IReadOnlyList<string> RowsChanged, IReadOnlyList<string> MatricesChanged)
{
    public bool Any => MatricesAdded.Count + MatricesRemoved.Count + RowsAdded.Count + RowsRemoved.Count + RowsChanged.Count + MatricesChanged.Count > 0;
    public string Summary
        => !Any ? "No change to any matrix or row."
            : string.Join("; ", new[]
            {
                MatricesAdded.Count > 0 ? "matrices added: " + string.Join(", ", MatricesAdded) : null, MatricesRemoved.Count > 0 ? "matrices removed: " + string.Join(", ", MatricesRemoved) : null,
                MatricesChanged.Count > 0 ? "matrix settings changed: " + string.Join(", ", MatricesChanged) : null, RowsAdded.Count > 0 ? "rows added: " + string.Join(", ", RowsAdded) : null,
                RowsRemoved.Count > 0 ? "rows removed: " + string.Join(", ", RowsRemoved) : null, RowsChanged.Count > 0 ? "rows changed: " + string.Join(", ", RowsChanged) : null
            }.Where(x => x is not null));
}

public static class MatrixDiff
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MatrixChanges Compare(MatrixSet? before, MatrixSet after)
    {
        var old = before?.Matrices.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase) ?? [];
        var now = after.Matrices.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);
        List<string> addedM = [], removedM = [], changedM = [], addedR = [], removedR = [], changedR = [];
        foreach (var (code, m) in now)
        {
            if (!old.TryGetValue(code, out var o)) { addedM.Add(code); continue; }
            if (Head(o) != Head(m)) changedM.Add(code);
            var oldRows = o.Rows.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
            foreach (var r in m.Rows)
            {
                if (!oldRows.TryGetValue(r.Code, out var or)) addedR.Add($"{code}/{r.Code}");
                else if (JsonSerializer.Serialize(or, Json) != JsonSerializer.Serialize(r, Json)) changedR.Add($"{code}/{r.Code}");
            }
            foreach (var r in o.Rows.Where(r => !m.Rows.Any(x => string.Equals(x.Code, r.Code, StringComparison.OrdinalIgnoreCase)))) removedR.Add($"{code}/{r.Code}");
        }
        foreach (var code in old.Keys.Where(k => !now.ContainsKey(k))) removedM.Add(code);
        return new MatrixChanges(addedM, removedM, addedR, removedR, changedR, changedM);
    }

    private static string Head(Matrix m) => JsonSerializer.Serialize(new { m.Name, m.Description, m.Currency, m.EffectiveFrom, m.EffectiveTo, m.Rates }, Json);
}
