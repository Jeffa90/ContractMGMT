namespace JbsContracts.Api.Features;

/// <summary>
/// Reading several list values from one spreadsheet cell (D-114): business units, sites, entities and functions. Values are
/// separated by semicolons or new lines ("Dinmore; Riverina"), the way JBS Contracts exports them. Commas separate them too,
/// unless the whole cell, or a part between semicolons, is itself a value with a comma in it.
/// </summary>
public static class ListCells
{
    private static readonly char[] Strong = [';', '\n', '\r', '|'];

    /// <summary>The values in a cell, trimmed, without repeats (any case). Empty for a blank cell.</summary>
    /// <param name="known">Whether a text is a value as it stands (used to keep a value with a comma in it whole).</param>
    public static List<string> Split(string? cell, Func<string, bool>? known = null)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(cell)) return result;
        var whole = cell.Trim();
        if (known is not null && known(whole)) return [whole];
        foreach (var part in whole.Split(Strong, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.Contains(',') || (known is not null && known(part))) result.Add(part);
            else result.AddRange(part.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
