using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>Slice 3: sending on from Draft, and the Legal review back-and-forth (product-spec sections 4 and 5).</summary>
public sealed class LegalTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private static object Roles(bool owner = false, bool legal = false) =>
        new { contractOwner = owner, legal, approver = false, groupHead = false, admin = false, auditor = false };

    private async Task<(HttpClient Client, int Id, string Name)> PersonAsync(string name, object roles)
    {
        var user = Unique("lg");
        var fullName = $"{name} {user[^4..]}"; // unique names, so emails can be told apart
        var person = await app.AddPersonAsync(user, fullName, roles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32(), fullName);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, int id) => await ReadAsync(await client.GetAsync($"/api/contracts/{id}", Ct));

    private static async Task<string> RowVersionAsync(HttpClient client, int id) =>
        (await DetailAsync(client, id)).GetProperty("contract").GetProperty("rowVersion").GetString()!;

    /// <summary>Runs an action with the contract's current row version.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> ActAsync(HttpClient client, int id, string action, object? extra = null)
    {
        var body = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
        body["rowVersion"] = await RowVersionAsync(client, id);
        var response = await client.PostAsJsonAsync($"/api/contracts/{id}/{action}", body, Ct);
        return (response.StatusCode, await ReadAsync(response));
    }

    private async Task<(int SupplierId, int CategoryId)> SupplierAndCategoryAsync(HttpClient owner)
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat"), parentId = await app.FamilyAsync() }, Ct));
        var supplier = await ReadAsync(await owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct));
        return (supplier.GetProperty("id").GetInt32(), category.GetProperty("id").GetInt32());
    }

    private static string[] Actions(JsonElement detail) => detail.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToArray();

    [Fact]
    public async Task A_contract_goes_back_and_forth_between_Legal_and_the_owner_until_cleared()
    {
        var (owner, ownerId, ownerName) = await PersonAsync("Olga Owner", Roles(owner: true));
        var (backup, backupId, _) = await PersonAsync("Bea Backup", Roles(owner: true));
        var (otherOwner, _, otherName) = await PersonAsync("Otto Other", Roles(owner: true));
        var (sam, samId, samName) = await PersonAsync("Sam Legal", Roles(legal: true));
        var (lee, leeId, leeName) = await PersonAsync("Lee Legal", Roles(legal: true));
        var (viewer, _, _) = await PersonAsync("Vic Viewer", AppFixture.NoRoles);

        var created = await owner.PostAsJsonAsync("/api/contracts", new { title = "Cold store fit-out", backupOwnerId = backupId }, Ct);
        var id = (await ReadAsync(created)).GetProperty("id").GetInt32();

        // Required fields are checked when it leaves Draft, in plain words.
        var draft = await DetailAsync(owner, id);
        Assert.Contains("sendToLegal", Actions(draft));
        Assert.Equal(["Supplier", "Category"], draft.GetProperty("missingRequired").EnumerateArray().Select(m => m.GetString()!).ToArray());
        var (missingStatus, missing) = await ActAsync(owner, id, "send-to-legal");
        Assert.Equal(HttpStatusCode.BadRequest, missingStatus);
        Assert.Equal("Fill in Supplier and Category before sending it to Legal.", missing.GetProperty("message").GetString());

        var (supplierId, categoryId) = await SupplierAndCategoryAsync(owner);
        var rv = await RowVersionAsync(owner, id);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Cold store fit-out", supplierId, categoryId, backupOwnerId = backupId, rowVersion = rv }, Ct)).StatusCode);

        // Legal and people with no roles can't send it; a stale page is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(sam, id, "send-to-legal")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(viewer, id, "send-to-legal")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/contracts/{id}/send-to-legal", new { rowVersion = rv }, Ct)).StatusCode);

        // 1. Owner sends it to Legal: nobody has it yet, every Legal person is emailed.
        var (sent, sentBody) = await ActAsync(owner, id, "send-to-legal", new { note = "Supplier wants a 30-day payment term." });
        Assert.True(sent == HttpStatusCode.OK, sentBody.ToString());
        var withLegal = await DetailAsync(sam, id);
        Assert.Equal("LegalReview", withLegal.GetProperty("contract").GetProperty("stage").GetString());
        Assert.Equal("Legal", withLegal.GetProperty("legal").GetProperty("whoseTurn").GetString());
        Assert.Equal("Not started", withLegal.GetProperty("legal").GetProperty("statusName").GetString());
        Assert.Contains("take", Actions(withLegal));
        Assert.False((await DetailAsync(owner, id)).GetProperty("canEdit").GetBoolean()); // details frozen on Legal's turn
        var rvFrozen = await RowVersionAsync(owner, id);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Changed", rowVersion = rvFrozen }, Ct)).StatusCode);
        await using var db = await app.OpenAsync();
        var toLegal = (await db.QueryAsync<string>("SELECT ToName FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'legal-sent'", new { id })).ToList();
        Assert.Contains(samName, toLegal);
        Assert.Contains(leeName, toLegal);

        // Owners can't act for Legal.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, id, "legal/send-to-owner", new { note = "x" })).Status);

        // 2. Sam takes it and says it's waiting on the supplier.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(sam, id, "legal/take")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(sam, id, "legal/status", new { status = "WaitingOnSupplier", note = "Insurance certificate requested" })).Status);
        var legal = (await DetailAsync(owner, id)).GetProperty("legal");
        Assert.Equal(samName, legal.GetProperty("legalPersonName").GetString());
        Assert.Equal("Waiting on supplier", legal.GetProperty("statusName").GetString());
        Assert.Equal("Insurance certificate requested", legal.GetProperty("note").GetString());

        // 3. Sam sends it to the owner with a mark-up; a note is required. The owner and backup are emailed, not Sam.
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(sam, id, "legal/send-to-owner")).Status);
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("mark-up"u8.ToArray()), "file", "markup.txt");
            form.Add(new StringContent("LegalMarkup"), "kind");
            var upload = await ReadAsync(await sam.PostAsync($"/api/contracts/{id}/documents", form, Ct));
            var versionId = upload.GetProperty("versionId").GetInt32();
            var (back, backBody) = await ActAsync(sam, id, "legal/send-to-owner", new { note = "Supplier won't accept clause 12, please negotiate.", documentVersionIds = new[] { versionId } });
            Assert.True(back == HttpStatusCode.OK, backBody.ToString());
        }
        var ownersTurn = await DetailAsync(otherOwner, id);
        Assert.Equal("Owner", ownersTurn.GetProperty("legal").GetProperty("whoseTurn").GetString());
        Assert.Equal("Waiting on owner", ownersTurn.GetProperty("legal").GetProperty("statusName").GetString());
        Assert.True(ownersTurn.GetProperty("canEdit").GetBoolean()); // any contract owner, on the owner's turn (D-55)
        Assert.Contains("sendToLegal", Actions(ownersTurn));
        var backEmails = (await db.QueryAsync<string>("SELECT ToName FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'legal-back'", new { id })).OrderBy(n => n).ToList();
        Assert.Equal(new[] { ownerName, (await db.QuerySingleAsync<string>("SELECT Name FROM contracts.Person WHERE Id = @backupId", new { backupId })) }.OrderBy(n => n), backEmails);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(sam, id, "legal/clear", new { note = "ok" })).Status); // not Legal's turn

        // 4. Another contract owner edits it and sends it back to Legal: it goes to Sam (who has it), and only Sam is emailed.
        var rvOwner = await RowVersionAsync(otherOwner, id);
        Assert.Equal(HttpStatusCode.OK, (await otherOwner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Cold store fit-out (revised)", supplierId, categoryId, ownerId, backupOwnerId = backupId, rowVersion = rvOwner }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(otherOwner, id, "send-to-legal", new { note = "Supplier accepted a 60-day cap." })).Status);
        var again = (await DetailAsync(sam, id)).GetProperty("legal");
        Assert.Equal(("Legal", samName, "Reviewing"), (again.GetProperty("whoseTurn").GetString(), again.GetProperty("legalPersonName").GetString(), again.GetProperty("statusName").GetString()));
        var lastSent = (await db.QueryAsync<string>("SELECT e.ToName FROM contracts.EmailOutbox e WHERE e.ContractId = @id AND e.Kind = 'legal-sent' AND e.Id > (SELECT MAX(Id) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'legal-back')", new { id })).ToList();
        Assert.Equal([samName], lastSent);

        // 5. Sam gives it to Lee (Lee is emailed); clearing needs an outcome note; Lee clears it and it moves to Signing.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(sam, id, "legal/assign", new { personId = leeId })).Status);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'legal-assigned' AND PersonId = @leeId", new { id, leeId }));
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(lee, id, "legal/clear")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(lee, id, "legal/clear", new { note = "Cleared. Liability cap agreed at $2m." })).Status);
        var cleared = await DetailAsync(owner, id);
        Assert.Equal("Signing", cleared.GetProperty("contract").GetProperty("stage").GetString());
        Assert.Equal("Cleared. Liability cap agreed at $2m.", cleared.GetProperty("legal").GetProperty("outcome").GetString());
        Assert.Equal(leeName, cleared.GetProperty("legal").GetProperty("clearedByName").GetString());
        Assert.False(cleared.GetProperty("canEdit").GetBoolean());
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'legal-cleared'", new { id }));


        // From Signing, the owner can send it back to Legal with a reason when something must change; it goes to Lee, who cleared it.
        Assert.Equal(["sendForApproval", "sendBackToLegal"], Actions(cleared));
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, id, "send-back-to-legal")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "send-back-to-legal", new { note = "Supplier wants to change the start date." })).Status);
        var reopened = await DetailAsync(lee, id);
        Assert.Equal(("LegalReview", "Legal", leeName), (reopened.GetProperty("contract").GetProperty("stage").GetString(), reopened.GetProperty("legal").GetProperty("whoseTurn").GetString(), reopened.GetProperty("legal").GetProperty("legalPersonName").GetString()));
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("legal").GetProperty("outcome").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(lee, id, "legal/clear", new { note = "Cleared again. Start date change is fine." })).Status);

        // The conversation is numbered, keeps the mark-up as it was, and is visible to anyone who can see the contract (D-58).
        var conversation = (await DetailAsync(viewer, id)).GetProperty("conversation").EnumerateArray().ToList();
        Assert.Equal(["SentToLegal", "Taken", "StatusChanged", "SentToOwner", "SentToLegal", "Assigned", "Cleared", "SentToLegal", "Cleared"], conversation.Select(e => e.GetProperty("action").GetString()!).ToArray());
        Assert.Equal(Enumerable.Range(1, 9), conversation.Select(e => e.GetProperty("number").GetInt32()));
        Assert.Equal("markup.txt", conversation[3].GetProperty("documents")[0].GetProperty("fileName").GetString());
        Assert.Equal(otherName, conversation[4].GetProperty("byName").GetString());

        // The Legal record (D-122): Legal and admins download a ZIP of a Word record and the conversation's files, numbered to match.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/contracts/{id}/legal-record.zip", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/contracts/{id}/legal-record.zip", Ct)).StatusCode);
        var record = await sam.GetAsync($"/api/contracts/{id}/legal-record.zip", Ct);
        Assert.Equal(HttpStatusCode.OK, record.StatusCode);
        using (var zip = new System.IO.Compression.ZipArchive(await record.Content.ReadAsStreamAsync(Ct)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("Files/4 - markup (v1).txt", names);
            var docx = zip.Entries.Single(e => e.FullName.StartsWith("Legal record - ") && e.FullName.EndsWith(".docx"));
            using var word = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(docx.Open(), false);
            var text = word.MainDocumentPart!.Document.Body!.InnerText;
            Assert.Contains("Supplier won't accept clause 12, please negotiate.", text);
            Assert.Contains("in this ZIP as Files/4 - markup (v1).txt", text);
            Assert.Contains("Cleared by Legal", text);
        }
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent WHERE ContractId = @id AND Action = N'legal.record-downloaded'", new { id }));

        // Entries can't be changed afterwards, even directly in the database.
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.ExecuteAsync("UPDATE contracts.LegalTurn SET Note = 'changed' WHERE ContractId = @id", new { id }));
        var activity = await db.QueryAsync<string>("SELECT Summary FROM contracts.AuditEvent WHERE ContractId = @id", new { id });
        Assert.Contains(activity, s => s.StartsWith($"{leeName} cleared"));
        Assert.DoesNotContain(activity, s => s.Contains(".\"."));  // no doubled full stops after a quoted note
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task Without_Legal_review_a_draft_goes_to_signing_and_can_be_withdrawn_later()
    {
        var (owner, _, _) = await PersonAsync("Nora NoLegal", Roles(owner: true));
        var (supplierId, categoryId) = await SupplierAndCategoryAsync(owner);
        var created = await owner.PostAsJsonAsync("/api/contracts", new { title = "Pallet hire", supplierId, categoryId, needsLegalReview = false }, Ct);
        var id = (await ReadAsync(created)).GetProperty("id").GetInt32();
        Assert.Equal(["readyForSigning"], Actions(await DetailAsync(owner, id)));
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, id, "send-to-legal")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "ready-for-signing")).Status);
        var signing = await DetailAsync(owner, id);
        Assert.Equal("Signing", signing.GetProperty("contract").GetProperty("stage").GetString());
        Assert.True(signing.GetProperty("canUpload").GetBoolean());
        Assert.True(signing.GetProperty("canWithdraw").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "withdraw", new { reason = "Supplier pulled out" })).Status);
        Assert.Equal("Withdrawn", (await DetailAsync(owner, id)).GetProperty("contract").GetProperty("stage").GetString());
    }

    [Fact]
    public async Task A_Confidential_contract_in_Legal_review_stays_hidden_from_others()
    {
        var (owner, _, _) = await PersonAsync("Cora Confidential", Roles(owner: true));
        var (other, _, _) = await PersonAsync("Ollie Outsider", Roles(owner: true));
        var (legal, _, _) = await PersonAsync("Lana Legal", Roles(legal: true));
        var (supplierId, categoryId) = await SupplierAndCategoryAsync(owner);
        var created = await owner.PostAsJsonAsync("/api/contracts", new { title = "Executive services", supplierId, categoryId, isConfidential = true }, Ct);
        var id = (await ReadAsync(created)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "send-to-legal")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/contracts/{id}", Ct)).StatusCode);
        var hidden = await other.PostAsJsonAsync($"/api/contracts/{id}/legal/take", new { rowVersion = "x" }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(legal, id, "legal/take")).Status); // Legal sees Confidential contracts
        var list = await ReadAsync(await legal.GetAsync("/api/contracts?view=legal", Ct));
        Assert.Contains(list.EnumerateArray(), c => c.GetProperty("id").GetInt32() == id && c.GetProperty("whoseTurn").GetString() == "Legal");
    }
}
