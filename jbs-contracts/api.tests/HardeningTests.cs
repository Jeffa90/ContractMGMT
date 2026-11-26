using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Features;

namespace JbsContracts.Api.Tests;

/// <summary>Fixes from the security and code review before go-live (D-93).</summary>
public sealed class HardeningTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private static object Roles(bool owner = false, bool approver = false, bool legal = false) =>
        new { contractOwner = owner, legal, approver, groupHead = false, admin = false, auditor = false };

    private async Task<(HttpClient Client, int Id, string User, JsonElement Person)> PersonAsync(string name, object roles)
    {
        var user = Unique("hd");
        var person = await app.AddPersonAsync(user, $"{name} {user[^4..]}", roles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32(), user, person);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> ContractAsync(HttpClient client, int id) => await ReadAsync(await client.GetAsync($"/api/contracts/{id}", Ct));
    private static async Task<JsonElement> VariationAsync(HttpClient client, int id, int vid) => await ReadAsync(await client.GetAsync($"/api/contracts/{id}/variations/{vid}", Ct));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string url, string rowVersion, object? extra = null)
    {
        var body = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
        body["rowVersion"] = rowVersion;
        var response = await client.PostAsJsonAsync(url, body, Ct);
        return (response.StatusCode, await ReadAsync(response));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ActAsync(HttpClient client, int id, string action, object? extra = null) =>
        await PostAsync(client, $"/api/contracts/{id}/{action}", (await ContractAsync(client, id)).GetProperty("contract").GetProperty("rowVersion").GetString()!, extra);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> VActAsync(HttpClient client, int id, int vid, string action, object? extra = null) =>
        await PostAsync(client, $"/api/contracts/{id}/variations/{vid}/{action}", (await VariationAsync(client, id, vid)).GetProperty("variation").GetProperty("rowVersion").GetString()!, extra);

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, int id, string kind, int? documentId = null, int? variationId = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF test"u8.ToArray()), "file", "file.pdf");
        form.Add(new StringContent(kind), "kind");
        if (documentId is { } d) form.Add(new StringContent(d.ToString()), "documentId");
        if (variationId is { } v) form.Add(new StringContent(v.ToString()), "variationId");
        return await client.PostAsync($"/api/contracts/{id}/documents", form, Ct);
    }

    private async Task<int> DraftAsync(HttpClient owner, object fields)
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat"), parentId = await app.FamilyAsync() }, Ct));
        var supplier = await ReadAsync(await owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct));
        var body = JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["title"] ??= Unique("Contract ");
        body["supplierId"] = supplier.GetProperty("id").GetInt32();
        body["categoryId"] = category.GetProperty("id").GetInt32();
        return (await ReadAsync(await owner.PostAsJsonAsync("/api/contracts", body, Ct))).GetProperty("id").GetInt32();
    }

    private async Task<int> EffectiveAsync(HttpClient owner, HttpClient approver, int endInDays = 300)
    {
        var id = await DraftAsync(owner, new { endDate = D(endInDays), needsLegalReview = false, noticePeriodDays = 30 });
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "ready-for-signing")).Status);
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, id, "SignedCopy")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "send-for-approval", new { effectiveDate = D(-100) })).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(approver, id, "approval/approve")).Status);
        return id;
    }

    private async Task<int> StartVariationAsync(HttpClient owner, int id, bool needsLegalReview)
    {
        var (status, started) = await ActAsync(owner, id, "variations", new { reason = "Extend the term", needsLegalReview });
        Assert.Equal(HttpStatusCode.OK, status);
        return started.GetProperty("result").GetProperty("variationId").GetInt32();
    }

    private async Task<int> SignedCopyDocumentAsync(int contractId)
    {
        await using var c = await app.OpenAsync();
        return await c.ExecuteScalarAsync<int>("SELECT Id FROM contracts.Document WHERE ContractId = @contractId AND Kind = N'SignedCopy' AND VariationId IS NULL", new { contractId });
    }

    [Fact]
    public async Task A_Variation_upload_cannot_replace_the_locked_contracts_own_documents()
    {
        var (owner, _, _, _) = await PersonAsync("Uma Uploader", Roles(owner: true));
        var (approver, _, _, _) = await PersonAsync("Abe Approver", Roles(approver: true));
        var id = await EffectiveAsync(owner, approver);
        var vid = await StartVariationAsync(owner, id, needsLegalReview: false);
        var signed = await SignedCopyDocumentAsync(id);

        // A new version of the registered signed copy, through the Variation: refused; and without it: locked.
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(owner, id, "SignedCopy", documentId: signed, variationId: vid)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(owner, id, "SignedCopy", documentId: signed)).StatusCode);
        await using (var c = await app.OpenAsync())
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT CurrentVersion FROM contracts.Document WHERE Id = @signed", new { signed }));

        // The Variation's own documents still take new versions.
        var added = await ReadAsync(await UploadAsync(owner, id, "Variation", variationId: vid));
        var own = added.GetProperty("documentId").GetInt32();
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(owner, id, "Variation", documentId: own, variationId: vid)).StatusCode);
    }

    [Fact]
    public async Task A_Variation_hand_over_only_links_that_Variations_documents()
    {
        var (owner, _, _, _) = await PersonAsync("Vic Variation", Roles(owner: true));
        var (approver, _, _, _) = await PersonAsync("Ava Approver", Roles(approver: true));
        var id = await EffectiveAsync(owner, approver);
        var otherContract = await EffectiveAsync(owner, approver);
        var vid = await StartVariationAsync(owner, id, needsLegalReview: true);
        using (var put = await owner.PutAsJsonAsync($"/api/contracts/{id}/variations/{vid}", new
        {
            rowVersion = (await VariationAsync(owner, id, vid)).GetProperty("variation").GetProperty("rowVersion").GetString(),
            changes = new Dictionary<string, object?> { ["endDate"] = D(400) },
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        int foreign;
        await using (var c = await app.OpenAsync())
            foreign = await c.ExecuteScalarAsync<int>("SELECT TOP (1) v.Id FROM contracts.DocumentVersion v JOIN contracts.Document d ON d.Id = v.DocumentId WHERE d.ContractId = @otherContract", new { otherContract });
        var (refused, problem) = await VActAsync(owner, id, vid, "send-to-legal", new { documentVersionIds = new[] { foreign } });
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Contains("isn't on this contract", problem.GetProperty("message").GetString());

        var ownVersion = (await ReadAsync(await UploadAsync(owner, id, "Variation", variationId: vid))).GetProperty("versionId").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(owner, id, vid, "send-to-legal", new { documentVersionIds = new[] { ownVersion } })).Status);
    }

    [Fact]
    public async Task A_contract_isnt_handed_over_while_its_Variation_waits_for_approval()
    {
        var (owner, _, _, _) = await PersonAsync("Hugo Holder", Roles(owner: true, approver: true));
        var (other, otherId, _, _) = await PersonAsync("Ivy Incoming", Roles(owner: true));
        var (approver, _, _, _) = await PersonAsync("Ari Approver", Roles(approver: true));
        var id = await EffectiveAsync(owner, approver);
        var vid = await StartVariationAsync(owner, id, needsLegalReview: false);
        using (var put = await owner.PutAsJsonAsync($"/api/contracts/{id}/variations/{vid}", new
        {
            rowVersion = (await VariationAsync(owner, id, vid)).GetProperty("variation").GetProperty("rowVersion").GetString(),
            changes = new Dictionary<string, object?> { ["endDate"] = D(400) },
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(owner, id, vid, "send-for-approval")).Status);

        var detail = await ContractAsync(owner, id);
        Assert.DoesNotContain("handOver", detail.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        var (status, body) = await ActAsync(owner, id, "hand-over", new { ownerId = otherId });
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("waiting for approval", body.GetProperty("message").GetString());
        _ = other;
    }

    [Fact]
    public async Task Downloads_cant_be_started_from_another_website()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        using var cross = new HttpRequestMessage(HttpMethod.Get, "/api/reports/everything.xlsx");
        cross.Headers.Add("Sec-Fetch-Site", "cross-site");
        var refused = await admin.SendAsync(cross, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("blocked", (await ReadAsync(refused)).GetProperty("code").GetString());

        using var own = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        own.Headers.Add("Sec-Fetch-Site", "same-origin");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(own, Ct)).StatusCode);
        using var typed = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        typed.Headers.Add("Sec-Fetch-Site", "none");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(typed, Ct)).StatusCode);
    }

    [Fact]
    public async Task Withdrawing_a_renewal_means_the_old_contract_needs_a_decision_again()
    {
        var (owner, _, _, _) = await PersonAsync("Rita Renewer", Roles(owner: true));
        var (approver, _, _, _) = await PersonAsync("Al Approver", Roles(approver: true));
        var old = await EffectiveAsync(owner, approver, endInDays: 40);
        var (status, renew) = await ActAsync(owner, old, "renewal-decision", new { decision = "Renew" });
        Assert.Equal(HttpStatusCode.OK, status);
        var renewal = renew.GetProperty("result").GetProperty("renewalId").GetInt32();
        Assert.Equal("Renew", (await ContractAsync(owner, old)).GetProperty("renewal").GetProperty("decision").GetString());

        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, renewal, "withdraw", new { reason = "Supplier pulled out" })).Status);
        var after = await ContractAsync(owner, old);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("renewal").GetProperty("decision").ValueKind);
        Assert.Contains(after.GetProperty("activity").EnumerateArray(), a => a.GetProperty("action").GetString() == "contract.renewal-decision-cleared");
    }

    [Fact]
    public async Task Someone_leaving_Legal_gives_back_the_reviews_they_had()
    {
        var (owner, _, _, _) = await PersonAsync("Olly Owner", Roles(owner: true));
        var (legal, legalId, legalUser, legalPerson) = await PersonAsync("Lou Legal", Roles(legal: true));
        var id = await DraftAsync(owner, new { endDate = D(300), needsLegalReview = true });
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "send-to-legal")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(legal, id, "legal/take")).Status);
        await using (var before = await app.OpenAsync())
            Assert.Equal(legalId, await before.ExecuteScalarAsync<int?>("SELECT LegalPersonId FROM contracts.Contract WHERE Id = @id", new { id }));

        using var admin = app.ClientFor(AppFixture.AdminAccount);
        legalPerson = (await ReadAsync(await admin.GetAsync("/api/people", Ct))).EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == legalId); // signing in changed it
        using (var put = await admin.PutAsJsonAsync($"/api/people/{legalId}", new
        {
            account = legalUser, name = legalPerson.GetProperty("name").GetString(), email = legalPerson.GetProperty("email").GetString(),
            roles = Roles(), isActive = true, rowVersion = legalPerson.GetProperty("rowVersion").GetString(),
        }, Ct))
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        await using var c = await app.OpenAsync();
        var after = await c.QuerySingleAsync<(int? LegalPersonId, string LegalStatus)>("SELECT LegalPersonId, LegalStatus FROM contracts.Contract WHERE Id = @id", new { id });
        Assert.Null(after.LegalPersonId);
        Assert.Equal("NotStarted", after.LegalStatus);
        Assert.Contains((await ContractAsync(owner, id)).GetProperty("activity").EnumerateArray(), a => a.GetProperty("action").GetString() == "contract.legal-released");
    }

    [Theory]
    [InlineData("Active", false)]
    [InlineData("Current - renewed", false)]
    [InlineData("In force", false)]
    [InlineData("Expired", true)]
    [InlineData("Terminated early", true)]
    [InlineData("Inactive", true)]
    [InlineData("Not renewed", null)]
    [InlineData("Not active", null)]
    [InlineData("Invalid", null)]
    [InlineData("Pending", null)]
    [InlineData("Expired, renewed", null)]
    public void Import_status_words_are_read_whole_and_negatives_are_left_for_a_person(string status, bool? ended) =>
        Assert.Equal(ended, LegacyImportEndpoints.StatusSays(status));
}
