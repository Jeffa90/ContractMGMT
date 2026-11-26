using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using JbsContracts.Api.Features;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>
/// D-114: business unit, site, entity and function each take several values on a contract, everywhere they're read or written:
/// the form, filters, Activity, Excel (several in one cell), Variations and renewals.
/// </summary>
public sealed class MultiChoiceListsTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

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

    private async Task<HttpClient> PersonAsync(string name, bool owner = false, bool approver = false, bool admin = false)
    {
        var user = Unique("mc");
        await app.AddPersonAsync(user, $"{name} {user[^4..]}", new { contractOwner = owner, legal = false, approver, groupHead = false, admin, auditor = false });
        return app.ClientFor($@"TEST\{user}");
    }

    private static async Task<int> ItemAsync(HttpClient admin, string list, string value) =>
        (await OkAsync(admin.PostAsJsonAsync("/api/lists/items", new { listName = list, value }, Ct))).GetProperty("id").GetInt32();

    private static async Task<JsonElement> ContractAsync(HttpClient who, int id) => (await OkAsync(who.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract");

    private static int[] Ids(JsonElement c, string key) => [.. c.GetProperty(key).EnumerateArray().Select(x => x.GetInt32()).Order()];

    private static async Task ActAsync(HttpClient who, int id, string action, object? extra = null)
    {
        var b = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
        b["rowVersion"] = (await ContractAsync(who, id)).GetProperty("rowVersion").GetString();
        await OkAsync(who.PostAsJsonAsync($"/api/contracts/{id}/{action}", b, Ct));
    }

    [Fact]
    public async Task A_contract_has_several_sites_business_units_entities_and_functions()
    {
        var u = Unique("s");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var owner = await PersonAsync("Mae Multi", owner: true);
        var (a, b, c) = (await ItemAsync(admin, "Site", $"Alpha, QLD {u}"), await ItemAsync(admin, "Site", $"Bravo {u}"), await ItemAsync(admin, "Site", $"Charlie {u}"));
        var unit = await ItemAsync(admin, "BusinessUnit", $"Unit {u}");
        var entity = await ItemAsync(admin, "Entity", $"Entity {u}");
        var (f1, f2) = (await ItemAsync(admin, "Function", $"Fn one {u}"), await ItemAsync(admin, "Function", $"Fn two {u}"));

        // Saved with several each, shown in the lists' order and joined for reading.
        var id = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Multi {u}", siteIds = new[] { b, a, a }, businessUnitIds = new[] { unit }, entityIds = new[] { entity }, functionIds = new[] { f2, f1 },
        }, Ct))).GetProperty("id").GetInt32();
        var k = await ContractAsync(owner, id);
        Assert.Equal([a, b], Ids(k, "siteIds"));
        Assert.Equal($"Alpha, QLD {u}, Bravo {u}", k.GetProperty("siteName").GetString());
        Assert.Equal([f1, f2], Ids(k, "functionIds"));
        Assert.Equal([unit], Ids(k, "businessUnitIds"));

        // Only values from the right list; none is fine.
        var (wrong, wrongBody) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "Wrong", siteIds = new[] { a, unit } }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, wrong);
        Assert.True(wrongBody.GetProperty("fields").TryGetProperty("siteIds", out _));
        var none = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"None {u}" }, Ct))).GetProperty("id").GetInt32();
        Assert.Empty(Ids(await ContractAsync(owner, none), "siteIds"));

        // A hidden value stays on the contract that has it, but can't be newly picked.
        await OkAsync(admin.PutAsJsonAsync($"/api/lists/items/{a}", new { listName = "Site", value = $"Alpha, QLD {u}", isActive = false }, Ct));
        var body = new { title = $"Multi {u}", siteIds = new[] { a, c }, businessUnitIds = new[] { unit }, entityIds = new[] { entity }, functionIds = new[] { f1, f2 }, rowVersion = k.GetProperty("rowVersion").GetString() };
        await OkAsync(owner.PutAsJsonAsync($"/api/contracts/{id}", body, Ct));
        Assert.Equal([a, c], Ids(await ContractAsync(owner, id), "siteIds"));
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(owner.PutAsJsonAsync($"/api/contracts/{none}",
            new { title = $"None {u}", siteIds = new[] { a }, rowVersion = (await ContractAsync(owner, none)).GetProperty("rowVersion").GetString() }, Ct))).Status);
        await OkAsync(admin.PutAsJsonAsync($"/api/lists/items/{a}", new { listName = "Site", value = $"Alpha, QLD {u}", isActive = true }, Ct));

        // Activity says what changed, in plain words.
        await using var db = await app.OpenAsync();
        var changed = await db.ExecuteScalarAsync<string>("SELECT TOP (1) Summary FROM contracts.AuditEvent WHERE ContractId = @id AND Action = N'contract.changed' ORDER BY Id DESC", new { id });
        Assert.Contains($"site from Alpha, QLD {u}, Bravo {u} to Alpha, QLD {u}, Charlie {u}", changed);

        // The contracts list finds it by any one of its sites.
        Assert.Contains(id, (await OkAsync(owner.GetAsync($"/api/contracts?view=all&f.site={c}", Ct))).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        Assert.DoesNotContain(id, (await OkAsync(owner.GetAsync($"/api/contracts?view=all&f.site={b}", Ct))).EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task Excel_writes_several_in_one_cell_and_reads_them_back()
    {
        var u = Unique("x");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var owner = await PersonAsync("Xan Excel", owner: true, admin: true);
        var (a, b, c) = (await ItemAsync(admin, "Site", $"Alpha, QLD {u}"), await ItemAsync(admin, "Site", $"Bravo {u}"), await ItemAsync(admin, "Site", $"Charlie {u}"));
        var created = await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"Sheet {u}", siteIds = new[] { a, b } }, Ct));
        var (id, reference) = (created.GetProperty("id").GetInt32(), created.GetProperty("reference").GetString()!);

        using (var book = new XLWorkbook(await (await owner.GetAsync("/api/excel/contracts", Ct)).Content.ReadAsStreamAsync(Ct)))
        {
            var sheet = book.Worksheet(1);
            var siteColumn = sheet.Row(1).CellsUsed().Single(x => x.GetString() == "Site").Address.ColumnNumber;
            var row = sheet.RowsUsed().Single(r => r.Cell(1).GetString() == reference);
            Assert.Equal($"Alpha, QLD {u}; Bravo {u}", row.Cell(siteColumn).GetString());
        }

        // A value with a comma in it stays whole; commas and semicolons both separate.
        var file = ExcelTests.Sheet(["Reference", "Site"], [reference, $"Alpha, QLD {u}; Charlie {u}"]);
        await OkAsync(Task.FromResult(await Post(owner, file)));
        Assert.Equal([a, c], Ids(await ContractAsync(owner, id), "siteIds"));
        file = ExcelTests.Sheet(["Reference", "Site"], [reference, $"Bravo {u}, Charlie {u}"]);
        await OkAsync(Task.FromResult(await Post(owner, file)));
        Assert.Equal([b, c], Ids(await ContractAsync(owner, id), "siteIds"));
        // A blank cell keeps what it has; an unknown value is refused by name.
        file = ExcelTests.Sheet(["Reference", "Title", "Site"], [reference, $"Sheet {u} renamed", null]);
        await OkAsync(Task.FromResult(await Post(owner, file)));
        Assert.Equal([b, c], Ids(await ContractAsync(owner, id), "siteIds"));
        var (_, refused) = await ExcelTests.PostAsync(owner, "contracts", "preview", ExcelTests.Sheet(["Reference", "Site"], [reference, $"Bravo {u}; Nowhere {u}"]));
        var problem = string.Join(" ", refused.GetProperty("rows").EnumerateArray().Single().GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
        Assert.Contains($"no site called \"Nowhere {u}\"", problem);
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, byte[] file)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(content, "file", "import.xlsx");
        var r = await client.PostAsync("/api/excel/contracts/import", form, Ct);
        await r.Content.LoadIntoBufferAsync(Ct);
        return r;
    }

    [Fact]
    public async Task A_Variation_changes_the_sites_and_a_renewal_keeps_them()
    {
        var u = Unique("v");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var owner = await PersonAsync("Vic Variation", owner: true);
        var approver = await PersonAsync("Val Approver", approver: true);
        var (a, b) = (await ItemAsync(admin, "Site", $"Alpha {u}"), await ItemAsync(admin, "Site", $"Bravo {u}"));
        var category = (await OkAsync(admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat"), parentId = await app.FamilyAsync() }, Ct))).GetProperty("id").GetInt32();
        var supplier = (await OkAsync(owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct))).GetProperty("id").GetInt32();
        var id = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Variation {u}", supplierId = supplier, categoryId = category, needsLegalReview = false, endDate = D(200), siteIds = new[] { a },
        }, Ct))).GetProperty("id").GetInt32();
        await ActAsync(owner, id, "ready-for-signing");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("%PDF signed"u8.ToArray()), "file", "signed.pdf");
            form.Add(new StringContent("SignedCopy"), "kind");
            await OkAsync(owner.PostAsync($"/api/contracts/{id}/documents", form, Ct));
        }
        await ActAsync(owner, id, "send-for-approval", new { effectiveDate = D(-10) });
        await ActAsync(approver, id, "approval/approve");

        var rv = (await ContractAsync(owner, id)).GetProperty("rowVersion").GetString();
        var vid = (await OkAsync(owner.PostAsJsonAsync($"/api/contracts/{id}/variations", new { reason = "Add Bravo", needsLegalReview = false, rowVersion = rv }, Ct)))
            .GetProperty("result").GetProperty("variationId").GetInt32();
        async Task<JsonElement> Variation() => await OkAsync(owner.GetAsync($"/api/contracts/{id}/variations/{vid}", Ct));
        var site = (await Variation()).GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "site");
        Assert.Equal("MultiChoice", site.GetProperty("kind").GetString());

        // The same set in another order is no change; a new set is one change, in plain words.
        async Task<(HttpStatusCode, JsonElement)> Put(object changes) => await SendAsync(owner.PutAsJsonAsync($"/api/contracts/{id}/variations/{vid}",
            new { rowVersion = (await Variation()).GetProperty("variation").GetProperty("rowVersion").GetString(), changes }, Ct));
        Assert.Equal(HttpStatusCode.OK, (await Put(new Dictionary<string, object?> { ["site"] = new[] { a.ToString() } })).Item1);
        Assert.Empty((await Variation()).GetProperty("changes").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(new Dictionary<string, object?> { ["site"] = new[] { "x" } })).Item1);
        Assert.Equal(HttpStatusCode.OK, (await Put(new Dictionary<string, object?> { ["site"] = new[] { b.ToString(), a.ToString() } })).Item1);
        var change = (await Variation()).GetProperty("changes").EnumerateArray().Single();
        Assert.Equal(($"Alpha {u}", $"Alpha {u}, Bravo {u}"), (change.GetProperty("old").GetString(), change.GetProperty("new").GetString()));

        async Task VAct(HttpClient who, string action) => await OkAsync(who.PostAsJsonAsync($"/api/contracts/{id}/variations/{vid}/{action}",
            new { rowVersion = (await OkAsync(who.GetAsync($"/api/contracts/{id}/variations/{vid}", Ct))).GetProperty("variation").GetProperty("rowVersion").GetString() }, Ct));
        await VAct(owner, "send-for-approval");
        Assert.Equal([a], Ids(await ContractAsync(owner, id), "siteIds")); // nothing changes until approved
        await VAct(approver, "approve");
        Assert.Equal([a, b], Ids(await ContractAsync(owner, id), "siteIds"));

        // Renewing copies them to the new draft.
        await ActAsync(owner, id, "renewal-decision", new { decision = "Renew" });
        await using var db = await app.OpenAsync();
        var renewal = await db.ExecuteScalarAsync<int>("SELECT Id FROM contracts.Contract WHERE RenewsContractId = @id", new { id });
        Assert.Equal([a, b], Ids(await ContractAsync(owner, renewal), "siteIds"));
    }
}
