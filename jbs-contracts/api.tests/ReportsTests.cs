using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;

namespace JbsContracts.Api.Tests;

/// <summary>Slice 8: the reports (register, pipeline, approvals, audit trail) and the Team page. Assertions only look at this test's own rows.</summary>
public sealed class ReportsTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, int Id, string Name)> PersonAsync(string name, object roles)
    {
        var user = Unique("rp");
        var full = $"{name} {user[^4..]}";
        var person = await app.AddPersonAsync(user, full, roles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32(), full);
    }

    private static object Roles(bool owner = false, bool legal = false, bool approver = false, bool auditor = false) =>
        new { contractOwner = owner, legal, approver, groupHead = false, admin = false, auditor };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string url) => await ReadAsync(await client.GetAsync(url, Ct));

    private static async Task OkAsync(HttpClient client, int id, string action, object? extra = null)
    {
        var body = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
        body["rowVersion"] = (await GetAsync(client, $"/api/contracts/{id}")).GetProperty("contract").GetProperty("rowVersion").GetString();
        await ReadAsync(await client.PostAsJsonAsync($"/api/contracts/{id}/{action}", body, Ct));
    }

    private async Task<(int Id, string Reference)> DraftAsync(HttpClient owner, object fields)
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat") }, Ct));
        var supplier = await ReadAsync(await owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct));
        var body = JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["title"] ??= Unique("Contract ");
        body["supplierId"] = supplier.GetProperty("id").GetInt32();
        body["categoryId"] = category.GetProperty("id").GetInt32();
        body["needsLegalReview"] ??= false;
        var created = await ReadAsync(await owner.PostAsJsonAsync("/api/contracts", body, Ct));
        return (created.GetProperty("id").GetInt32(), created.GetProperty("reference").GetString()!);
    }

    private async Task<(int Id, string Reference)> EffectiveAsync(HttpClient owner, HttpClient approver, object fields)
    {
        var (id, reference) = await DraftAsync(owner, fields);
        await OkAsync(owner, id, "ready-for-signing");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("%PDF signed"u8.ToArray()), "file", "signed.pdf");
            form.Add(new StringContent("SignedCopy"), "kind");
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
        }
        await OkAsync(owner, id, "send-for-approval", new { effectiveDate = D(-100) });
        await OkAsync(approver, id, "approval/approve");
        return (id, reference);
    }

    private static JsonElement Row(JsonElement rows, string property, int id) => rows.EnumerateArray().Single(r => r.GetProperty(property).GetInt32() == id);

    [Fact]
    public async Task Reports_are_for_admins_and_auditors_and_show_the_register_approvals_and_audit()
    {
        var (owner, _, ownerName) = await PersonAsync("Rory Report", Roles(owner: true));
        var (approver, _, approverName) = await PersonAsync("Anna Approve", Roles(approver: true));
        var (auditor, _, _) = await PersonAsync("Audrey Audit", Roles(auditor: true));
        using var admin = app.ClientFor(AppFixture.AdminAccount);

        var (effective, reference) = await EffectiveAsync(owner, approver, new { endDate = D(400), noticePeriodDays = 60, value = 250000 });
        var (confidential, _) = await EffectiveAsync(owner, approver, new { endDate = D(400), isConfidential = true });
        var (draft, _) = await DraftAsync(owner, new { });

        // Only admins and auditors.
        foreach (var url in new[] { "register", "register.xlsx", "pipeline", "approvals", "approvals.xlsx", "audit", "audit.xlsx" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/reports/{url}", Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await approver.GetAsync($"/api/reports/{url}", Ct)).StatusCode);
        }

        // Register: Effective contracts, Confidential included and marked; Drafts are not in it.
        var register = await GetAsync(auditor, "/api/reports/register");
        var row = Row(register.GetProperty("rows"), "id", effective);
        Assert.Equal(reference, row.GetProperty("reference").GetString());
        Assert.Equal(ownerName, row.GetProperty("ownerName").GetString());
        Assert.Equal(D(340), row.GetProperty("noticeDeadline").GetString());
        Assert.Equal(approverName, row.GetProperty("registeredByName").GetString());
        Assert.True(Row(register.GetProperty("rows"), "id", confidential).GetProperty("isConfidential").GetBoolean());
        Assert.DoesNotContain(register.GetProperty("rows").EnumerateArray(), r => r.GetProperty("id").GetInt32() == draft);

        // The Excel register has the same contract.
        var bytes = await (await admin.GetAsync("/api/reports/register.xlsx?scope=all", Ct)).Content.ReadAsByteArrayAsync(Ct);
        using (var book = new XLWorkbook(new MemoryStream(bytes)))
        {
            var sheet = book.Worksheet(1);
            Assert.Equal("Reference", sheet.Cell(1, 1).GetString());
            Assert.Contains(sheet.RowsUsed(), r => r.Cell(1).GetString() == reference && r.Cell(9).GetString() == ownerName);
        }

        // Pipeline counts the Draft.
        var pipeline = await GetAsync(admin, "/api/reports/pipeline");
        Assert.True(pipeline.GetProperty("stages").EnumerateArray().Single(s => s.GetProperty("stage").GetString() == "Draft").GetProperty("count").GetInt32() >= 1);

        // Approvals: who approved, and how long it waited.
        var approvals = await GetAsync(auditor, "/api/reports/approvals?days=30");
        var decision = approvals.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("contractId").GetInt32() == effective);
        Assert.Equal("Approved", decision.GetProperty("decision").GetString());
        Assert.Equal(approverName, decision.GetProperty("byName").GetString());
        Assert.Equal(JsonValueKind.Number, decision.GetProperty("turnaroundDays").ValueKind);
        Assert.EndsWith("Z", decision.GetProperty("atUtc").GetString());
        Assert.Contains(approvals.GetProperty("summary").GetProperty("byApprover").EnumerateArray(), a => a.GetProperty("name").GetString() == approverName && a.GetProperty("approved").GetInt32() == 2);

        // Audit: intact, searchable, and exporting it is itself recorded.
        var audit = await GetAsync(auditor, $"/api/reports/audit?days=7&q={reference}");
        Assert.True(audit.GetProperty("intact").GetBoolean());
        Assert.NotEmpty(audit.GetProperty("rows").EnumerateArray());
        Assert.All(audit.GetProperty("rows").EnumerateArray(), e => Assert.True(
            e.GetProperty("reference").GetString() == reference || e.GetProperty("summary").GetString()!.Contains(reference, StringComparison.Ordinal)));
        var export = await auditor.GetAsync($"/api/reports/audit.xlsx?days=7&q={reference}", Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var after = await GetAsync(admin, $"/api/reports/audit?days=7&q=audit.exported");
        Assert.Contains(after.GetProperty("rows").EnumerateArray(), e => e.GetProperty("summary").GetString()!.Contains($"matching \"{reference}\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Team_page_shows_what_each_person_has_coming_up_and_outstanding()
    {
        var (owner, ownerId, _) = await PersonAsync("Tess Team", Roles(owner: true));
        var (approver, _, _) = await PersonAsync("Archie Approve", Roles(approver: true));
        var (legal, legalId, _) = await PersonAsync("Lou Legal", Roles(legal: true));
        var (auditor, _, _) = await PersonAsync("Ada Audit", Roles(auditor: true));

        var (overdue, _) = await EffectiveAsync(owner, approver, new { endDate = D(20), noticePeriodDays = 30 });   // notice deadline 10 days ago
        var (soon, _) = await EffectiveAsync(owner, approver, new { endDate = D(60) });                              // ends in 60 days
        var (later, _) = await EffectiveAsync(owner, approver, new { endDate = D(400), noticePeriodDays = 30 });     // not coming up
        var (draft, _) = await DraftAsync(owner, new { });
        var (withLegal, _) = await DraftAsync(owner, new { needsLegalReview = true });
        await OkAsync(owner, withLegal, "send-to-legal", new { note = "Please review." });
        await OkAsync(legal, withLegal, "legal/take");

        // Not for owners, Legal or approvers.
        foreach (var client in new[] { owner, legal, approver })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/team", Ct)).StatusCode);

        var team = await GetAsync(auditor, "/api/team?days=90");
        var tess = Row(team.GetProperty("people"), "id", ownerId);
        Assert.Equal(1, tess.GetProperty("overdue").GetInt32());
        Assert.Equal(1, tess.GetProperty("comingUp").GetInt32());
        Assert.Equal(1, tess.GetProperty("theirTurn").GetInt32());
        Assert.Equal(1, tess.GetProperty("waitingOnOthers").GetInt32());
        Assert.Equal(3, tess.GetProperty("owns").GetInt32());
        var items = tess.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("Overdue", items[0].GetProperty("what").GetString());
        Assert.Equal(overdue, items[0].GetProperty("contractId").GetInt32());
        Assert.Equal("Notice", items[0].GetProperty("kind").GetString());
        Assert.Equal(-10, items[0].GetProperty("days").GetInt32());
        Assert.Contains(items, i => i.GetProperty("contractId").GetInt32() == soon && i.GetProperty("what").GetString() == "ComingUp");
        Assert.Contains(items, i => i.GetProperty("contractId").GetInt32() == draft && i.GetProperty("what").GetString() == "Draft");
        Assert.Contains(items, i => i.GetProperty("contractId").GetInt32() == withLegal && i.GetProperty("what").GetString() == "WithLegal");
        Assert.DoesNotContain(items, i => i.GetProperty("contractId").GetInt32() == later);

        var lou = Row(team.GetProperty("people"), "id", legalId);
        Assert.Equal(1, lou.GetProperty("legalTurn").GetInt32());
        Assert.Equal(withLegal, lou.GetProperty("items")[0].GetProperty("contractId").GetInt32());

        // A shorter window drops what's further out; a decision takes it off the list.
        var shorter = Row((await GetAsync(auditor, "/api/team?days=30")).GetProperty("people"), "id", ownerId);
        Assert.Equal(0, shorter.GetProperty("comingUp").GetInt32());
        Assert.Equal(1, shorter.GetProperty("overdue").GetInt32());
        await OkAsync(owner, overdue, "renewal-decision", new { decision = "Renew" });
        var decided = Row((await GetAsync(auditor, "/api/team?days=90")).GetProperty("people"), "id", ownerId);
        Assert.Equal(0, decided.GetProperty("overdue").GetInt32());
        Assert.Equal(2, decided.GetProperty("theirTurn").GetInt32()); // the renewal Draft is now theirs to finish
    }
}
