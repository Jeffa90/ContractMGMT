namespace JbsContracts.Api.Features;

/// <summary>
/// The spend taxonomy (D-115): Family, then Category, then Subcategory (three levels at most). A contract's main category is a
/// Category or a Subcategory, never a Family alone. Paths read "Family > Category > Subcategory" ("›" works too); names repeat
/// across the tree, so a path is how a node is named in Excel and on imports.
/// </summary>
public sealed class CategoryTree
{
    public const int MaxDepth = 3;
    public static readonly string[] LevelNames = ["family", "category", "subcategory"];

    private readonly Dictionary<int, CategoryRow> byId;
    private readonly ILookup<int?, CategoryRow> children;

    public CategoryTree(IEnumerable<CategoryRow> rows)
    {
        var list = rows.ToList();
        byId = list.ToDictionary(r => r.Id);
        children = list.OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToLookup(r => r.ParentId);
    }

    public IEnumerable<CategoryRow> All => byId.Values;
    public CategoryRow? Get(int id) => byId.GetValueOrDefault(id);
    public IEnumerable<CategoryRow> Children(int? parentId) => children[parentId];

    private static string K(string s) => s.Trim().ToUpperInvariant();

    /// <summary>1 for a family, 2 for a category, 3 for a subcategory.</summary>
    public int Depth(int id)
    {
        var depth = 0;
        for (int? at = id; at is { } a && byId.TryGetValue(a, out var row); at = row.ParentId) depth++;
        return depth;
    }

    /// <summary>The node and its parents, top first.</summary>
    public List<CategoryRow> Line(int id)
    {
        var line = new List<CategoryRow>();
        for (int? at = id; at is { } a && byId.TryGetValue(a, out var row); at = row.ParentId) line.Insert(0, row);
        return line;
    }

    public string Path(int id) => string.Join(" > ", Line(id).Select(r => r.Name));

    /// <summary>The node and everything under it.</summary>
    public IEnumerable<int> WithDescendants(int id)
    {
        yield return id;
        foreach (var child in children[id])
            foreach (var d in WithDescendants(child.Id)) yield return d;
    }

    public static string[] SplitPath(string path) => path.Split(['>', '›'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The node at a full path from the top ("Packaging > Casings"), or null.</summary>
    public CategoryRow? FindPath(IReadOnlyList<string> parts)
    {
        CategoryRow? at = null;
        foreach (var part in parts)
        {
            at = children[at?.Id].FirstOrDefault(c => K(c.Name) == K(part));
            if (at is null) return null;
        }
        return at;
    }

    /// <summary>
    /// What a person typed for a contract's category: a full path, a path's last parts ("Casings > Collagen Casing"), or a name
    /// found once below the families. Problem says what's wrong, in plain words; families alone aren't enough.
    /// </summary>
    public CategoryRow? Resolve(string text, out string? problem)
    {
        problem = null;
        var parts = SplitPath(text);
        if (parts.Length == 0) { problem = "Category: choose one."; return null; }
        if (parts.Length > MaxDepth) { problem = $"Category: \"{text}\" has more than three levels. Write it as Family > Category > Subcategory."; return null; }
        var found = FindPath(parts) is { } exact ? [exact] : All.Where(n => EndsWith(n.Id, parts)).ToList();
        if (found.Count == 0) { problem = $"Category: there's no category called \"{text}\". Add it under Admin, Lists first."; return null; }
        if (found.Count > 1) { problem = $"Category: \"{text}\" is in more than one place. Write it as Family > Category > Subcategory."; return null; }
        if (Depth(found[0].Id) == 1) { problem = $"Category: {found[0].Name} is a family. Choose a category in it, for example {Children(found[0].Id).FirstOrDefault()?.Name ?? "one under it"}."; return null; }
        return found[0];
    }

    private bool EndsWith(int id, string[] parts)
    {
        var line = Line(id);
        if (line.Count < parts.Length) return false;
        return line.Skip(line.Count - parts.Length).Select(r => K(r.Name)).SequenceEqual(parts.Select(K));
    }

    /// <summary>Every category and subcategory as a path, families' order then names: what can be picked for a contract.</summary>
    public IEnumerable<(int Id, string Path, bool IsActive)> Pickable()
    {
        foreach (var family in children[null])
            foreach (var category in children[family.Id])
            {
                yield return (category.Id, Path(category.Id), family.IsActive && category.IsActive);
                foreach (var sub in children[category.Id])
                    yield return (sub.Id, Path(sub.Id), family.IsActive && category.IsActive && sub.IsActive);
            }
    }
}
