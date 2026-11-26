using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>D-111: on QA an admin clears contracts, suppliers and contract lists for a fresh import; people and settings stay.</summary>
[Collection(nameof(DemoDataTests))] // it clears the whole test database, so nothing else runs alongside
public sealed class QaClearTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    [Fact]
    public async Task An_admin_on_QA_clears_contracts_suppliers_and_lists_and_numbering_starts_again()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var ownerUser = Unique("qc");
        await app.AddPersonAsync(ownerUser, "Quinn Clear", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var owner = app.ClientFor($@"TEST\{ownerUser}");
        // Something of everything: a list value, a category, a supplier with a contact, a contract with a document and an approval decision.
        var site = (await (await admin.PostAsJsonAsync("/api/lists/items", new { listName = "Site", value = Unique("Site ") }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        var cat = (await (await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat "), parentId = await app.FamilyAsync() }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        var sid = (await (await owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        Assert.True((await admin.PostAsJsonAsync($"/api/suppliers/{sid}/contacts", new { name = "Casey Contact" }, Ct)).IsSuccessStatusCode);
        var id = (await (await owner.PostAsJsonAsync("/api/contracts", new { title = "To be cleared", supplierId = sid, categoryId = cat, siteIds = new[] { site } }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetInt32();
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("%PDF clear me"u8.ToArray()), "file", "draft.pdf");
            form.Add(new StringContent("Draft"), "kind");
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
        }
        await using var db = await app.OpenAsync();
        await db.ExecuteAsync("INSERT INTO contracts.Approval (ContractId, Decision, ById, Reason) VALUES (@id, N'SentBack', (SELECT TOP 1 Id FROM contracts.Person), N'test')", new { id });
        // Approval decisions are still append-only outside the clear.
        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => db.ExecuteAsync("DELETE FROM contracts.Approval WHERE ContractId = @id", new { id }));

        // Admins only, and only with CLEAR typed.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/admin/qa-clear", new { confirm = "CLEAR" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/qa-clear", new { confirm = "clear" }, Ct)).StatusCode);
        var people = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person");
        var currencies = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WHERE ListName = N'Currency'");

        var result = await admin.PostAsJsonAsync("/api/admin/qa-clear", new { confirm = "CLEAR" }, Ct);
        Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync(Ct));
        foreach (var table in new[] { "Contract", "Document", "DocumentVersion", "Approval", "Supplier", "SupplierContact", "Category" })
            Assert.Equal(0, await db.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM contracts.{table}"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WHERE ListName IN (N'Site', N'AgreementType', N'BusinessUnit', N'Entity', N'Function')"));
        Assert.Equal(people, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person"));
        Assert.Equal(currencies, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ListItem WHERE ListName = N'Currency'"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent WHERE Action = N'qa.cleared'"));
        Assert.Null(await AuditLog.VerifyAsync(db));

        // Numbering starts again, and the append-only rules are back.
        var next = await (await owner.PostAsJsonAsync("/api/contracts", new { title = "After the clear" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("JBSAUS0000001", next.GetProperty("reference").GetString());
        var newId = next.GetProperty("id").GetInt32();
        await db.ExecuteAsync("INSERT INTO contracts.Approval (ContractId, Decision, ById, Reason) VALUES (@newId, N'SentBack', (SELECT TOP 1 Id FROM contracts.Person), N'test')", new { newId });
        await Assert.ThrowsAnyAsync<Microsoft.Data.SqlClient.SqlException>(() => db.ExecuteAsync("DELETE FROM contracts.Approval WHERE ContractId = @newId", new { newId }));
    }
}
