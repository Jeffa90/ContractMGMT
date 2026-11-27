using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

// The setting is shared by every contract, so these run on their own, after the others.
[CollectionDefinition(nameof(ContractTitleTests), DisableParallelization = true)]
public sealed class ContractTitlesAlone;

/// <summary>
/// D-131, D-132: while the admin setting is on (it is in a new database), a contract's title is made from its details on the server,
/// Supplier_Agreement type_What it covers, whatever the form sends; switched off, people's own titles are kept. Admins only; audited.
/// </summary>
[Collection(nameof(ContractTitleTests))]
public sealed class ContractTitleTests(AppFixture app)
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

    [Fact]
    public async Task While_on_the_title_is_made_from_the_details_and_people_cant_type_their_own()
    {
        var u = Unique("t");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var user = Unique("ct");
        await app.AddPersonAsync(user, $"Tia Title {u[^4..]}", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var owner = app.ClientFor($@"TEST\{user}");
        var family = await app.FamilyAsync();
        int Id(JsonElement e) => e.GetProperty("id").GetInt32();
        var casings = Id(await OkAsync(admin.PostAsJsonAsync("/api/categories", new { name = $"Casings {u}", parentId = family }, Ct)));
        var collagen = Id(await OkAsync(admin.PostAsJsonAsync("/api/categories", new { name = "Collagen Casing", parentId = casings }, Ct)));
        var other = Id(await OkAsync(admin.PostAsJsonAsync("/api/categories", new { name = "Other", parentId = casings }, Ct)));
        var supplier = Id(await OkAsync(owner.PostAsJsonAsync("/api/suppliers", new { name = $"Acme {u}" }, Ct)));
        await using var db = await app.OpenAsync();
        var type = await db.ExecuteScalarAsync<int>("INSERT INTO contracts.ListItem (ListName, Value, SortOrder) OUTPUT INSERTED.Id VALUES (N'AgreementType', @v, 99)", new { v = $"Supply {u}" });

        // Only admins switch it; it's on.
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(owner.PutAsJsonAsync("/api/settings/title-from-details", new { fromDetails = true }, Ct))).Status);
        await OkAsync(admin.PutAsJsonAsync("/api/settings/title-from-details", new { fromDetails = true }, Ct));
        try
        {
            Assert.True((await OkAsync(owner.GetAsync("/api/layout", Ct))).GetProperty("titleFromDetails").GetBoolean());

            // Nothing to make it from: refused, saying why. Otherwise the typed title is replaced.
            var (status, body) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "My own title" }, Ct));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("the title is made from them", body.GetProperty("message").GetString());
            var id = Id(await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "My own title", supplierId = supplier, agreementTypeId = type, categoryId = collagen }, Ct)));
            var k = (await OkAsync(owner.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract");
            Assert.Equal($"Acme {u}_Supply {u}_Collagen Casing", k.GetProperty("title").GetString());

            // Edited, it follows the details; "Other" uses its category.
            await OkAsync(owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Typed again", supplierId = supplier, agreementTypeId = type, categoryId = other, rowVersion = k.GetProperty("rowVersion").GetString() }, Ct));
            k = (await OkAsync(owner.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract");
            Assert.Equal($"Acme {u}_Supply {u}_Casings {u}", k.GetProperty("title").GetString());

            // Off: people's own titles are kept.
            await OkAsync(admin.PutAsJsonAsync("/api/settings/title-from-details", new { fromDetails = false }, Ct));
            await OkAsync(owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Collagen casings 2027", supplierId = supplier, agreementTypeId = type, categoryId = other, rowVersion = k.GetProperty("rowVersion").GetString() }, Ct));
            Assert.Equal("Collagen casings 2027", (await OkAsync(owner.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract").GetProperty("title").GetString());
            Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent WHERE Action = N'settings.title' AND Summary LIKE N'%Alex%'"));
            Assert.Null(await AuditLog.VerifyAsync(db));
        }
        finally
        {
            await db.ExecuteAsync("UPDATE contracts.AppSetting SET Value = N'No' WHERE Name = N'TitleFromDetails'");
        }
    }
}
