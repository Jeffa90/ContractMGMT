using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>
/// D-116: supplier groups (for example a franchise: the head company and its franchisees, each its own supplier and SAP vendor number;
/// the head company's contracts cover the group), and imports that find suppliers by SAP vendor number before name.
/// </summary>
public sealed class SupplierGroupsTests(AppFixture app)
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

    private async Task<HttpClient> PersonAsync(string name, bool owner = false, bool procurement = false, bool admin = false)
    {
        var user = Unique("sg");
        await app.AddPersonAsync(user, $"{name} {user[^4..]}", new { contractOwner = owner, legal = false, approver = false, groupHead = false, admin, auditor = false, procurement });
        return app.ClientFor($@"TEST\{user}");
    }

    private static async Task<int> SupplierAsync(HttpClient who, string name, string? vendorNumber = null) =>
        (await OkAsync(who.PostAsJsonAsync("/api/suppliers", new { name, vendorNumber }, Ct))).GetProperty("id").GetInt32();

    private static Task<(HttpStatusCode Status, JsonElement Body)> GroupAsync(HttpClient who, int memberId, int? parentId) =>
        SendAsync(who.PutAsJsonAsync($"/api/suppliers/{memberId}/group", new { parentSupplierId = parentId }, Ct));

    [Fact]
    public async Task A_franchise_is_a_group_whose_head_companys_contracts_cover_every_franchisee()
    {
        var u = Unique("f");
        var owner = await PersonAsync("Gus Group", owner: true);
        var procurement = await PersonAsync("Pru Procure", procurement: true);
        var head = await SupplierAsync(owner, $"Acme Franchising {u}", $"H{u}");
        var toowoomba = await SupplierAsync(owner, $"Acme Toowoomba {u}", $"T{u}");
        var rocky = await SupplierAsync(owner, $"Acme Rockhampton {u}", $"R{u}");

        // Only Procurement and admins put suppliers in groups; groups go one level.
        Assert.Equal(HttpStatusCode.Forbidden, (await GroupAsync(owner, toowoomba, head)).Status);
        await OkAsync(procurement.PutAsJsonAsync($"/api/suppliers/{toowoomba}/group", new { parentSupplierId = head }, Ct));
        await OkAsync(procurement.PutAsJsonAsync($"/api/suppliers/{rocky}/group", new { parentSupplierId = head }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, (await GroupAsync(procurement, head, rocky)).Status);      // the head has members
        Assert.Equal(HttpStatusCode.BadRequest, (await GroupAsync(procurement, toowoomba, toowoomba)).Status);
        var other = await SupplierAsync(owner, $"Other {u}");
        var (deep, deepBody) = await GroupAsync(procurement, other, rocky);                                  // a member isn't a group
        Assert.Equal(HttpStatusCode.BadRequest, deep);
        Assert.Contains("one level", deepBody.GetProperty("message").GetString());

        // The head company's contract shows on each franchisee's page, marked as the group's.
        var contract = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"Franchise agreement {u}", supplierId = head }, Ct))).GetProperty("id").GetInt32();
        var page = await OkAsync(owner.GetAsync($"/api/suppliers/{toowoomba}", Ct));
        Assert.Equal($"Acme Franchising {u}", page.GetProperty("supplier").GetProperty("parentSupplierName").GetString());
        var listed = page.GetProperty("contracts").EnumerateArray().Single();
        Assert.Equal((contract, true), (listed.GetProperty("id").GetInt32(), listed.GetProperty("viaGroup").GetBoolean()));
        var headPage = await OkAsync(owner.GetAsync($"/api/suppliers/{head}", Ct));
        Assert.Equal(2, headPage.GetProperty("members").GetArrayLength());
        var all = (await OkAsync(owner.GetAsync("/api/suppliers", Ct))).EnumerateArray().ToDictionary(s => s.GetProperty("id").GetInt32());
        Assert.Equal(2, all[head].GetProperty("memberCount").GetInt32());
        Assert.Equal(1, all[rocky].GetProperty("contractCount").GetInt32());

        // Taken out: audited, and the contract no longer covers it.
        await OkAsync(procurement.PutAsJsonAsync($"/api/suppliers/{rocky}/group", new { parentSupplierId = (int?)null }, Ct));
        Assert.Empty((await OkAsync(owner.GetAsync($"/api/suppliers/{rocky}", Ct))).GetProperty("contracts").EnumerateArray());
        await using var db = await app.OpenAsync();
        var trail = (await db.QueryAsync<string>("SELECT Summary FROM contracts.AuditEvent WHERE SubjectType = N'Supplier' AND SubjectId = @rocky AND Action = N'supplier.group-changed' ORDER BY Id", new { rocky })).ToList();
        Assert.Equal(2, trail.Count);
        Assert.Contains("took", trail[1]);
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task The_supplier_sheet_matches_by_SAP_vendor_number_keeps_the_apps_name_and_sets_groups()
    {
        var u = Unique("x");
        var admin = await PersonAsync("Ada Admin", owner: true, admin: true);
        await SupplierAsync(admin, $"Acme Franchising {u}", $"H{u}");
        await SupplierAsync(admin, $"Named Only {u}");

        var file = ExcelTests.Sheet(["Name", "ABN", "SAP vendor number", "Part of"],
            [$"ACME FRANCHISING PTY LTD {u}", "51 824 753 556", $"H{u}", null],       // found by number: keeps its name, gets the ABN
            [$"Acme Toowoomba {u}", null, $"T{u}", $"H{u}"],                           // new, in the head's group (by its number)
            [$"Acme Mackay {u}", null, $"M{u}", $"Acme Newco {u}"],                    // new, in a group added further down
            [$"Acme Newco {u}", null, $"N{u}", null],
            [$"Named Only {u}", null, $"H{u}", null],                                   // the number is another supplier's: refused
            [$"Loop {u}", null, null, $"Acme Toowoomba {u}"],                           // Toowoomba is a member, not a group: refused
            [$"Orphan {u}", null, null, $"Bad ABN {u}"],                                // its group's row is refused: refused too
            [$"Bad ABN {u}", "123", null, null]);
        var (_, result) = await ExcelTests.PostAsync(admin, "suppliers", "import", file);
        Assert.Equal((3, 1, 4), (ExcelTests.Count(result, "added"), ExcelTests.Count(result, "updated"), ExcelTests.Count(result, "refused")));
        var notes = string.Join(" ", result.GetProperty("rows").EnumerateArray().SelectMany(r => r.GetProperty("notes").EnumerateArray().Select(n => n.GetString())));
        Assert.Contains("keeps the name", notes);
        Assert.Contains($"is Acme Franchising {u}'s", notes);
        Assert.Contains("one level", notes);
        Assert.Contains("isn't being added", notes);

        await using var db = await app.OpenAsync();
        var rows = (await db.QueryAsync<(int Id, string Name, string? Abn, string? Parent)>("""
            SELECT s.Id, s.Name, s.Abn, p.Name FROM contracts.Supplier s LEFT JOIN contracts.Supplier p ON p.Id = s.ParentSupplierId WHERE s.Name LIKE @like
            """, new { like = $"%{u}" })).ToDictionary(r => r.Name);
        Assert.Equal("51824753556", rows[$"Acme Franchising {u}"].Abn);
        Assert.False(rows.ContainsKey($"ACME FRANCHISING PTY LTD {u}"));
        Assert.Equal($"Acme Franchising {u}", rows[$"Acme Toowoomba {u}"].Parent);
        Assert.Equal($"Acme Newco {u}", rows[$"Acme Mackay {u}"].Parent);
        Assert.Null(rows[$"Named Only {u}"].Abn);

        // Contacts can name their supplier by SAP vendor number; the export writes the group.
        var (_, contacts) = await ExcelTests.PostAsync(admin, "contacts", "import", ExcelTests.Sheet(["Supplier", "Name", "Email"], [$"T{u}", "Tia Contact", "tia@example.com"]));
        Assert.Equal(1, ExcelTests.Count(contacts, "added"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.SupplierContact WHERE SupplierId = @id AND Name = N'Tia Contact'", new { id = rows[$"Acme Toowoomba {u}"].Id }));
        using var book = new ClosedXML.Excel.XLWorkbook(await (await admin.GetAsync("/api/excel/suppliers", Ct)).Content.ReadAsStreamAsync(Ct));
        var sheet = book.Worksheet(1);
        var partOf = sheet.Row(1).CellsUsed().Single(c => c.GetString() == "Part of").Address.ColumnNumber;
        Assert.Equal($"Acme Franchising {u}", sheet.RowsUsed().Single(r => r.Cell(1).GetString() == $"Acme Toowoomba {u}").Cell(partOf).GetString());
    }
}
