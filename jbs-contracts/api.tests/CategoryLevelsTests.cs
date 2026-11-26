using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>
/// D-115: the spend taxonomy goes Family > Category > Subcategory. A contract has one main category (a category or subcategory,
/// never a family alone) and can have other categories; filters by a family or category find both; the supplier tier follows the
/// main category up the tree; Excel reads and writes paths.
/// </summary>
public sealed class CategoryLevelsTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(Task<HttpResponseMessage> request)
    {
        var r = await request;
        var text = await r.Content.ReadAsStringAsync(Ct);
        return (r.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var (status, body) = await SendAsync(request);
        Assert.True((int)status is >= 200 and < 300, $"{(int)status}: {body}");
        return body;
    }

    private static async Task<int> AddAsync(HttpClient admin, string name, int? parentId) =>
        (await OkAsync(admin.PostAsJsonAsync("/api/categories", new { name, parentId }, Ct))).GetProperty("id").GetInt32();

    private static async Task<JsonElement> ContractAsync(HttpClient who, int id) => (await OkAsync(who.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract");

    private static int[] Listed(JsonElement list) => [.. list.EnumerateArray().Select(x => x.GetProperty("id").GetInt32())];

    [Fact]
    public async Task A_contract_has_a_main_category_and_other_categories_found_by_any_level()
    {
        var u = Unique("t");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var user = Unique("cl");
        await app.AddPersonAsync(user, $"Cat Levels {u[^4..]}", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var owner = app.ClientFor($@"TEST\{user}");

        // Packaging > Casings > Collagen casing, and Ingredients > Premixes.
        var packaging = await AddAsync(admin, $"Packaging {u}", null);
        var casings = await AddAsync(admin, "Casings", packaging);
        var collagen = await AddAsync(admin, "Collagen casing", casings);
        var ingredients = await AddAsync(admin, $"Ingredients {u}", null);
        var premixes = await AddAsync(admin, "Premixes", ingredients);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(admin.PostAsJsonAsync("/api/categories", new { name = "Too deep", parentId = collagen }, Ct))).Status);

        // Main category a subcategory; other categories a category (the main one isn't repeated); a family alone is refused.
        var id = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"Casings and premixes {u}", categoryId = collagen, otherCategoryIds = new[] { premixes, collagen } }, Ct)))
            .GetProperty("id").GetInt32();
        var k = await ContractAsync(owner, id);
        Assert.Equal($"Packaging {u} > Casings > Collagen casing", k.GetProperty("categoryDisplay").GetString());
        Assert.Equal([premixes], k.GetProperty("otherCategoryIds").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal([$"Ingredients {u} > Premixes"], k.GetProperty("otherCategories").EnumerateArray().Select(x => x.GetString()));
        var (s, b) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "Family", categoryId = casings, otherCategoryIds = new[] { ingredients } }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.True(b.GetProperty("fields").TryGetProperty("otherCategoryIds", out _));

        // Activity says so; the contracts list finds it under either family, its category, or the other category.
        await using var db = await app.OpenAsync();
        var created = await db.ExecuteScalarAsync<string>("SELECT TOP (1) DetailsJson FROM contracts.AuditEvent WHERE ContractId = @id AND Action = N'contract.created'", new { id });
        Assert.Contains("Premixes", created);
        foreach (var filter in new[] { packaging, casings, collagen, ingredients, premixes })
            Assert.Contains(id, Listed(await OkAsync(owner.GetAsync($"/api/contracts?view=all&f.category={filter}", Ct))));

        // Change the other categories: in plain words.
        await OkAsync(owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = $"Casings and premixes {u}", categoryId = collagen, otherCategoryIds = Array.Empty<int>(), rowVersion = k.GetProperty("rowVersion").GetString() }, Ct));
        var changed = await db.ExecuteScalarAsync<string>("SELECT TOP (1) Summary FROM contracts.AuditEvent WHERE ContractId = @id AND Action = N'contract.changed' ORDER BY Id DESC", new { id });
        Assert.Contains($"cleared other categories", changed);
        Assert.DoesNotContain(id, Listed(await OkAsync(owner.GetAsync($"/api/contracts?view=all&f.category={ingredients}", Ct))));
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task Excel_reads_and_writes_category_paths_and_the_tier_follows_the_family()
    {
        var u = Unique("x");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var user = Unique("cx");
        await app.AddPersonAsync(user, $"Cat Excel {u[^4..]}", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = true, auditor = false });
        using var owner = app.ClientFor($@"TEST\{user}");
        var family = await AddAsync(admin, $"Packaging {u}", null);
        var casings = await AddAsync(admin, $"Casings {u}", family);
        var collagen = await AddAsync(admin, "Collagen casing", casings);
        var film = await AddAsync(admin, "Film", family);

        // A path, the path's last parts, or a name found once all work; a family alone and an unknown name are refused.
        var supplier = Unique("Supplier ");
        var (_, result) = await ExcelTests.PostAsync(owner, "contracts", "import", ExcelTests.Sheet(["Title", "Supplier", "Category", "Other categories"],
            [$"Full {u}", supplier, $"Packaging {u} > Casings {u} > Collagen casing", $"Packaging {u} > Film"],
            [$"Ends {u}", supplier, $"Casings {u} > Collagen casing", null],
            [$"Family {u}", supplier, $"Packaging {u}", null],
            [$"Unknown {u}", supplier, $"Nothing {u}", null]));
        Assert.Equal((2, 2), (ExcelTests.Count(result, "added"), ExcelTests.Count(result, "refused")));
        await using var db = await app.OpenAsync();
        var full = await db.ExecuteScalarAsync<int>("SELECT Id FROM contracts.Contract WHERE Title = @t", new { t = $"Full {u}" });
        var k = await ContractAsync(owner, full);
        Assert.Equal(collagen, k.GetProperty("categoryId").GetInt32());
        Assert.Equal([film], k.GetProperty("otherCategoryIds").EnumerateArray().Select(x => x.GetInt32()));

        // The export writes paths, and the "Other categories" column, separated by semicolons.
        using (var book = new XLWorkbook(await (await owner.GetAsync("/api/excel/contracts", Ct)).Content.ReadAsStreamAsync(Ct)))
        {
            var sheet = book.Worksheet(1);
            int Col(string h) => sheet.Row(1).CellsUsed().Single(x => x.GetString() == h).Address.ColumnNumber;
            var row = sheet.RowsUsed().Single(r => r.Cell(1).GetString() == k.GetProperty("reference").GetString());
            Assert.Equal($"Packaging {u} > Casings {u} > Collagen casing", row.Cell(Col("Category")).GetString());
            Assert.Equal($"Packaging {u} > Film", row.Cell(Col("Other categories")).GetString());
        }

        // The supplier tier: set on the family, the subcategory follows it.
        var (tier, tierName) = await db.QueryFirstAsync<(int, string)>("SELECT TOP (1) Id, Value FROM contracts.ListItem WHERE ListName = N'SupplierTier' AND IsActive = 1 ORDER BY SortOrder");
        await OkAsync(admin.PutAsJsonAsync($"/api/categories/{family}/tier", new { tierId = tier }, Ct));
        var sid = await db.ExecuteScalarAsync<int>("SELECT Id FROM contracts.Supplier WHERE Name = @supplier", new { supplier });
        await db.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Effective' WHERE Id = @full", new { full });
        var page = await OkAsync(admin.GetAsync($"/api/suppliers/{sid}", Ct));
        Assert.Equal(tierName, page.GetProperty("supplier").GetProperty("autoTierName").GetString());
    }
}
