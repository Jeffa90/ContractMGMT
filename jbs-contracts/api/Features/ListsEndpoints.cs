using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Features;

public sealed class ListItemRow
{
    public int Id { get; set; }
    public string ListName { get; set; } = "";
    public string Value { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
}

public sealed class CategoryRow
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    /// <summary>The supplier tier this spend category gives (D-108); a subcategory without one follows its category.</summary>
    public int? TierId { get; set; }
}

public sealed record CategoryTier(int? TierId);

public sealed record SaveListItem(string? ListName, string? Value, bool? IsActive);
public sealed record SaveCategory(string? Name, int? ParentId, bool? IsActive);

/// <summary>Lists an admin manages: categories (with subcategories), agreement types, business units, sites, entities, functions, currencies and more.</summary>
public static class ListsEndpoints
{
    public static readonly Dictionary<string, string> ListLabels = new()
    {
        ["AgreementType"] = "agreement type", ["BusinessUnit"] = "business unit", ["Site"] = "site", ["Currency"] = "currency", ["SupplierDocumentType"] = "supplier document type", ["SupplierTier"] = "supplier tier",
        ["DocumentKind"] = "contract document kind", ["SupplierRisk"] = "supplier risk level", ["Entity"] = "entity", ["Function"] = "function",
    };

