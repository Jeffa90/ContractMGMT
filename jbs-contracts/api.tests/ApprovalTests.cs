using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>Slice 4: signing, the approver's confirmation, registering and locking (product-spec sections 4 and 6; D-59 to D-62).</summary>
public sealed class ApprovalTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private static object Roles(bool owner = false, bool approver = false, bool legal = false) =>
        new { contractOwner = owner, legal, approver, groupHead = false, admin = false, auditor = false };

    private async Task<(HttpClient Client, int Id)> PersonAsync(string name, object roles)
    {
        var user = Unique("ap");
        var person = await app.AddPersonAsync(user, $"{name} {user[^4..]}", roles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, int id) => await ReadAsync(await client.GetAsync($"/api/contracts/{id}", Ct));

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ActAsync(HttpClient client, int id, string action, object? extra = null)
    {
        var body = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
        body["rowVersion"] = (await DetailAsync(client, id)).GetProperty("contract").GetProperty("rowVersion").GetString();
        var response = await client.PostAsJsonAsync($"/api/contracts/{id}/{action}", body, Ct);
        return (response.StatusCode, await ReadAsync(response));
    }

    private static string[] Actions(JsonElement detail) => detail.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToArray();
    private static string Stage(JsonElement detail) => detail.GetProperty("contract").GetProperty("stage").GetString()!;

    /// <summary>A contract in Signing that skipped Legal (Needs Legal review unticked).</summary>
    private async Task<int> InSigningAsync(HttpClient owner, string endDate = "2028-06-30")
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat"), parentId = await app.FamilyAsync() }, Ct));
        var supplier = await ReadAsync(await owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct));
        var created = await owner.PostAsJsonAsync("/api/contracts", new
        {
            title = "Pallet hire", supplierId = supplier.GetProperty("id").GetInt32(), categoryId = category.GetProperty("id").GetInt32(),
            needsLegalReview = false, endDate,
        }, Ct);
        var id = (await ReadAsync(created)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "ready-for-signing")).Status);
        return id;
    }

    private static async Task UploadSignedCopyAsync(HttpClient client, int id)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF-1.4 signed"u8.ToArray()), "file", "signed.pdf");
        form.Add(new StringContent("SignedCopy"), "kind");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_signed_contract_is_confirmed_by_an_approver_and_becomes_Effective_and_locked()
    {
        var (owner, ownerId) = await PersonAsync("Oscar Owner", Roles(owner: true));
        var (approver, _) = await PersonAsync("Ava Approver", Roles(approver: true));
        var (ownerApprover, _) = await PersonAsync("Owen OwnerApprover", Roles(owner: true, approver: true));
        var id = await InSigningAsync(owner);
        var signing = await DetailAsync(owner, id);
        Assert.Equal(["sendForApproval", "backToDraft", "sapNumber"], Actions(signing));

        // The signed copy and the effective date are both needed; the date is never filled in (D-61).
        var (status, body) = await ActAsync(owner, id, "send-for-approval");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Add the signed copy and the effective date first.", body.GetProperty("message").GetString());
        await UploadSignedCopyAsync(owner, id);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, id, "send-for-approval")).Status);
        var (late, lateBody) = await ActAsync(owner, id, "send-for-approval", new { effectiveDate = "2029-01-01" });
        Assert.Equal(HttpStatusCode.BadRequest, late);
        Assert.Contains("after the end date", lateBody.GetProperty("fields").GetProperty("effectiveDate").GetString());
        var (sent, sentBody) = await ActAsync(owner, id, "send-for-approval", new { effectiveDate = "2026-12-01" });
        Assert.True(sent == HttpStatusCode.OK, sentBody.ToString());

        var waiting = await DetailAsync(approver, id);
        Assert.Equal("Approval", Stage(waiting));
        Assert.Equal("2026-12-01", waiting.GetProperty("contract").GetProperty("effectiveDate").GetString());
        Assert.Equal(["approve", "sendBack"], Actions(waiting));
        Assert.False(waiting.GetProperty("canWithdraw").GetBoolean());
        await using var db = await app.OpenAsync();
        var emailed = (await db.QueryAsync<int>("SELECT PersonId FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'approval-waiting'", new { id })).ToList();
        Assert.NotEmpty(emailed);
        Assert.DoesNotContain(ownerId, emailed);

        // Nobody approves what they own; non-approvers can't; sending back needs a reason.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, id, "approval/approve")).Status);
        var raised = await InSigningAsync(ownerApprover);
        await UploadSignedCopyAsync(ownerApprover, raised);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(ownerApprover, raised, "send-for-approval", new { effectiveDate = "2026-12-01" })).Status);
        var own = await DetailAsync(ownerApprover, raised);
        Assert.Equal(["sapNumber"], Actions(own)); // only the SAP number (D-124)
        Assert.Contains("raised it", own.GetProperty("cannotApproveReason").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(ownerApprover, raised, "approval/approve")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(approver, id, "approval/send-back")).Status);

        // Sent back: Signing again, the reason at the top, the owner emailed.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(approver, id, "approval/send-back", new { note = "The signed copy is missing page 4." })).Status);
        var back = await DetailAsync(owner, id);
        Assert.Equal("Signing", Stage(back));
        Assert.Equal("The signed copy is missing page 4.", back.GetProperty("sentBack").GetProperty("reason").GetString());
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'approval-sent-back' AND PersonId = @ownerId", new { id, ownerId }));

        // Sent again and approved: Effective, registered, locked.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "send-for-approval", new { effectiveDate = "2026-12-01" })).Status);
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(owner, id)).GetProperty("sentBack").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(approver, id, "approval/approve")).Status);
        var effective = await DetailAsync(owner, id);
        var c = effective.GetProperty("contract");
        Assert.Equal("Effective", Stage(effective));
        Assert.True(c.GetProperty("isLocked").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, c.GetProperty("registeredUtc").ValueKind);
        Assert.True(c.GetProperty("registeredAsBackup").GetBoolean()); // a backup approver, not the Group Head
        Assert.Equal(["Approved", "SentBack"], effective.GetProperty("approvals").EnumerateArray().Select(a => a.GetProperty("decision").GetString()!).ToArray());
        Assert.False(effective.GetProperty("canEdit").GetBoolean());
        Assert.False(effective.GetProperty("canUpload").GetBoolean());
        Assert.False(effective.GetProperty("canWithdraw").GetBoolean());
        // Only its renewal actions (slice 6) and locked-contract changes (slice 7). "anyTimeFields" depends on the shared layout, which LayoutTests changes.
        Assert.Equal(["recordDecision", "endEarly", "reviewDone", "handOver", "sapNumber", "startVariation"], Actions(effective).Where(a => a != "anyTimeFields"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'approved' AND PersonId = @ownerId", new { id, ownerId }));

        // Locked means locked: edits, documents and withdrawal are refused on the server.
        var rowVersion = c.GetProperty("rowVersion").GetString();
        var edit = await owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = "Changed", rowVersion }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        Assert.Contains("locked", (await ReadAsync(edit)).GetProperty("message").GetString());
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("x"u8.ToArray()), "file", "extra.txt");
            form.Add(new StringContent("Supporting"), "kind");
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, id, "withdraw", new { reason = "x" })).Status);
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.ExecuteAsync("DELETE FROM contracts.Approval WHERE ContractId = @id", new { id }));
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task From_Signing_a_contract_that_skipped_Legal_can_go_back_to_draft_with_a_reason()
    {
        var (owner, _) = await PersonAsync("Dora Draft", Roles(owner: true));
        var id = await InSigningAsync(owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, id, "send-back-to-legal", new { note = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, id, "back-to-draft")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "back-to-draft", new { note = "Supplier changed the rates." })).Status);
        var draft = await DetailAsync(owner, id);
        Assert.Equal("Draft", Stage(draft));
        Assert.True(draft.GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task An_approval_made_by_mistake_can_be_undone_by_its_approver_until_anything_else_happens()
    {
        var (owner, _) = await PersonAsync("Uma Owner", Roles(owner: true));
        var (approver, _) = await PersonAsync("Udo Approver", Roles(approver: true));
        var (other, _) = await PersonAsync("Una Approver", Roles(approver: true));
        await using var db = await app.OpenAsync();

        async Task<int> EffectiveAsync(int id)
        {
            await UploadSignedCopyAsync(owner, id);
            Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "send-for-approval", new { effectiveDate = "2026-12-01" })).Status);
            Assert.Equal(HttpStatusCode.OK, (await ActAsync(approver, id, "approval/approve")).Status);
            return id;
        }
        var id = await EffectiveAsync(await InSigningAsync(owner));

        // Only the approver who approved sees and can use Undo.
        Assert.NotEqual(JsonValueKind.Null, (await DetailAsync(approver, id)).GetProperty("undoApprovalUntilUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(other, id)).GetProperty("undoApprovalUntilUtc").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(other, id, "approval/undo")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, id, "approval/undo")).Status);

        var (status, body) = await ActAsync(approver, id, "approval/undo");
        Assert.True(status == HttpStatusCode.OK, body.ToString());
        var back = await DetailAsync(approver, id);
        Assert.Equal("Approval", Stage(back));
        Assert.False(back.GetProperty("contract").GetProperty("isLocked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, back.GetProperty("contract").GetProperty("registeredUtc").ValueKind);
        Assert.Equal(["Undone", "Approved"], back.GetProperty("approvals").EnumerateArray().Take(2).Select(a => a.GetProperty("decision").GetString()));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = N'approval-undone'", new { id }));
        // Undoing twice isn't possible; it can be approved again.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(approver, id, "approval/undo")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(other, id, "approval/approve")).Status);
        Assert.Equal("Effective", Stage(await DetailAsync(other, id)));

        // Once anything else happens to it (here a renewal decision), it can't be undone.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "renewal-decision", new { decision = "Renegotiate" })).Status);
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(other, id)).GetProperty("undoApprovalUntilUtc").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(other, id, "approval/undo")).Status);

        // A renewal: undoing its approval makes the contract it renewed Effective again.
        var old = await EffectiveAsync(await InSigningAsync(owner));
        var (renewStatus, renewBody) = await ActAsync(owner, old, "renewal-decision", new { decision = "Renew" });
        Assert.True(renewStatus == HttpStatusCode.OK, renewBody.ToString());
        var renewal = renewBody.GetProperty("result").GetProperty("renewalId").GetInt32();
        await db.ExecuteAsync("UPDATE contracts.Contract SET NeedsLegalReview = 0, EndDate = '2030-06-30' WHERE Id = @renewal", new { renewal });
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, renewal, "ready-for-signing")).Status);
        await EffectiveAsync(renewal);
        Assert.Equal("Ended", Stage(await DetailAsync(owner, old)));
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(approver, renewal, "approval/undo")).Status);
        var oldAgain = await DetailAsync(owner, old);
        Assert.Equal("Effective", Stage(oldAgain));
        Assert.Equal(JsonValueKind.Null, oldAgain.GetProperty("contract").GetProperty("endedOn").ValueKind);
        Assert.Equal("Approval", Stage(await DetailAsync(owner, renewal)));
        Assert.Null(await AuditLog.VerifyAsync(db));
    }
    [Fact]
    public async Task The_SAP_number_can_be_added_when_sending_for_approval_and_changed_after_without_a_Variation()
    {
        // D-124: the SAP outline agreement number often only exists once it's signed. Optional at Send for approval; from Signing on it
        // changes by itself (contract owners, Procurement, admins), audited; drafts change it on the form.
        var (owner, _) = await PersonAsync("Sid Sap", Roles(owner: true));
        var (approver, _) = await PersonAsync("Ada SapApprover", Roles(approver: true));
        var (legal, _) = await PersonAsync("Lee SapLegal", Roles(legal: true));
        var user = Unique("sp");
        await app.AddPersonAsync(user, $"Pia Procure {user[^4..]}", new { contractOwner = false, legal = false, approver = false, groupHead = false, admin = false, auditor = false, procurement = true });
        using var procurement = app.ClientFor($@"TEST\{user}");

        var draft = (await ReadAsync(await owner.PostAsJsonAsync("/api/contracts", new { title = "SAP draft" }, Ct))).GetProperty("id").GetInt32();
        Assert.DoesNotContain("sapNumber", Actions(await DetailAsync(owner, draft)));
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, draft, "sap-number", new { sapOutlineAgreement = "4600000001" })).Status);

        var id = await InSigningAsync(owner);
        var signing = await DetailAsync(owner, id);
        Assert.Contains("sapNumber", Actions(signing));
        var label = signing.GetProperty("sapLabel").GetString()!; // LayoutTests renames it, so read it
        Assert.StartsWith("SAP outline agreement", label);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(legal, id, "sap-number", new { sapOutlineAgreement = "4600000001" })).Status);

        // Sent for approval with the number: saved, and Activity says so.
        await UploadSignedCopyAsync(owner, id);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "send-for-approval", new { effectiveDate = "2026-12-01", sapOutlineAgreement = " 4600012345 " })).Status);
        var sent = await DetailAsync(owner, id);
        Assert.Equal("4600012345", sent.GetProperty("contract").GetProperty("sapOutlineAgreement").GetString());
        Assert.Contains($"added the {label} 4600012345", sent.GetProperty("activity")[0].GetProperty("summary").GetString());

        // Effective and locked, yet the number still changes by itself.
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(approver, id, "approval/approve")).Status);
        var effective = await DetailAsync(owner, id);
        Assert.Equal("Effective", Stage(effective));
        Assert.Contains("sapNumber", Actions(effective));
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(owner, id, "sap-number", new { sapOutlineAgreement = "4600099999" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, id, "sap-number", new { sapOutlineAgreement = "4600099999" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, id, "sap-number", new { sapOutlineAgreement = new string('9', 51) })).Status);
        Assert.Equal(HttpStatusCode.OK, (await ActAsync(procurement, id, "sap-number", new { sapOutlineAgreement = "" })).Status);
        var after = await DetailAsync(owner, id);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("contract").GetProperty("sapOutlineAgreement").ValueKind);
        Assert.True(after.GetProperty("contract").GetProperty("isLocked").GetBoolean());

        await using var db = await app.OpenAsync();
        var said = (await db.QueryAsync<string>("SELECT Summary FROM contracts.AuditEvent WHERE ContractId = @id AND Action = N'contract.changed' ORDER BY Id", new { id })).ToList();
        Assert.Contains(said, s => s.Contains($"changed the {label} from 4600012345 to 4600099999"));
        Assert.Contains(said, s => s.Contains($"cleared the {label} (was 4600099999)"));
        Assert.Null(await AuditLog.VerifyAsync(db));
    }
}
