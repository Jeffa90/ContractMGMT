using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>Slice 7: Variations, hand-over, Confidential on locked contracts, any-time fields and emergency unlock.</summary>
public sealed class VariationTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private static object Roles(bool owner = false, bool approver = false, bool legal = false) =>
        new { contractOwner = owner, legal, approver, groupHead = false, admin = false, auditor = false };

    private async Task<(HttpClient Client, int Id, string Name)> PersonAsync(string name, object roles)
    {
        var user = Unique("vr");
        var full = $"{name} {user[^4..]}";
        var person = await app.AddPersonAsync(user, full, roles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32(), full);
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

    private static async Task<(HttpStatusCode Status, JsonElement Body)> VPutAsync(HttpClient client, int id, int vid, object changes, bool? needsLegalReview = null)
    {
        var rv = (await VariationAsync(client, id, vid)).GetProperty("variation").GetProperty("rowVersion").GetString();
        var response = await client.PutAsJsonAsync($"/api/contracts/{id}/variations/{vid}", new { rowVersion = rv, changes, needsLegalReview }, Ct);
        return (response.StatusCode, await ReadAsync(response));
    }

    private static string[] Actions(JsonElement detail) => detail.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToArray();

    private async Task<int> EffectiveAsync(HttpClient owner, HttpClient approver, object fields)
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat") }, Ct));
        var supplier = await ReadAsync(await owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct));
        var body = JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["title"] ??= Unique("Contract ");
        body["supplierId"] = supplier.GetProperty("id").GetInt32();
        body["categoryId"] = category.GetProperty("id").GetInt32();
        body["needsLegalReview"] = false;
        var id = (await ReadAsync(await owner.PostAsJsonAsync("/api/contracts", body, Ct))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "ready-for-signing")).Status);
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("%PDF signed"u8.ToArray()), "file", "signed.pdf");
            form.Add(new StringContent("SignedCopy"), "kind");
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "send-for-approval", new { effectiveDate = D(-100) })).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(approver, id, "approval/approve")).Status);
        return id;
    }

    [Fact]
    public async Task A_Variation_goes_through_Legal_and_approval_and_then_changes_the_contract_in_one_step()
    {
        var (owner, _, _) = await PersonAsync("Vera Variation", Roles(owner: true));
        var (approver, _, approverName) = await PersonAsync("Abel Approver", Roles(approver: true));
        var (legal, _, _) = await PersonAsync("Lou Legal", Roles(legal: true));
        var id = await EffectiveAsync(owner, approver, new { endDate = D(200), noticePeriodDays = 60, value = 100000 });
        var registeredBefore = (await ContractAsync(owner, id)).GetProperty("contract").GetProperty("registeredUtc").GetString();

        // Start: a reason is required; Effective contracts only; owners only.
        Assert.Contains("startVariation", Actions(await ContractAsync(owner, id)));
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, id, "variations")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(approver, id, "variations", new { reason = "x" })).Status);
        var (started, startBody) = await ActAsync(owner, id, "variations", new { reason = "Supplier agreed a 12-month extension at a higher rate." });
        Assert.Equal(HttpStatusCode.OK, started);
        var vid = startBody.GetProperty("result").GetProperty("variationId").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, id, "variations", new { reason = "Second" })).Status); // one open at a time
        var v = await VariationAsync(owner, id, vid);
        Assert.True(v.GetProperty("variation").GetProperty("needsLegalReview").GetBoolean()); // ticked by default (D-75)
        var keys = v.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()!).ToArray();
        Assert.Contains("endDate", keys);
        Assert.DoesNotContain("title", keys);

        // Changes: only allowed fields; checked like the form; unchanged values are dropped.
        Assert.Equal(HttpStatusCode.BadRequest, (await VPutAsync(owner, id, vid, new Dictionary<string, object?> { ["title"] = "New title" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await VPutAsync(owner, id, vid, new Dictionary<string, object?> { ["evergreen"] = true })).Status); // evergreen with an end date
        Assert.Equal(HttpStatusCode.OK, (await VPutAsync(owner, id, vid, new Dictionary<string, object?> { ["endDate"] = D(565), ["value"] = 135000, ["noticePeriodDays"] = 60 })).Status);
        var changes = (await VariationAsync(owner, id, vid)).GetProperty("changes").EnumerateArray().Select(ch => ch.GetProperty("key").GetString()!).ToArray();
        Assert.Equal(["endDate", "value"], changes);

        // Legal is ticked, so approval waits for Legal; the back-and-forth works as for contracts.
        Assert.Equal(HttpStatusCode.Forbidden, (await VActAsync(owner, id, vid, "send-for-approval")).Status);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(owner, id, vid, "send-to-legal", new { note = "Please check the new rate clause." })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await VPutAsync(owner, id, vid, new Dictionary<string, object?> { ["value"] = 1 })).Status); // Legal's turn
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(legal, id, vid, "legal/take")).Status);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(legal, id, vid, "legal/send-to-owner", new { note = "Cap the increase at 30%." })).Status);
        Assert.Equal(HttpStatusCode.OK, (await VPutAsync(owner, id, vid, new Dictionary<string, object?> { ["endDate"] = D(565), ["value"] = 130000 })).Status);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(owner, id, vid, "send-to-legal")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await VActAsync(legal, id, vid, "legal/clear")).Status);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(legal, id, vid, "legal/clear", new { note = "Cleared. Increase within policy." })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await VPutAsync(owner, id, vid, new Dictionary<string, object?> { ["value"] = 1 })).Status); // cleared: frozen
        var cleared = await VariationAsync(owner, id, vid);
        Assert.Equal(["sendToLegal", "sendForApproval", "withdraw"], Actions(cleared));
        Assert.Equal(5, cleared.GetProperty("conversation").GetArrayLength()); // sent, taken, back to owner, sent again, cleared

        // Approval: nothing changes on the contract until it's approved; then every change applies at once, audited; the registered date stays.
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(owner, id, vid, "send-for-approval")).Status);
        Assert.Equal(D(200), (await ContractAsync(owner, id)).GetProperty("contract").GetProperty("endDate").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await VActAsync(owner, id, vid, "approve")).Status);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(approver, id, vid, "approve")).Status);
        var after = (await ContractAsync(owner, id)).GetProperty("contract");
        Assert.Equal((D(565), 130000m), (after.GetProperty("endDate").GetString(), after.GetProperty("value").GetDecimal()));
        Assert.Equal(registeredBefore, after.GetProperty("registeredUtc").GetString());
        Assert.Equal("Effective", after.GetProperty("stage").GetString());
        Assert.Equal("Approved", (await VariationAsync(owner, id, vid)).GetProperty("variation").GetProperty("stage").GetString());
        await using var db = await app.OpenAsync();
        var summaries = (await db.QueryAsync<string>("SELECT Summary FROM contracts.AuditEvent WHERE ContractId = @id ORDER BY Id", new { id })).ToList();
        Assert.Contains(summaries, s => s.Contains("(through Variation 1, approved)") && s.Contains("end date from"));
        Assert.Contains(summaries, s => s.StartsWith($"{approverName} approved Variation 1"));

        // A withdrawn Variation changes nothing; then another can start.
        var (_, second) = await ActAsync(owner, id, "variations", new { reason = "Change the site", needsLegalReview = false });
        var vid2 = second.GetProperty("result").GetProperty("variationId").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await VPutAsync(owner, id, vid2, new Dictionary<string, object?> { ["noticePeriodDays"] = 30 })).Status);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(owner, id, vid2, "withdraw", new { note = "Not needed after all." })).Status);
        Assert.Equal(60, (await ContractAsync(owner, id)).GetProperty("contract").GetProperty("noticePeriodDays").GetInt32());
        Assert.Contains("startVariation", Actions(await ContractAsync(owner, id)));
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task An_approver_can_send_a_Variation_back_and_nobody_approves_their_own()
    {
        var (owner, _, _) = await PersonAsync("Ozzie Owner", Roles(owner: true, approver: true));
        var (approver, _, _) = await PersonAsync("Ada Approver", Roles(approver: true));
        var id = await EffectiveAsync(owner, approver, new { endDate = D(300) });
        var (_, started) = await ActAsync(owner, id, "variations", new { reason = "Extend", needsLegalReview = false });
        var vid = started.GetProperty("result").GetProperty("variationId").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await VActAsync(owner, id, vid, "send-for-approval")).Status); // no changes yet
        Assert.Equal(HttpStatusCode.OK, (await VPutAsync(owner, id, vid, new Dictionary<string, object?> { ["endDate"] = D(400) })).Status);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(owner, id, vid, "send-for-approval")).Status);
        var own = await VariationAsync(owner, id, vid);
        Assert.Empty(Actions(own).Intersect(["approve"]));
        Assert.Contains("raised the Variation", own.GetProperty("cannotApproveReason").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await VActAsync(approver, id, vid, "send-back")).Status);
        Assert.Equal(HttpStatusCode.OK, (await VActAsync(approver, id, vid, "send-back", new { note = "Attach the signed letter." })).Status);
        var back = await VariationAsync(owner, id, vid);
        Assert.Equal("Draft", back.GetProperty("variation").GetProperty("stage").GetString());
        Assert.Equal("SentBack", back.GetProperty("approvals")[0].GetProperty("decision").GetString());
        Assert.Equal(D(300), (await ContractAsync(owner, id)).GetProperty("contract").GetProperty("endDate").GetString());
    }

    [Fact]
    public async Task Locked_contracts_can_be_handed_over_marked_Confidential_by_admins_and_unlocked_in_an_emergency()
    {
        var (owner, _, _) = await PersonAsync("Hana Handover", Roles(owner: true));
        var (other, otherId, otherName) = await PersonAsync("Theo Takeover", Roles(owner: true));
        var (approver, _, _) = await PersonAsync("Amy Approver", Roles(approver: true));
        var (viewer, _, _) = await PersonAsync("Vic Viewer", AppFixture.NoRoles);
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var id = await EffectiveAsync(owner, approver, new { endDate = D(300), title = "Locked contract" });

        // Hand-over: any contract owner, even though it's locked; the new owner is emailed.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(viewer, id, "hand-over", new { ownerId = otherId })).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "hand-over", new { ownerId = otherId })).Status);
        Assert.Equal(otherName, (await ContractAsync(owner, id)).GetProperty("contract").GetProperty("ownerName").GetString());
        await using var db = await app.OpenAsync();
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'handed-over' AND PersonId = @otherId", new { id, otherId }));

        // Confidential on a locked contract: admins only, with a reason.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(other, id, "confidential", new { isConfidential = true, note = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(admin, id, "confidential", new { isConfidential = true })).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(admin, id, "confidential", new { isConfidential = true, note = "Commercially sensitive pricing." })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/contracts/{id}", Ct)).StatusCode);

        // Any-time fields: only added fields an admin marked "any time"; built-in fields refuse.
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(other, id, "any-time-fields", new { fields = new Dictionary<string, object> { ["endDate"] = D(1) } })).Status);

        // Emergency unlock: admins only, a reason, 1 to 4 hours; meanwhile only admins edit, each edit carries the unlock; locking again ends it.
        var rv = (await ContractAsync(other, id)).GetProperty("contract").GetProperty("rowVersion").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Fixed", rowVersion = rv }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(other, id, "unlock", new { note = "x", hours = 1 })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(admin, id, "unlock", new { note = "Fix the title", hours = 5 })).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(admin, id, "unlock", new { note = "Title was mistyped at registration.", hours = 2 })).Status);
        var unlocked = await ContractAsync(admin, id);
        Assert.Equal("Title was mistyped at registration.", unlocked.GetProperty("contract").GetProperty("unlock").GetProperty("reason").GetString());
        Assert.True(unlocked.GetProperty("canEdit").GetBoolean());
        Assert.False((await ContractAsync(other, id)).GetProperty("canEdit").GetBoolean()); // admins only (D-79)
        var c = unlocked.GetProperty("contract");
        var edit = await admin.PutAsJsonAsync($"/api/contracts/{id}", new
        {
            title = "Locked contract (corrected)", supplierId = c.GetProperty("supplierId").GetInt32(), categoryId = c.GetProperty("categoryId").GetInt32(), ownerId = otherId,
            isConfidential = true, endDate = D(300), effectiveDate = c.GetProperty("effectiveDate").GetString(), needsLegalReview = false, rowVersion = c.GetProperty("rowVersion").GetString(),
        }, Ct);
        Assert.True(edit.StatusCode == HttpStatusCode.OK, await edit.Content.ReadAsStringAsync(Ct));
        var after = (await ContractAsync(admin, id)).GetProperty("contract");
        Assert.Equal(("Locked contract (corrected)", "Effective"), (after.GetProperty("title").GetString(), after.GetProperty("stage").GetString()));
        Assert.Contains("emergency unlock", await db.QuerySingleAsync<string>("SELECT TOP 1 Summary FROM contracts.AuditEvent WHERE ContractId = @id AND Action = 'contract.changed' ORDER BY Id DESC", new { id }));
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(admin, id, "unlock/close")).Status);
        Assert.False((await ContractAsync(admin, id)).GetProperty("canEdit").GetBoolean());
        Assert.Null(await AuditLog.VerifyAsync(db));
    }
}
