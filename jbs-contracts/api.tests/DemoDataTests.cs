using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>
/// Demo data (D-66, D-97): admins only; shared out across everyone by role; never sends email; refreshing archives the old set and
/// loads a new one; it never removes or changes the people already there.
/// </summary>
// Run on its own: the demo data shares items with every active person, which would upset other tests counting their own people's tiles.
[CollectionDefinition(nameof(DemoDataTests), DisableParallelization = true)]
public sealed class DemoDataAlone;

[Collection(nameof(DemoDataTests))]
public sealed class DemoDataTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int Tile(JsonElement home, string key) =>
        home.GetProperty("tiles").EnumerateArray().Where(t => t.GetProperty("key").GetString() == key).Select(t => t.GetProperty("count").GetInt32()).SingleOrDefault();

    [Fact]
    public async Task Demo_data_gives_everyone_something_by_role_sends_no_email_and_refreshes_without_removing_anyone()
    {
        // Sorted first by name, so this person is within the people the demo data shares items with.
        var user = $"keep{Guid.NewGuid():N}"[..14];
        var keptName = $"Aaron Aardvark {user[^4..]}";
        var existing = await app.AddPersonAsync(user, keptName, new { contractOwner = true, legal = true, approver = true, groupHead = false, admin = false, auditor = false });
        await using var db = await app.OpenAsync();
        var peopleBefore = (await db.QueryAsync<(int Id, string Name, bool IsContractOwner, bool IsLegal, bool IsApprover, bool IsGroupHead, bool IsAdmin, bool IsActive)>(
            "SELECT Id, Name, IsContractOwner, IsLegal, IsApprover, IsGroupHead, IsAdmin, IsActive FROM contracts.Person")).ToList();

        // Only admins.
        using var kept = app.ClientFor($@"TEST\{user}");
        Assert.Equal(HttpStatusCode.Forbidden, (await kept.PostAsync("/api/admin/demo-data", null, Ct)).StatusCode);

        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var first = await admin.PostAsync("/api/admin/demo-data", null, Ct);
        Assert.True(first.StatusCode == HttpStatusCode.OK, await first.Content.ReadAsStringAsync(Ct));
        Assert.Equal("loaded", (await admin.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("demoData").GetString());

        // Something for this person in each of their roles: their drafts, a Legal review on their desk, one to approve.
        var home = await kept.GetFromJsonAsync<JsonElement>("/api/home", Ct);
        Assert.True(Tile(home, "drafts") > 0, "drafts");
        Assert.True(Tile(home, "legal-your-turn") > 0, "legal-your-turn");
        Assert.True(Tile(home, "to-approve") > 0, "to-approve");

        // Every stage, Variations (approved, with Legal, waiting for approval), imported contracts with a cover page, supplier reviews.
        var stages = (await db.QueryAsync<string>("SELECT DISTINCT Stage FROM contracts.Contract WHERE IsDemo = 1")).ToHashSet();
        Assert.Superset(new HashSet<string> { "Draft", "LegalReview", "Signing", "Approval", "Effective", "Ended", "Archived", "Withdrawn" }, stages);
        var variationStages = (await db.QueryAsync<string>("SELECT DISTINCT v.Stage FROM contracts.Variation v JOIN contracts.Contract k ON k.Id = v.ContractId WHERE k.IsDemo = 1")).ToHashSet();
        Assert.Superset(new HashSet<string> { "Draft", "LegalReview", "Approval", "Approved" }, variationStages);
        Assert.True(await db.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM contracts.Contract k JOIN contracts.Document d ON d.ContractId = k.Id
            WHERE k.IsDemo = 1 AND k.ImportedUtc IS NOT NULL AND k.LegacyReference LIKE N'MED-%' AND d.Kind = N'InternalSignOff'
            """) > 0);
        Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.BusinessReviewAction a JOIN contracts.BusinessReview r ON r.Id = a.ReviewId JOIN contracts.Supplier s ON s.Id = r.SupplierId WHERE s.IsDemo = 1 AND a.DoneUtc IS NULL AND r.IsRemoved = 0") > 0);

        // No email about demo items: not from the load, and not when someone acts on one.
        var draft = await db.ExecuteScalarAsync<int>("SELECT TOP 1 Id FROM contracts.Contract WHERE IsDemo = 1 AND Stage = N'Draft' AND OwnerId = @id AND SupplierId IS NOT NULL ORDER BY Id DESC",
            new { id = existing.GetProperty("id").GetInt32() });
        var detail = await kept.GetFromJsonAsync<JsonElement>($"/api/contracts/{draft}", Ct);
        var sent = await kept.PostAsJsonAsync($"/api/contracts/{draft}/send-to-legal", new { rowVersion = detail.GetProperty("contract").GetProperty("rowVersion").GetString() }, Ct);
        Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync(Ct));
        // (The morning job queues through the same place, which skips demo contracts and suppliers.)
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId IN (SELECT Id FROM contracts.Contract WHERE IsDemo = 1)"));
        Assert.Equal("LegalReview", (await kept.GetFromJsonAsync<JsonElement>($"/api/contracts/{draft}", Ct)).GetProperty("contract").GetProperty("stage").GetString());

        // Refresh: the old set is archived (nothing deleted) and a fresh one loaded.
        var activeBefore = (await db.QueryAsync<int>("SELECT Id FROM contracts.Contract WHERE IsDemo = 1 AND Stage <> N'Archived'")).ToList();
        var second = await admin.PostAsync("/api/admin/demo-data", null, Ct);
        Assert.True(second.StatusCode == HttpStatusCode.OK, await second.Content.ReadAsStringAsync(Ct));
        Assert.True((await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("archived").GetInt32() >= activeBefore.Count);
        Assert.Equal(activeBefore.Count, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM contracts.Contract WHERE Id IN (SELECT value FROM OPENJSON(@ids)) AND Stage = N'Archived'", new { ids = JsonSerializer.Serialize(activeBefore) }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Variation v JOIN contracts.Contract k ON k.Id = v.ContractId WHERE k.Stage = N'Archived' AND v.Stage IN (N'Draft', N'LegalReview', N'Approval')"));
        Assert.True(Tile(await kept.GetFromJsonAsync<JsonElement>("/api/home", Ct), "drafts") > 0);

        // People who were there are still there, unchanged, and nobody was removed.
        var peopleAfter = (await db.QueryAsync<(int Id, string Name, bool IsContractOwner, bool IsLegal, bool IsApprover, bool IsGroupHead, bool IsAdmin, bool IsActive)>(
            "SELECT Id, Name, IsContractOwner, IsLegal, IsApprover, IsGroupHead, IsAdmin, IsActive FROM contracts.Person")).ToList();
        Assert.Contains(peopleBefore.Single(p => p.Name == keptName), peopleAfter);
        Assert.Contains(peopleBefore.Single(p => p.Name == AppFixture.AdminName), peopleAfter);
        Assert.All(peopleBefore, p => Assert.Contains(peopleAfter, a => a.Id == p.Id));
        Assert.Equal(4, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.Person WHERE Name LIKE N'%(demo)' AND Email LIKE N'%@example.com'"));

        // Documents download; the audit chain holds.
        var versionId = await db.ExecuteScalarAsync<int>("SELECT TOP 1 v.Id FROM contracts.DocumentVersion v JOIN contracts.Document d ON d.Id = v.DocumentId JOIN contracts.Contract k ON k.Id = d.ContractId WHERE k.IsDemo = 1 AND d.Kind = N'SignedCopy' ORDER BY v.Id DESC");
        var file = await admin.GetAsync($"/api/documents/{versionId}/file", Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(await file.Content.ReadAsByteArrayAsync(Ct))[..4]);
        Assert.NotEmpty((await admin.GetFromJsonAsync<JsonElement>("/api/home", Ct)).GetProperty("urgent").EnumerateArray());
        Assert.Null(await AuditLog.VerifyAsync(db));
    }
}
