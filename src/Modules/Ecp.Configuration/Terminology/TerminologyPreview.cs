using Ecp.SharedKernel;

namespace Ecp.Configuration;

/// <summary>Builds the rows an administrator reads to see what a terminology document would change: its words beside the platform's neutral wording and, where known, the wording in force now.</summary>
public static class TerminologyPreview
{
    /// <summary>The first locale in the document that carries the most terms (usually the base language), one row per catalogue key it defines, in catalogue order.</summary>
    public static IReadOnlyList<TerminologyPreviewRow> Rows(TerminologyDocument document, TermSet? current)
    {
        var locale = document.Terms.GroupBy(t => t.Locale).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Length).Select(g => g.Key).FirstOrDefault() ?? "en";
        var byKey = document.Terms.Where(t => t.Active && t.Locale == locale).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var neutral = TermSet.Neutral();
        var rows = new List<TerminologyPreviewRow>();
        foreach (var def in TermCatalogue.All)
            if (byKey.TryGetValue(def.Key, out var e)) rows.Add(new TerminologyPreviewRow(def.Key, locale, e.Singular, e.Plural, neutral.Singular(def.Key), current?.Singular(def.Key)));
        foreach (var e in byKey.Values.Where(e => !TermCatalogue.IsKnown(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal))
            rows.Add(new TerminologyPreviewRow(e.Key, locale, e.Singular, e.Plural, "", current?.Singular(e.Key)));
        return rows;
    }
}
