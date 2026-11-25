using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace JbsContracts.Api.Tests;

/// <summary>Admins add their own contract document kinds and supplier risk levels (D-98); the built-in ones stay as they are.</summary>
public sealed class AddedListTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, int id, string kind)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF test"u8.ToArray()), "file", "certificate.pdf");
        form.Add(new StringContent(kind), "kind");
        return await client.PostAsync($"/api/contracts/{id}/documents", form, Ct);
    }

    [Fact]
    public async Task An_added_document_kind_can_be_used_renamed_and_hidden()
    {
        var u = Guid.NewGuid().ToString("N")[..6];
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/lists/items", new { listName = "DocumentKind", value = "signed copy" }, Ct)).StatusCode);
        var added = await ReadAsync(await admin.PostAsJsonAsync("/api/lists/items", new { listName = "DocumentKind", value = $"Insurance certificate {u}" }, Ct));
        var itemId = added.GetProperty("id").GetInt32();

        var user = $"dk{u}";
        await app.AddPersonAsync(user, $"Dana Kind {u}", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var owner = app.ClientFor($@"TEST\{user}");
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = $"Cat {u}" }, Ct));
        var id = (await ReadAsync(await owner.PostAsJsonAsync("/api/contracts", new { title = $"Kinds {u}", categoryId = category.GetProperty("id").GetInt32() }, Ct))).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, id, $"L{itemId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, id, "L999999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, id, "Nonsense")).StatusCode);
        string KindName(JsonElement detail) => detail.GetProperty("documents").EnumerateArray().Single().GetProperty("kindName").GetString()!;
        Assert.Equal($"Insurance certificate {u}", KindName(await ReadAsync(await owner.GetAsync($"/api/contracts/{id}", Ct))));

        // Renamed: everywhere. Hidden: not offered for new documents, but older ones keep their name.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/lists/items/{itemId}", new { value = $"Certificate of currency {u}" }, Ct)).StatusCode);
        Assert.Equal($"Certificate of currency {u}", KindName(await ReadAsync(await owner.GetAsync($"/api/contracts/{id}", Ct))));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/lists/items/{itemId}", new { isActive = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, id, $"L{itemId}")).StatusCode);
        Assert.Equal($"Certificate of currency {u}", KindName(await ReadAsync(await owner.GetAsync($"/api/contracts/{id}", Ct))));
    }

    [Fact]
    public async Task An_added_risk_level_asks_why_and_shows_its_name()
    {
        var u = Guid.NewGuid().ToString("N")[..6];
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/lists/items", new { listName = "SupplierRisk", value = "HIGH" }, Ct)).StatusCode);
        var itemId = (await ReadAsync(await admin.PostAsJsonAsync("/api/lists/items", new { listName = "SupplierRisk", value = $"Critical {u}" }, Ct))).GetProperty("id").GetInt32();
        var supplierId = (await ReadAsync(await admin.PostAsJsonAsync("/api/suppliers", new { name = $"Risky Supplier {u}" }, Ct))).GetProperty("id").GetInt32();

        async Task<(HttpStatusCode, JsonElement)> SaveAsync(string? risk, string? reason)
        {
            var page = await ReadAsync(await admin.GetAsync($"/api/suppliers/{supplierId}", Ct));
            var r = await admin.PutAsJsonAsync($"/api/suppliers/{supplierId}/relationship",
                new { rowVersion = page.GetProperty("supplier").GetProperty("rowVersion").GetString(), riskRating = risk, riskReason = reason }, Ct);
            return (r.StatusCode, await ReadAsync(r));
        }
        var (noReason, problem) = await SaveAsync($"L{itemId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, noReason);
        Assert.True(problem.GetProperty("fields").TryGetProperty("riskReason", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync("L999999", "Because")).Item1);
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync($"L{itemId}", "Sole supplier of a critical part.")).Item1);
        var supplier = (await ReadAsync(await admin.GetAsync($"/api/suppliers/{supplierId}", Ct))).GetProperty("supplier");
        Assert.Equal($"L{itemId}", supplier.GetProperty("riskRating").GetString());
        Assert.Equal($"Critical {u}", supplier.GetProperty("riskName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync("Low", null)).Item1); // Low needs no reason
    }
}
