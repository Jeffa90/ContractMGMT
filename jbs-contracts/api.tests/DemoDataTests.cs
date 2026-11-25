using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>Demo data (D-66): admins only, once, fictional, and it never removes or changes anything already there.</summary>
public sealed class DemoDataTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_admin_loads_fictional_demo_data_once_without_touching_existing_people()
    {
        var user = $"keep{Guid.NewGuid():N}"[..14];
        var existing = await app.AddPersonAsync(user, "Kept Person", new { contractOwner = true, legal = true, approver = true, groupHead = false, admin = false, auditor = false });
        await using var db = await app.OpenAsync();
        var peopleBefore = (await db.QueryAsync<(int Id, string Name, bool IsContractOwner, bool IsLegal, bool IsApprover, bool IsGroupHead, bool IsAdmin, bool IsActive)>(
            "SELECT Id, Name, IsContractOwner, IsLegal, IsApprover, IsGroupHead, IsAdmin, IsActive FROM contracts.Person")).ToList();

        // Only admins.
        using var owner = app.ClientFor($@"TEST\{user}");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/admin/demo-data", null, Ct)).StatusCode);

        using var admin = app.ClientFor(AppFixture.AdminAccount);
        Assert.Equal("available", (await admin.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("demoData").GetString());
        var load = await admin.PostAsync("/api/admin/demo-data", null, Ct);
        Assert.True(load.StatusCode == HttpStatusCode.OK, await load.Content.ReadAsStringAsync(Ct));
        Assert.Equal("loaded", (await admin.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("demoData").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/demo-data", null, Ct)).StatusCode); // once only

        // People who were there are still there, unchanged (other tests change their own people in parallel, so check the ones
        // this test controls: an existing person and the admin), and nobody was removed.
        var peopleAfter = (await db.QueryAsync<(int Id, string Name, bool IsContractOwner, bool IsLegal, bool IsApprover, bool IsGroupHead, bool IsAdmin, bool IsActive)>(
            "SELECT Id, Name, IsContractOwner, IsLegal, IsApprover, IsGroupHead, IsAdmin, IsActive FROM contracts.Person")).ToList();
        Assert.Contains(peopleBefore.Single(p => p.Name == "Kept Person"), peopleAfter);
        Assert.Contains(peopleBefore.Single(p => p.Name == AppFixture.AdminName), peopleAfter);
        Assert.All(peopleBefore, p => Assert.Contains(peopleAfter, a => a.Id == p.Id));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent WHERE Action LIKE N'person.%' AND Summary LIKE N'%(demo)%'"));
        Assert.Equal(4, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person WHERE Name LIKE N'%(demo)' AND Email LIKE N'%@example.com'"));

        // Contracts in every stage, each with an Activity line; no emails queued; documents downloadable; the audit chain holds.
        var stages = (await db.QueryAsync<string>("SELECT DISTINCT k.Stage FROM contracts.Contract k JOIN contracts.Supplier s ON s.Id = k.SupplierId WHERE s.Name LIKE N'%(demo)'")).ToHashSet();
        Assert.Superset(new HashSet<string> { "Draft", "LegalReview", "Signing", "Approval", "Effective" }, stages);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox e JOIN contracts.Person p ON p.Id = e.PersonId WHERE p.Name LIKE N'%(demo)'"));
        var versionId = await db.ExecuteScalarAsync<int>("SELECT TOP 1 v.Id FROM contracts.DocumentVersion v JOIN contracts.Document d ON d.Id = v.DocumentId WHERE d.Kind = N'SignedCopy' ORDER BY v.Id DESC");
        var file = await admin.GetAsync($"/api/documents/{versionId}/file", Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await file.Content.ReadAsByteArrayAsync(Ct))[..4]);
        var home = await admin.GetFromJsonAsync<JsonElement>("/api/home", Ct);
        Assert.NotEmpty(home.GetProperty("urgent").EnumerateArray());
        Assert.Null(await AuditLog.VerifyAsync(db));
        Assert.Equal(existing.GetProperty("id").GetInt32(), peopleAfter.Single(p => p.Name == "Kept Person").Id);
    }
}
