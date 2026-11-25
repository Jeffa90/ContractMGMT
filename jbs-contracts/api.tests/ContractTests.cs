using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Features;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

public sealed class ContractTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..18];

    private static readonly object OwnerRoles = new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false };

    private async Task<(HttpClient Client, int Id)> NewOwnerAsync(string name)
    {
        var user = Unique("own");
        var person = await app.AddPersonAsync(user, name, OwnerRoles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32());
    }

    private async Task<HttpClient> NewPersonAsync(string name, object roles)
    {
        var user = Unique("p");
        await app.AddPersonAsync(user, name, roles);
        return app.ClientFor($@"TEST\{user}");
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<int> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/contracts", body, Ct);
        var json = await ReadAsync(response);
        Assert.True(response.StatusCode == HttpStatusCode.Created, json.ToString());
        return json.GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, int id) => await ReadAsync(await client.GetAsync($"/api/contracts/{id}", Ct));

    [Fact]
    public async Task A_contract_owner_starts_a_draft_with_a_JBSAUS_reference()
    {
        var (owner, ownerId) = await NewOwnerAsync("Olive Owner");
        var id = await CreateAsync(owner, new { title = "Freight services 2026" });
        var detail = await GetAsync(owner, id);
        var contract = detail.GetProperty("contract");
        Assert.Matches(@"^JBSAUS-\d{4,}$", contract.GetProperty("reference").GetString());
        Assert.Equal("Draft", contract.GetProperty("stage").GetString());
        Assert.Equal(ownerId, contract.GetProperty("ownerId").GetInt32());
        Assert.True(contract.GetProperty("needsLegalReview").GetBoolean()); // on by default (D-08)
        Assert.True(detail.GetProperty("canEdit").GetBoolean());
        Assert.Contains("started the draft", detail.GetProperty("activity")[0].GetProperty("summary").GetString());

        var mine = await ReadAsync(await owner.GetAsync("/api/contracts?view=mine", Ct));
        Assert.Contains(mine.EnumerateArray(), c => c.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task People_without_the_contract_owner_tick_cannot_add_or_change_contracts()
    {
        var looker = await NewPersonAsync("Lou Looker", AppFixture.NoRoles);
        var response = await looker.PostAsJsonAsync("/api/contracts", new { title = "Nope" }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var (owner, _) = await NewOwnerAsync("Ollie Owner");
        var id = await CreateAsync(owner, new { title = "Cleaning" });
        var detail = await GetAsync(looker, id); // can look
        Assert.False(detail.GetProperty("canEdit").GetBoolean());
        var edit = await looker.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Changed", rowVersion = detail.GetProperty("contract").GetProperty("rowVersion").GetString() }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
    }

    [Fact]
    public async Task Confidential_contracts_are_hidden_from_people_who_should_not_see_them()
    {
        var (owner, _) = await NewOwnerAsync("Cora Confidential");
        var (otherOwner, _) = await NewOwnerAsync("Otto Other");
        var looker = await NewPersonAsync("Lena Looker", AppFixture.NoRoles);
        var legal = await NewPersonAsync("Lee Legal", new { contractOwner = false, legal = true, approver = false, groupHead = false, admin = false, auditor = false });
        var title = Unique("Secret deal ");
        var id = await CreateAsync(owner, new { title, isConfidential = true });
        var upload = await UploadAsync(owner, id, "secret.pdf", "%PDF-1.4 secret", "Draft");
        var versionId = (await ReadAsync(upload)).GetProperty("versionId").GetInt32();

        foreach (var outsider in new[] { otherOwner, looker })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/contracts/{id}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/documents/{versionId}/file", Ct)).StatusCode);
            var all = await ReadAsync(await outsider.GetAsync($"/api/contracts?view=all&q={Uri.EscapeDataString(title)}", Ct));
            Assert.Equal(0, all.GetArrayLength());
        }
        Assert.Equal(HttpStatusCode.OK, (await legal.GetAsync($"/api/contracts/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await legal.GetAsync($"/api/documents/{versionId}/file", Ct)).StatusCode);
    }

    [Fact]
    public async Task Editing_a_draft_is_audited_in_plain_words_and_stale_edits_are_refused()
    {
        var (owner, _) = await NewOwnerAsync("Edie Editor");
        var id = await CreateAsync(owner, new { title = "Packaging", noticePeriodDays = 90 });
        var rowVersion = (await GetAsync(owner, id)).GetProperty("contract").GetProperty("rowVersion").GetString();
        var ok = await owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Packaging supply", endDate = "2027-12-31", noticePeriodDays = 90, rowVersion }, Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var detail = await GetAsync(owner, id);
        Assert.Equal("2027-10-02", detail.GetProperty("contract").GetProperty("noticeDeadline").GetString()); // end date minus 90 days
        var summary = detail.GetProperty("activity")[0].GetProperty("summary").GetString();
        Assert.Contains("title from Packaging to Packaging supply", summary);
        Assert.Contains("set end date to 31 Dec 2027", summary);

        var stale = await owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Overwrite", rowVersion }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Draft_details_are_checked_with_plain_messages()
    {
        var (owner, ownerId) = await NewOwnerAsync("Val Validator");
        var response = await owner.PostAsJsonAsync("/api/contracts", new
        {
            title = "", isEvergreen = true, endDate = "2027-01-01", value = -5, noticePeriodDays = 5000, backupOwnerId = ownerId, supplierId = 999999,
        }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await ReadAsync(response)).GetProperty("fields");
        foreach (var f in new[] { "title", "endDate", "value", "noticePeriodDays", "backupOwnerId", "supplierId" })
            Assert.True(fields.TryGetProperty(f, out _), $"expected a message for {f}");
    }

    [Fact]
    public async Task Suppliers_need_a_unique_name_and_a_real_ABN()
    {
        var (owner, _) = await NewOwnerAsync("Sue Supplier");
        var name = Unique("Linfox ");
        var bad = await owner.PostAsJsonAsync("/api/suppliers", new { name, abn = "12 345 678 901" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var good = await owner.PostAsJsonAsync("/api/suppliers", new { name, abn = "51 824 753 556", vendorNumber = "900001" }, Ct);
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.Equal("51824753556", (await ReadAsync(good)).GetProperty("abn").GetString());
        var dup = await owner.PostAsJsonAsync("/api/suppliers", new { name = name.ToUpperInvariant() }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);

        var supplierId = (await ReadAsync(good)).GetProperty("id").GetInt32();
        var id = await CreateAsync(owner, new { title = "Linehaul", supplierId });
        var found = await ReadAsync(await owner.GetAsync($"/api/contracts?view=all&q={Uri.EscapeDataString(name)}", Ct));
        Assert.Contains(found.EnumerateArray(), c => c.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task Only_admins_change_the_lists_and_hidden_items_stay_on_contracts_that_use_them()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var catName = Unique("Freight");
        var created = await admin.PostAsJsonAsync("/api/categories", new { name = catName }, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var catId = (await ReadAsync(created)).GetProperty("id").GetInt32();
        var sub = await admin.PostAsJsonAsync("/api/categories", new { name = "Linehaul", parentId = catId }, Ct);
        Assert.Equal(HttpStatusCode.Created, sub.StatusCode);
        var subId = (await ReadAsync(sub)).GetProperty("id").GetInt32();
        var tooDeep = await admin.PostAsJsonAsync("/api/categories", new { name = "Deeper", parentId = subId }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);

        var (owner, _) = await NewOwnerAsync("Lisa Lists");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/categories", new { name = "Mine" }, Ct)).StatusCode);
        var id = await CreateAsync(owner, new { title = "Linehaul Qld", categoryId = subId });
        Assert.Equal($"{catName} > Linehaul", (await GetAsync(owner, id)).GetProperty("contract").GetProperty("categoryDisplay").GetString());

        // Hide the subcategory: the contract keeps it and can still be saved; new contracts can't pick it.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/categories/{subId}", new { isActive = false }, Ct)).StatusCode);
        var rowVersion = (await GetAsync(owner, id)).GetProperty("contract").GetProperty("rowVersion").GetString();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Linehaul Qld 2", categoryId = subId, rowVersion }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/contracts", new { title = "New", categoryId = subId }, Ct)).StatusCode);
    }

    private async Task<HttpResponseMessage> UploadAsync(HttpClient client, int contractId, string fileName, string content, string kind, int? documentId = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(kind), "kind");
        if (documentId is not null) form.Add(new StringContent(documentId.ToString()!), "documentId");
        return await client.PostAsync($"/api/contracts/{contractId}/documents", form, Ct);
    }

    [Fact]
    public async Task Documents_upload_with_versions_download_exactly_and_unsafe_files_are_refused()
    {
        var (owner, _) = await NewOwnerAsync("Doc Holder");
        var id = await CreateAsync(owner, new { title = "Security services" });

        var first = await UploadAsync(owner, id, "Draft agreement.pdf", "%PDF-1.4 version one", "Draft");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var documentId = (await ReadAsync(first)).GetProperty("documentId").GetInt32();
        var second = await UploadAsync(owner, id, "Draft agreement v2.pdf", "%PDF-1.4 version two", "Draft", documentId);
        Assert.Equal(2, (await ReadAsync(second)).GetProperty("version").GetInt32());
        var versionId = (await ReadAsync(second)).GetProperty("versionId").GetInt32();

        var download = await owner.GetAsync($"/api/documents/{versionId}/file", Ct);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("%PDF-1.4 version two", await download.Content.ReadAsStringAsync(Ct));
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, id, "run-me.exe", "MZ", "Supporting")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(owner, id, "empty.pdf", "", "Supporting")).StatusCode);

        var looker = await NewPersonAsync("Dora Looker", AppFixture.NoRoles);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(looker, id, "mine.pdf", "%PDF", "Supporting")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await looker.GetAsync($"/api/documents/{versionId}/file", Ct)).StatusCode);

        var detail = await GetAsync(owner, id);
        var doc = Assert.Single(detail.GetProperty("documents").EnumerateArray());
        Assert.Equal(2, doc.GetProperty("versions").GetArrayLength());

        await using var db = await app.OpenAsync();
        var stored = await db.QueryAsync<string>("SELECT StoredName FROM contracts.DocumentVersion v JOIN contracts.Document d ON d.Id = v.DocumentId WHERE d.ContractId = @id", new { id });
        Assert.All(stored, s => Assert.DoesNotContain("Draft agreement", s)); // never the uploaded name

        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/contracts/{id}/documents/{documentId}/remove", null, Ct)).StatusCode);
        Assert.Equal(0, (await GetAsync(owner, id)).GetProperty("documents").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/documents/{versionId}/file", Ct)).StatusCode); // kept, not deleted
    }

    [Fact]
    public async Task Withdrawing_needs_a_reason_and_locks_the_draft()
    {
        var (owner, _) = await NewOwnerAsync("Walt Withdraw");
        var id = await CreateAsync(owner, new { title = "Abandoned" });
        var rowVersion = (await GetAsync(owner, id)).GetProperty("contract").GetProperty("rowVersion").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/contracts/{id}/withdraw", new { reason = " ", rowVersion }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/contracts/{id}/withdraw", new { reason = "Supplier pulled out", rowVersion }, Ct)).StatusCode);
        var detail = await GetAsync(owner, id);
        Assert.Equal("Withdrawn", detail.GetProperty("contract").GetProperty("stage").GetString());
        Assert.False(detail.GetProperty("canEdit").GetBoolean());
        await using var db = await app.OpenAsync();
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task Suppliers_and_list_items_added_at_the_same_moment_all_succeed()
    {
        var (owner, _) = await NewOwnerAsync("Paula Parallel");
        var suppliers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => owner.PostAsJsonAsync("/api/suppliers", new { name = Unique($"Parallel {i} ") }, Ct)));
        Assert.All(suppliers, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var items = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => admin.PostAsJsonAsync("/api/lists/items", new { listName = "Site", value = Unique($"Site {i} ") }, Ct)));
        Assert.All(items, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
    }

    [Theory]
    [InlineData("51824753556", true)]
    [InlineData("53004085616", true)]
    [InlineData("12345678901", false)]
    [InlineData("5182475355", false)]
    public void Abn_check_digits(string abn, bool valid) => Assert.Equal(valid, Abn.IsValid(abn));
}
