using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>Admin contract fields (slice 2). One test class: the layout is shared, so these run one after another.</summary>
public sealed class LayoutTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private async Task<JsonObject> LayoutAsync(HttpClient client) =>
        JsonNode.Parse(await client.GetStringAsync("/api/layout", Ct))!.AsObject();

    /// <summary>The layout in the shape it is saved: sections in order, fields in order.</summary>
    private static JsonObject ToSave(JsonObject layout) => new()
    {
        ["versionId"] = layout["versionId"]!.GetValue<int>(),
        ["sections"] = new JsonArray(layout["sections"]!.AsArray().Select(s => (JsonNode)new JsonObject
        {
            ["key"] = s!["key"]!.GetValue<string>(), ["title"] = s["title"]!.GetValue<string>(), ["isHidden"] = s["isHidden"]!.GetValue<bool>(),
        }).ToArray()),
        ["fields"] = new JsonArray(layout["fields"]!.AsArray().Select(f => (JsonNode)new JsonObject
        {
            ["key"] = f!["key"]!.GetValue<string>(), ["kind"] = f["kind"]!.GetValue<string>(), ["label"] = f["label"]!.GetValue<string>(),
            ["help"] = f["help"]?.GetValue<string>(), ["sectionKey"] = f["sectionKey"]!.GetValue<string>(), ["isRequired"] = f["isRequired"]!.GetValue<bool>(),
            ["isHidden"] = f["isHidden"]!.GetValue<bool>(), ["showInList"] = f["showInList"]!.GetValue<bool>(), ["filterable"] = f["filterable"]!.GetValue<bool>(),
            ["inExport"] = f["inExport"]!.GetValue<bool>(), ["afterEffective"] = f["afterEffective"]!.GetValue<string>(),
            ["choices"] = f["choices"]!.DeepClone(), ["maxLength"] = f["maxLength"]?.DeepClone(), ["minValue"] = f["minValue"]?.DeepClone(), ["maxValue"] = f["maxValue"]?.DeepClone(),
        }).ToArray()),
    };

    private async Task<(HttpStatusCode Status, JsonObject Body)> SaveAsync(HttpClient client, JsonObject layout)
    {
        var response = await client.PutAsJsonAsync("/api/layout", layout, Ct);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject());
    }

    private static JsonObject Field(JsonObject save, Func<JsonNode, bool> where) => save["fields"]!.AsArray().First(f => where(f!))!.AsObject();

    [Fact]
    public async Task An_admin_adds_sections_and_fields_and_contracts_use_them_with_plain_words_audit()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var save = ToSave(await LayoutAsync(admin));
        var section = Unique("Insurance");
        var (fieldA, fieldB, fieldC) = (Unique("Cover type"), Unique("Expiry"), Unique("Policy link"));
        save["sections"]!.AsArray().Add(new JsonObject { ["key"] = "new-1", ["title"] = section, ["isHidden"] = false });
        save["fields"]!.AsArray().Add(new JsonObject
        {
            ["kind"] = "Choice", ["label"] = fieldA, ["sectionKey"] = "new-1", ["isRequired"] = false, ["isHidden"] = false, ["showInList"] = true, ["filterable"] = true, ["inExport"] = true,
            ["choices"] = new JsonArray(new JsonObject { ["label"] = "Public liability" }, new JsonObject { ["label"] = "Product liability" }),
        });
        save["fields"]!.AsArray().Add(new JsonObject { ["kind"] = "Date", ["label"] = fieldB, ["sectionKey"] = "new-1", ["isRequired"] = false, ["isHidden"] = false, ["showInList"] = false, ["filterable"] = false, ["inExport"] = true, ["afterEffective"] = "AnyTime" });
        save["fields"]!.AsArray().Add(new JsonObject { ["kind"] = "Link", ["label"] = fieldC, ["sectionKey"] = "new-1", ["isRequired"] = false, ["isHidden"] = false, ["showInList"] = false, ["filterable"] = false, ["inExport"] = true });
        // Rename a built-in field too.
        Field(save, f => f["key"]!.GetValue<string>() == "sapOutlineAgreement")["label"] = "SAP outline agreement";
        var (status, saved) = await SaveAsync(admin, save);
        Assert.True(status == HttpStatusCode.OK, saved.ToJsonString());
        var fields = saved["fields"]!.AsArray();
        var keyA = fields.First(f => f!["label"]!.GetValue<string>() == fieldA)!["key"]!.GetValue<string>();
        var keyB = fields.First(f => f!["label"]!.GetValue<string>() == fieldB)!["key"]!.GetValue<string>();
        var keyC = fields.First(f => f!["label"]!.GetValue<string>() == fieldC)!["key"]!.GetValue<string>();
        var choice = fields.First(f => f!["key"]!.GetValue<string>() == keyA)!["choices"]![1]!["key"]!.GetValue<string>();

        var user = Unique("fo");
        await app.AddPersonAsync(user, "Fiona Fields", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var owner = app.ClientFor($@"TEST\{user}");
        // Bad values get plain messages, by field.
        var bad = await owner.PostAsJsonAsync("/api/contracts", new { title = "Fielded", fields = new Dictionary<string, object> { [keyA] = "nope", [keyC] = "not a link" } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var badFields = (await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("fields");
        Assert.True(badFields.TryGetProperty($"field.{keyA}", out _));
        Assert.True(badFields.TryGetProperty($"field.{keyC}", out _));

        var created = await owner.PostAsJsonAsync("/api/contracts", new { title = "Fielded", fields = new Dictionary<string, object> { [keyA] = choice, [keyB] = "2027-06-30" } }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/contracts/{id}", Ct);
        Assert.Equal("Product liability", detail.GetProperty("fieldValues").GetProperty(keyA).GetProperty("display").GetString());
        Assert.Equal("30 Jun 2027", detail.GetProperty("fieldValues").GetProperty(keyB).GetProperty("display").GetString());

        var rowVersion = detail.GetProperty("contract").GetProperty("rowVersion").GetString();
        var edit = await owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Fielded", rowVersion, fields = new Dictionary<string, object?> { [keyB] = null, [keyC] = "https://jbs.sharepoint.com/sites/contracts" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        await using var db = await app.OpenAsync();
        var summary = await db.QuerySingleAsync<string>("SELECT TOP 1 Summary FROM contracts.AuditEvent WHERE ContractId = @id ORDER BY Id DESC", new { id });
        Assert.Contains($"cleared {fieldB}", summary);
        Assert.Contains($"set {fieldC} to https://jbs.sharepoint.com/sites/contracts", summary);

        // Offered as a list column: the list carries its value.
        var list = await owner.GetFromJsonAsync<JsonElement>("/api/contracts?view=mine", Ct);
        var row = list.EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == id);
        Assert.Equal("Product liability", row.GetProperty("fields").GetProperty(keyA).GetString());

        // ---- Rules
        // Rule fields can't be hidden.
        var layout = ToSave(await LayoutAsync(admin));
        Field(layout, f => f["key"]!.GetValue<string>() == "endDate")["isHidden"] = true;
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(admin, layout)).Status);
        // A field's kind can't change once it has values.
        layout = ToSave(await LayoutAsync(admin));
        Field(layout, f => f["key"]!.GetValue<string>() == keyA)["kind"] = "ShortText";
        var (kindStatus, kindBody) = await SaveAsync(admin, layout);
        Assert.Equal(HttpStatusCode.BadRequest, kindStatus);
        Assert.Contains("already has values", kindBody.ToJsonString());
        // A stale version is refused.
        layout = ToSave(await LayoutAsync(admin));
        layout["versionId"] = layout["versionId"]!.GetValue<int>() - 1;
        Assert.Equal(HttpStatusCode.Conflict, (await SaveAsync(admin, layout)).Status);
        // Only admins change the layout.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/layout", ToSave(await LayoutAsync(owner)), Ct)).StatusCode);

        // ---- Removing hides and keeps values; history puts it back.
        layout = ToSave(await LayoutAsync(admin));
        var versionWithField = layout["versionId"]!.GetValue<int>();
        Field(layout, f => f["key"]!.GetValue<string>() == keyA)["isHidden"] = true;
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(admin, layout)).Status);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ContractFieldValue v JOIN contracts.FieldDefinition f ON f.Id = v.FieldId WHERE f.[Key] = @keyA", new { keyA }));
        rowVersion = (await owner.GetFromJsonAsync<JsonElement>($"/api/contracts/{id}", Ct)).GetProperty("contract").GetProperty("rowVersion").GetString();
        var toHidden = await owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Fielded", rowVersion, fields = new Dictionary<string, object> { [keyA] = choice } }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, toHidden.StatusCode);

        var history = await admin.GetFromJsonAsync<JsonElement>("/api/layout/history", Ct);
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("summary").GetString()!.Contains($"removed {fieldA}"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/layout/history/{versionWithField}/restore", null, Ct)).StatusCode);
        var restored = await LayoutAsync(admin);
        Assert.False(restored["fields"]!.AsArray().First(f => f!["key"]!.GetValue<string>() == keyA)!["isHidden"]!.GetValue<bool>());
        Assert.Null(await AuditLog.VerifyAsync(db));
    }
}
