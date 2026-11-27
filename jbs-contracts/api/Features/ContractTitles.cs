using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

public sealed record SaveTitleSetting(bool FromDetails);

/// <summary>
/// Contract titles made from the details (D-131, D-132): Supplier_Agreement type_What it covers, where what it covers is the main
/// category's lowest level picked (a subcategory called just "Other" uses its category). While the admin setting is on, the title
/// of a contract raised or edited on the form is always this one, whatever was sent; imports keep their own titles.
/// </summary>
public static class ContractTitles
{
    public static async Task<bool> FromDetailsAsync(SqlConnection c, SqlTransaction? tx = null) =>
        await c.ExecuteScalarAsync<string?>("SELECT Value FROM contracts.AppSetting WHERE Name = N'TitleFromDetails'", transaction: tx) != "No";

    public static async Task<string> SuggestedAsync(SqlConnection c, SqlTransaction? tx, int? supplierId, int? agreementTypeId, int? categoryId)
    {
        var supplier = supplierId is null ? null
            : await c.ExecuteScalarAsync<string?>("SELECT Name FROM contracts.Supplier WHERE Id = @supplierId", new { supplierId }, tx);
        var type = agreementTypeId is null ? null
            : await c.ExecuteScalarAsync<string?>("SELECT Value FROM contracts.ListItem WHERE Id = @agreementTypeId AND ListName = N'AgreementType'", new { agreementTypeId }, tx);
        string? covers = null;
        if (categoryId is not null)
        {
            var (name, parentId) = await c.QuerySingleOrDefaultAsync<(string? Name, int? ParentId)>(
                "SELECT Name, ParentId FROM contracts.Category WHERE Id = @categoryId", new { categoryId }, tx);
            covers = name;
            if (name?.Trim().Equals("Other", StringComparison.OrdinalIgnoreCase) == true && parentId is { } parent)
                covers = await c.ExecuteScalarAsync<string?>("SELECT Name FROM contracts.Category WHERE Id = @parent", new { parent }, tx);
        }
        var title = string.Join('_', new[] { supplier, type, covers }.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p)));
        return title.Length > 200 ? title[..200] : title;
    }

    /// <summary>
    /// The contract as it's saved from the form: with the title made from its details while the setting is on. A refusal when none of
    /// supplier, agreement type and category is chosen yet, as there's nothing to make it from.
    /// </summary>
    public static async Task<(SaveContract Body, Dictionary<string, string>? Errors)> ApplyAsync(SqlConnection c, SqlTransaction tx, SaveContract body)
    {
        if (!await FromDetailsAsync(c, tx)) return (body, null);
        var title = await SuggestedAsync(c, tx, body.SupplierId, body.AgreementTypeId, body.CategoryId);
        if (title.Length == 0) return (body, new() { ["title"] = "Choose the supplier, agreement type or category first: the title is made from them." });
        return (body with { Title = title }, null);
    }

    public static void MapContractTitleEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin, Contract fields: titles made from the details, or typed by people (D-132). Audited.
        app.MapPut("/api/settings/title-from-details", async (SaveTitleSetting body, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ListsEndpoints.RequireAdminAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var before = await FromDetailsAsync(c, tx);
            if (before == body.FromDetails) return Results.Ok(new { fromDetails = before });
            await c.ExecuteAsync("""
                MERGE contracts.AppSetting AS t USING (SELECT N'TitleFromDetails' AS Name) AS s ON t.Name = s.Name
                WHEN MATCHED THEN UPDATE SET Value = @value, UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me
                WHEN NOT MATCHED THEN INSERT (Name, Value, UpdatedById) VALUES (N'TitleFromDetails', @value, @me);
                """, new { value = body.FromDetails ? "Yes" : "No", me = me!.Id }, tx);
            await audit.WriteAsync(c, tx, me.AsActor(), "settings.title", "AppSetting", null,
                body.FromDetails
                    ? $"{me.Name} set contract titles to be made from the details (Supplier_Agreement type_What it covers); people can't type their own."
                    : $"{me.Name} let people type their own contract titles (the form still suggests one from the details).",
                new { before, after = body.FromDetails });
            await tx.CommitAsync();
            return Results.Ok(new { fromDetails = body.FromDetails });
        });
    }
}
