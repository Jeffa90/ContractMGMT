using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;

namespace JbsContracts.Api.Tests;

/// <summary>D-119: the By supplier report lists each supplier a contract covers, its group's members included, for people who can see reports.</summary>
public sealed class BySupplierReportTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> request)
    {
        var r = await request;
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task A_groups_contract_lists_the_head_company_and_every_member_with_their_vendor_numbers()
    {
        var u = Unique("b");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var plainUser = Unique("bp");
        await app.AddPersonAsync(plainUser, "Pat Plain", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var plain = app.ClientFor($@"TEST\{plainUser}");
        async Task<int> Supplier(string name, string vendor) => (await OkAsync(admin.PostAsJsonAsync("/api/suppliers", new { name, vendorNumber = vendor }, Ct))).GetProperty("id").GetInt32();
        var head = await Supplier($"Head {u}", $"H{u}");
        var one = await Supplier($"Member one {u}", $"A{u}");
        var two = await Supplier($"Member two {u}", $"B{u}");
        var alone = await Supplier($"Alone {u}", $"C{u}");
        foreach (var m in new[] { one, two }) await OkAsync(admin.PutAsJsonAsync($"/api/suppliers/{m}/group", new { parentSupplierId = head }, Ct));
        var grouped = (await OkAsync(admin.PostAsJsonAsync("/api/contracts", new { title = $"Franchise {u}", supplierId = head }, Ct))).GetProperty("id").GetInt32();
        var single = (await OkAsync(admin.PostAsJsonAsync("/api/contracts", new { title = $"Single {u}", supplierId = alone }, Ct))).GetProperty("id").GetInt32();

        // Drafts are in "everything"; only groups by default.
        var report = await OkAsync(admin.GetAsync("/api/reports/by-supplier?scope=everything", Ct));
        var mine = report.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("contractId").GetInt32() is var id && (id == grouped || id == single)).ToList();
        Assert.Equal([($"Head {u}", $"H{u}", false), ($"Member one {u}", $"A{u}", true), ($"Member two {u}", $"B{u}", true)],
            mine.Select(r => (r.GetProperty("supplierName").GetString()!, r.GetProperty("vendorNumber").GetString()!, r.GetProperty("viaGroup").GetBoolean())));
        Assert.All(mine, r => Assert.Equal($"Head {u}", r.GetProperty("groupName").GetString()));
        var every = await OkAsync(admin.GetAsync("/api/reports/by-supplier?scope=everything&groupsOnly=false", Ct));
        Assert.Contains(every.GetProperty("rows").EnumerateArray(), r => r.GetProperty("contractId").GetInt32() == single);

        // Excel, one row per supplier; people without reports can't see it.
        using var book = new XLWorkbook(await (await admin.GetAsync("/api/reports/by-supplier.xlsx?scope=everything", Ct)).Content.ReadAsStreamAsync(Ct));
        Assert.Equal(3, book.Worksheet(1).RowsUsed().Count(r => r.Cell(2).GetString() == $"Franchise {u}"));
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/reports/by-supplier", Ct)).StatusCode);
    }
}