    /// <summary>The built-in document kinds and risk levels aren't in these lists (D-98), so an added one can't take their name.</summary>
    private static string? BuiltInClash(string listName, string value) => listName switch
    {
        "DocumentKind" => DocumentEndpoints.Kinds.Values.FirstOrDefault(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase)),
        "SupplierRisk" => new[] { "Low", "Medium", "High" }.FirstOrDefault(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase)),
        _ => null,
    };

    public static void MapListsEndpoints(this IEndpointRouteBuilder app)
    {
        // Everyone signed in reads the lists (for pickers), plus the people who can own contracts.
        app.MapGet("/api/lists", async (CurrentUser current, Db db) =>
        {
            var (_, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var items = await c.QueryAsync<ListItemRow>("SELECT * FROM contracts.ListItem ORDER BY ListName, SortOrder, Value");
            var categories = await c.QueryAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive, TierId FROM contracts.Category ORDER BY SortOrder, Name");
            var owners = await c.QueryAsync<IdName>("SELECT Id, Name FROM contracts.Person WHERE IsActive = 1 AND IsContractOwner = 1 ORDER BY Name");
            return Results.Ok(new { items, categories, owners });
        });

        app.MapPost("/api/lists/items", async (SaveListItem body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            if (body.ListName is null || !ListLabels.TryGetValue(body.ListName, out var label)) return ApiResults.Invalid("Choose a list.", new() { ["listName"] = "Choose a list." });
            var value = (body.Value ?? "").Trim();
            if (value.Length == 0 || value.Length > 100) return ApiResults.Invalid("Check the name.", new() { ["value"] = value.Length == 0 ? "Type a name." : "Keep it under 100 characters." });
            if (body.ListName == "Currency" && !System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z]{3}$"))
                return ApiResults.Invalid("Check the currency.", new() { ["value"] = "Use the three-letter code, for example AUD." });
            if (body.ListName == "Currency") value = value.ToUpperInvariant();
            if (BuiltInClash(body.ListName, value) is { } builtIn)
                return ApiResults.Invalid("That one's built in.", new() { ["value"] = $"{builtIn} is already there for everyone; it isn't shown in this list. Choose another name." });
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var clash = await c.QuerySingleOrDefaultAsync<ListItemRow>("SELECT * FROM contracts.ListItem WITH (UPDLOCK, HOLDLOCK) WHERE ListName = @ListName AND ValueKey = UPPER(@value)", new { body.ListName, value }, tx);
            if (clash is not null)
                return ApiResults.Invalid("That's already in the list.", new() { ["value"] = clash.IsActive ? $"{clash.Value} is already in the list." : $"{clash.Value} is in the list but hidden. Show it again instead." });
            var order = await c.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(SortOrder), -1) + 1 FROM contracts.ListItem WHERE ListName = @ListName", new { body.ListName }, tx);
            var id = await c.ExecuteScalarAsync<int>("INSERT INTO contracts.ListItem (ListName, Value, SortOrder) OUTPUT INSERTED.Id VALUES (@ListName, @value, @order)",
                new { body.ListName, value, order }, tx);
            await audit.WriteAsync(c, tx, me!.AsActor(), "list.added", "ListItem", id, $"{me.Name} added the {label} {value}.");
            await tx.CommitAsync();
            return Results.Created($"/api/lists/items/{id}", new { id });
        });

        app.MapPut("/api/lists/items/{id:int}", async (int id, SaveListItem body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await c.QuerySingleOrDefaultAsync<ListItemRow>("SELECT * FROM contracts.ListItem WITH (UPDLOCK) WHERE Id = @id", new { id }, tx);
            if (before is null) return ApiResults.NotFound("We couldn't find that list item.");
            var value = (body.Value ?? before.Value).Trim();
            if (before.ListName == "Currency") value = value.ToUpperInvariant();
            if (value.Length == 0 || value.Length > 100) return ApiResults.Invalid("Check the name.", new() { ["value"] = "Type a name under 100 characters." });
            if (BuiltInClash(before.ListName, value) is { } builtIn)
                return ApiResults.Invalid("That one's built in.", new() { ["value"] = $"{builtIn} is already there for everyone; it isn't shown in this list. Choose another name." });
            var clash = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WITH (UPDLOCK, HOLDLOCK) WHERE ListName = @ListName AND ValueKey = UPPER(@value) AND Id <> @id", new { before.ListName, value, id }, tx);
            if (clash > 0) return ApiResults.Invalid("That's already in the list.", new() { ["value"] = $"{value} is already in the list." });
            var active = body.IsActive ?? before.IsActive;
            await c.ExecuteAsync("UPDATE contracts.ListItem SET Value = @value, IsActive = @active WHERE Id = @id", new { value, active, id }, tx);
            var label = ListLabels[before.ListName];
            var summary = before.IsActive && !active ? $"{me!.Name} hid the {label} {before.Value}. Contracts that use it keep it."
                : !before.IsActive && active ? $"{me!.Name} showed the {label} {value} again."
                : $"{me!.Name} renamed the {label} {before.Value} to {value}.";
            await audit.WriteAsync(c, tx, me.AsActor(), "list.changed", "ListItem", id, summary, new { before = new { before.Value, before.IsActive }, after = new { value, active } });
            await tx.CommitAsync();
            return Results.Ok(new { id });
        });

        app.MapPost("/api/categories", async (SaveCategory body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            var name = (body.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 100) return ApiResults.Invalid("Check the name.", new() { ["name"] = name.Length == 0 ? "Type a name." : "Keep it under 100 characters." });
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            string? parentName = null;
            if (body.ParentId is { } parentId)
            {
                var parent = await c.QuerySingleOrDefaultAsync<CategoryRow>("SELECT * FROM contracts.Category WHERE Id = @parentId", new { parentId }, tx);
                if (parent is null) return ApiResults.Invalid("Choose a category.", new() { ["parentId"] = "That category no longer exists." });
                if (parent.ParentId is not null) return ApiResults.Invalid("Subcategories go one level deep.", new() { ["parentId"] = "Choose a top-level category." });
                parentName = parent.Name;
            }
            var clash = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Category WITH (UPDLOCK, HOLDLOCK) WHERE ParentKey = ISNULL(@ParentId, 0) AND NameKey = UPPER(@name)", new { body.ParentId, name }, tx);
            if (clash > 0) return ApiResults.Invalid("That's already there.", new() { ["name"] = $"{name} is already there." });
            var order = await c.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(SortOrder), -1) + 1 FROM contracts.Category WHERE ParentKey = ISNULL(@ParentId, 0)", new { body.ParentId }, tx);
            var id = await c.ExecuteScalarAsync<int>("INSERT INTO contracts.Category (ParentId, Name, SortOrder) OUTPUT INSERTED.Id VALUES (@ParentId, @name, @order)",
                new { body.ParentId, name, order }, tx);
            await audit.WriteAsync(c, tx, me!.AsActor(), "category.added", "Category", id,
                parentName is null ? $"{me.Name} added the category {name}." : $"{me.Name} added the subcategory {name} under {parentName}.");
            await tx.CommitAsync();
            return Results.Created($"/api/categories/{id}", new { id });
        });

        app.MapPut("/api/categories/{id:int}", async (int id, SaveCategory body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await c.QuerySingleOrDefaultAsync<CategoryRow>("SELECT Id, ParentId, Name, SortOrder, IsActive FROM contracts.Category WITH (UPDLOCK) WHERE Id = @id", new { id }, tx);
            if (before is null) return ApiResults.NotFound("We couldn't find that category.");
            var name = (body.Name ?? before.Name).Trim();
            if (name.Length == 0 || name.Length > 100) return ApiResults.Invalid("Check the name.", new() { ["name"] = "Type a name under 100 characters." });
            var clash = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Category WITH (UPDLOCK, HOLDLOCK) WHERE ParentKey = ISNULL(@ParentId, 0) AND NameKey = UPPER(@name) AND Id <> @id", new { before.ParentId, name, id }, tx);
            if (clash > 0) return ApiResults.Invalid("That's already there.", new() { ["name"] = $"{name} is already there." });
            var active = body.IsActive ?? before.IsActive;
            await c.ExecuteAsync("UPDATE contracts.Category SET Name = @name, IsActive = @active WHERE Id = @id", new { name, active, id }, tx);
            var summary = before.IsActive && !active ? $"{me!.Name} hid the category {before.Name}. Contracts that use it keep it."
                : !before.IsActive && active ? $"{me!.Name} showed the category {name} again."
                : $"{me!.Name} renamed the category {before.Name} to {name}.";
            await audit.WriteAsync(c, tx, me.AsActor(), "category.changed", "Category", id, summary, new { before = new { before.Name, before.IsActive }, after = new { name, active } });
            await tx.CommitAsync();
            return Results.Ok(new { id });
        });

        // Set or clear the supplier tier a category gives (D-108).
        app.MapPut("/api/categories/{id:int}/tier", async (int id, CategoryTier body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await c.QuerySingleOrDefaultAsync<(string Name, int? TierId, string? TierName)>("""
                SELECT k.Name, k.TierId, t.Value FROM contracts.Category k WITH (UPDLOCK) LEFT JOIN contracts.ListItem t ON t.Id = k.TierId WHERE k.Id = @id
                """, new { id }, tx);
            if (before == default) return ApiResults.NotFound("We couldn't find that category.");
            string? tierName = null;
            if (body.TierId is { } tierId)
            {
                tierName = await c.ExecuteScalarAsync<string?>("SELECT Value FROM contracts.ListItem WHERE Id = @tierId AND ListName = N'SupplierTier' AND IsActive = 1", new { tierId }, tx);
                if (tierName is null) return ApiResults.Invalid("Choose a tier from the list.", new() { ["tierId"] = "Choose a supplier tier from the list." });
            }
            if (before.TierId == body.TierId) return Results.Ok(new { id });
            await c.ExecuteAsync("UPDATE contracts.Category SET TierId = @tierId WHERE Id = @id", new { tierId = body.TierId, id }, tx);
            await audit.WriteAsync(c, tx, me!.AsActor(), "category.tier", "Category", id,
                tierName is null ? $"{me.Name} cleared the supplier tier of the category {before.Name} (was {before.TierName})."
                    : $"{me.Name} set the supplier tier of the category {before.Name} to {tierName}{(before.TierName is { } was ? $" (was {was})" : "")}.",
                new { before = before.TierId, after = body.TierId });
            await tx.CommitAsync();
            return Results.Ok(new { id });
        });
    }

    public static async Task<(Person? Person, IResult? Refusal)> RequireAdminAsync(CurrentUser current, Db db)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return (null, refusal);
        return me!.IsAdmin ? (me, null) : (null, ApiResults.NotAllowed("Only admins can change the lists. Ask your admin."));
    }
}
