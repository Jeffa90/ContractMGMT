using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Dapper;
using JbsContracts.Api.Features;
using JbsContracts.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace JbsContracts.Api.Tests;

/// <summary>
/// D-102, after comparing Medius's contract fields with Jeff: Entity and Function drop-downs, Renews automatically (the daily job moves
/// the end date on), and contract numbers that carry on from Medius (imported contracts keep theirs).
/// Runs on its own (the non-parallel collection): the daily job here moves end dates on, and another class running the job at the
/// same time could move this class's contracts on between steps.
/// </summary>
[Collection(nameof(DemoDataTests))]
public sealed class StandardFieldsTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

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

    private async Task<(HttpClient Client, int Id, string Account)> PersonAsync(string name, bool owner = false, bool approver = false)
    {
        var user = Unique("sf");
        var person = await app.AddPersonAsync(user, $"{name} {user[^4..]}", new { contractOwner = owner, legal = false, approver, groupHead = false, admin = false, auditor = false });
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32(), $@"TEST\{user}");
    }

    private static async Task<int> ItemAsync(HttpClient admin, string list, string value) =>
        (await OkAsync(admin.PostAsJsonAsync("/api/lists/items", new { listName = list, value }, Ct))).GetProperty("id").GetInt32();

    private async Task<int> EffectiveAsync(HttpClient owner, HttpClient approver, HttpClient admin, object fields)
    {
        var category = (await OkAsync(admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat"), parentId = await app.FamilyAsync() }, Ct))).GetProperty("id").GetInt32();
        var supplier = (await OkAsync(owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct))).GetProperty("id").GetInt32();
        var body = JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["title"] = Unique("Contract ");
        body["supplierId"] = supplier;
        body["categoryId"] = category;
        body["needsLegalReview"] = false;
        var id = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", body, Ct))).GetProperty("id").GetInt32();
        async Task Act(HttpClient who, string action, object? extra = null)
        {
            var b = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
            b["rowVersion"] = (await OkAsync(who.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract").GetProperty("rowVersion").GetString();
            await OkAsync(who.PostAsJsonAsync($"/api/contracts/{id}/{action}", b, Ct));
        }
        await Act(owner, "ready-for-signing");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("%PDF signed"u8.ToArray()), "file", "signed.pdf");
            form.Add(new StringContent("SignedCopy"), "kind");
            await OkAsync(owner.PostAsync($"/api/contracts/{id}/documents", form, Ct));
        }
        await Act(owner, "send-for-approval", new { effectiveDate = D(-100) });
        await Act(approver, "approval/approve");
        if (fields.GetType().GetProperty("decision")?.GetValue(fields) is string decision) await Act(owner, "renewal-decision", new { decision });
        return id;
    }

    [Fact]
    public async Task Entity_and_function_are_drop_downs_admins_keep_and_renews_automatically_is_checked()
    {
        var u = Unique("e");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var (owner, _, _) = await PersonAsync("Ella Entity", owner: true);
        var entity = await ItemAsync(admin, "Entity", $"Fictional Meats Pty Ltd {u}");
        var function = await ItemAsync(admin, "Function", $"Procurement {u}");
        var site = await ItemAsync(admin, "Site", $"Site {u}");

        // Saved and shown by name; a new contract gets the next JBSAUS number (seven digits).
        var created = await OkAsync(owner.PostAsJsonAsync("/api/contracts",
            new { title = $"Entity {u}", entityIds = new[] { entity }, functionIds = new[] { function }, endDate = D(300), autoRenews = true, renewalTermMonths = 12 }, Ct));
        Assert.Matches(@"^JBSAUS\d{7}$", created.GetProperty("reference").GetString());
        var id = created.GetProperty("id").GetInt32();
        var c = (await OkAsync(owner.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract");
        Assert.Equal($"Fictional Meats Pty Ltd {u}", c.GetProperty("entityName").GetString());
        Assert.Equal($"Procurement {u}", c.GetProperty("functionName").GetString());
        Assert.True(c.GetProperty("autoRenews").GetBoolean());
        Assert.Equal(12, c.GetProperty("renewalTermMonths").GetInt32());

        // Only items from the right list; renewing needs a term and can't be evergreen.
        var (s1, b1) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "Wrong list", entityIds = new[] { site }, functionIds = new[] { entity } }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.True(b1.GetProperty("fields").TryGetProperty("entityIds", out _));
        Assert.True(b1.GetProperty("fields").TryGetProperty("functionIds", out _));
        var (s2, b2) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "No term", endDate = D(300), autoRenews = true }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Contains("how many months", b2.GetProperty("fields").GetProperty("renewalTermMonths").GetString());
        var (s3, b3) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "Both", isEvergreen = true, autoRenews = true, renewalTermMonths = 12 }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, s3);
        Assert.True(b3.GetProperty("fields").TryGetProperty("autoRenews", out _));
        // Unticked, the term isn't kept.
        var plain = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"Plain {u}", autoRenews = false, renewalTermMonths = 24 }, Ct))).GetProperty("id").GetInt32();
        Assert.Equal(JsonValueKind.Null, (await OkAsync(owner.GetAsync($"/api/contracts/{plain}", Ct))).GetProperty("contract").GetProperty("renewalTermMonths").ValueKind);

        // The contracts list filters by them.
        var listed = await OkAsync(owner.GetAsync($"/api/contracts?view=all&f.entity={entity}", Ct));
        Assert.Equal([id], listed.EnumerateArray().Select(x => x.GetProperty("id").GetInt32()));

        // The Excel sheet has the columns.
        var book = new XLWorkbook(await (await owner.GetAsync("/api/excel/contracts", Ct)).Content.ReadAsStreamAsync(Ct));
        var headers = book.Worksheet(1).Row(1).CellsUsed().Select(x => x.GetString()).ToList();
        Assert.Contains("Entity", headers);
        Assert.Contains("Function", headers);
        Assert.Contains("Renews automatically", headers);
        Assert.Contains("Renewal term (months)", headers);
    }

    [Fact]
    public async Task A_contract_that_renews_automatically_moves_its_end_date_on_unless_someone_lets_it_end()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var (owner, _, _) = await PersonAsync("Arlo Auto", owner: true);
        var (approver, _, _) = await PersonAsync("Avery Approver", approver: true);
        var rolls = await EffectiveAsync(owner, approver, admin, new { endDate = D(10), noticePeriodDays = 30, autoRenews = true, renewalTermMonths = 12, reviewMonthsBeforeEnd = 3, decision = "Renegotiate" });
        var ends = await EffectiveAsync(owner, approver, admin, new { endDate = D(5), noticePeriodDays = 30, autoRenews = true, renewalTermMonths = 12, decision = "LetItEnd" });
        await using var db = await app.OpenAsync();
        var job = app.Factory.Services.GetRequiredService<ReminderJob>();

        // Before the end date passes nothing moves.
        await job.RunAsync(Today.AddDays(10), Ct);
        Assert.Equal(Today.AddDays(10), DateOnly.FromDateTime(await db.ExecuteScalarAsync<DateTime>("SELECT EndDate FROM contracts.Contract WHERE Id = @rolls", new { rolls })));

        // After: one moves on a term (its decision cleared for the new term); the one let go ends.
        await job.RunAsync(Today.AddDays(11), Ct);
        var after = await db.QuerySingleAsync<(DateTime EndDate, string Stage, string? RenewalDecision)>("SELECT EndDate, Stage, RenewalDecision FROM contracts.Contract WHERE Id = @rolls", new { rolls });
        Assert.Equal(Today.AddDays(10).AddMonths(12), DateOnly.FromDateTime(after.EndDate));
        // A review set as 3 months before the end date moves with it (D-105).
        Assert.Equal(Today.AddDays(10).AddMonths(9), DateOnly.FromDateTime(await db.ExecuteScalarAsync<DateTime>("SELECT ReviewDate FROM contracts.Contract WHERE Id = @rolls", new { rolls })));
        Assert.Equal("Effective", after.Stage);
        Assert.Null(after.RenewalDecision);
        Assert.Equal("Ended", await db.ExecuteScalarAsync<string>("SELECT Stage FROM contracts.Contract WHERE Id = @ends", new { ends }));
        var activity = await db.ExecuteScalarAsync<string>("SELECT Summary FROM contracts.AuditEvent WHERE Action = N'contract.auto-renewed' AND ContractId = @rolls", new { rolls });
        Assert.Contains("renewed automatically for 12 months", activity);
        Assert.Contains("(Renegotiate) was cleared", activity);

        // Running again changes nothing; one long overdue catches up in whole terms, past today.
        await job.RunAsync(Today.AddDays(11), Ct);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent WHERE Action = N'contract.auto-renewed' AND ContractId = @rolls", new { rolls }));
        var was = Today.AddDays(-800);
        var p = new DynamicParameters(new { rolls });
        p.Add("was", was.ToDateTime(TimeOnly.MinValue), System.Data.DbType.Date);
        await db.ExecuteAsync("UPDATE contracts.Contract SET EndDate = @was WHERE Id = @rolls", p);
        await job.RunAsync(Today, Ct);
        Assert.Equal(was.AddMonths(36), DateOnly.FromDateTime(await db.ExecuteScalarAsync<DateTime>("SELECT EndDate FROM contracts.Contract WHERE Id = @rolls", new { rolls })));
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task Imported_contracts_keep_their_Medius_number_and_new_numbers_carry_on_after_the_highest()
    {
        var u = Unique("m");
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var (owner, ownerId, account) = await PersonAsync("Mia Medius", owner: true);
        var category = $"Freight {u}";
        await OkAsync(admin.PostAsJsonAsync("/api/categories", new { name = category, parentId = await app.FamilyAsync() }, Ct));
        var entity = $"Fictional Foods Pty Ltd {u}";
        await ItemAsync(admin, "Entity", entity);
        // A number well past anything else in the test database, so it becomes the highest.
        var n = 2_000_000 + Random.Shared.Next(5_000_000);
        var taken = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"Taken {u}" }, Ct))).GetProperty("reference").GetString()!;

        string[] headers = ["Agreement No", "Agreement Name", "Vendor", "Category", "Contract Owner", "Start Date", "Expiry Date", "Notice Period", "Extension Type", "Extension Period", "Entity", "Status"];
        object?[][] rows =
        [
            [ContractNumbers.Format(n), "Linehaul", $"Vendor {u}", category, account, Today.AddDays(-300), new DateOnly(Today.Year + 1, 6, 30), "3 months", "Automatic", "1 year", entity, "Active"],
            [ContractNumbers.Format(n + 1), "Cartons", $"Vendor {u}", category, account, Today.AddDays(-300), Today.AddDays(200), "60", "Manual", null, null, "Active"],
            [taken, "Clashing number", $"Vendor {u}", category, account, Today.AddDays(-300), Today.AddDays(200), null, "Auto renewal", null, null, "Active"],
            [$"{u}-x", "Unclear renewal", $"Vendor {u}", category, account, Today.AddDays(-300), Today.AddDays(200), null, "Option to extend", "12", null, "Active"],
            [$"{u}-m", "Monthly", $"Vendor {u}", category, account, Today.AddDays(-300), Today.AddDays(200), null, "Auto Extension - Monthly", null, null, "Active"],
            [$"{u}-f", "In full", $"Vendor {u}", category, account, Today.AddDays(-300), Today.AddDays(200), null, "AutoExtensionFull", null, null, "Active"],
            [$"{u}-n", "No extension", $"Vendor {u}", category, account, Today.AddDays(-300), Today.AddDays(200), null, "NoExtension", null, null, "Active"],
        ];
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Export");
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        for (var r = 0; r < rows.Length; r++)
            for (var i = 0; i < rows[r].Length; i++)
                if (rows[r][i] is DateOnly d) sheet.Cell(r + 2, i + 1).Value = d.ToDateTime(TimeOnly.MinValue);
                else if (rows[r][i] is { } v) sheet.Cell(r + 2, i + 1).Value = v.ToString();
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        var file = stream.ToArray();
        MultipartFormDataContent Form(Dictionary<string, string>? mapping = null)
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(file), "file", "medius.xlsx" } };
            if (mapping is not null) form.Add(new StringContent(JsonSerializer.Serialize(mapping)), "mapping");
            return form;
        }

        // Medius's column names are matched to the new fields.
        var columns = await OkAsync(admin.PostAsync("/api/import/legacy/columns", Form(), Ct));
        var suggested = columns.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("header").GetString()!, c => c.GetProperty("suggested").GetString());
        Assert.Equal("autoRenews", suggested["Extension Type"]);
        Assert.Equal("renewalTermMonths", suggested["Extension Period"]);
        Assert.Equal("entity", suggested["Entity"]);
        var mapping = suggested.Where(s => s.Value is not null).ToDictionary(s => s.Key, s => s.Value!);

        var plan = await OkAsync(admin.PostAsync("/api/import/legacy/preview", Form(mapping), Ct));
        var byRow = plan.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("row").GetInt32());
        Assert.Equal("Effective", byRow[2].GetProperty("outcome").GetString());
        Assert.Equal("Effective", byRow[3].GetProperty("outcome").GetString());
        Assert.Contains(byRow[4].GetProperty("problems").EnumerateArray(), x => x.GetString()!.Contains("Renewal term: blank"));
        Assert.Contains(byRow[5].GetProperty("problems").EnumerateArray(), x => x.GetString()!.Contains("isn't clearly yes or no"));
        // Medius's "Auto Extension - Monthly" with no period renews a month at a time.
        Assert.Equal("Effective", byRow[6].GetProperty("outcome").GetString());
        Assert.Contains(byRow[6].GetProperty("notes").EnumerateArray(), x => x.GetString()!.Contains("a month at a time"));
        // "In Full" keeps its end date and carries on under the same terms after it until replaced (Jeff, 27 Nov). "NoExtension" doesn't renew.
        Assert.Equal("Effective", byRow[7].GetProperty("outcome").GetString());
        Assert.Contains(byRow[7].GetProperty("notes").EnumerateArray(), x => x.GetString()!.Contains("keeps its end date"));
        Assert.Equal("Effective", byRow[8].GetProperty("outcome").GetString());
        // Matched to Evergreen by mistake, the Extension Type says where it belongs.
        var wrong = new Dictionary<string, string>(mapping) { ["Extension Type"] = "evergreen" };
        var misplaced = await OkAsync(admin.PostAsync("/api/import/legacy/preview", Form(wrong), Ct));
        var row7 = misplaced.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("row").GetInt32() == 7);
        Assert.Contains(row7.GetProperty("problems").EnumerateArray(), x => x.GetString()!.Contains("choose Renews automatically"));

        // Fix the clash row's renewal so it imports: it keeps its old reference only as the old reference, as the number is taken.
        rows[2][8] = "No";
        using (var fixedBook = new XLWorkbook(new MemoryStream(file)))
        {
            fixedBook.Worksheet(1).Cell(4, 9).Value = "No";
            using var s = new MemoryStream();
            fixedBook.SaveAs(s);
            file = s.ToArray();
        }
        plan = await OkAsync(admin.PostAsync("/api/import/legacy/preview", Form(mapping), Ct));
        byRow = plan.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("row").GetInt32());
        Assert.Contains(byRow[4].GetProperty("notes").EnumerateArray(), x => x.GetString()!.Contains("already another contract's number"));

        var result = await OkAsync(admin.PostAsync("/api/import/legacy/import", Form(mapping), Ct));
        Assert.Equal(6, result.GetProperty("imported").GetInt32());
        await using var db = await app.OpenAsync();
        var imported = (await db.QueryAsync<(string Reference, string LegacyReference, bool AutoRenews, int? RenewalTermMonths, int? NoticePeriodDays, string? Entity, DateTime? EndDate)>("""
            SELECT k.Reference, k.LegacyReference, k.AutoRenews, k.RenewalTermMonths, k.NoticePeriodDays, e.Value, k.EndDate
            FROM contracts.Contract k OUTER APPLY (SELECT TOP (1) li.Value FROM contracts.ContractListValue lv JOIN contracts.ListItem li ON li.Id = lv.ListItemId AND li.ListName = N'Entity' WHERE lv.ContractId = k.Id) e WHERE k.SupplierId = (SELECT Id FROM contracts.Supplier WHERE Name = @name)
            """, new { name = $"Vendor {u}" })).ToDictionary(x => x.LegacyReference);
        var first = imported[ContractNumbers.Format(n)];
        Assert.Equal(ContractNumbers.Format(n), first.Reference);
        Assert.True(first.AutoRenews);
        Assert.Equal(12, first.RenewalTermMonths);
        Assert.Equal(entity, first.Entity);
        var end = DateOnly.FromDateTime(first.EndDate!.Value);
        Assert.Equal(end.DayNumber - end.AddMonths(-3).DayNumber, first.NoticePeriodDays); // 3 months back from 30 June: 92 days
        Assert.Equal(92, first.NoticePeriodDays);
        Assert.False(imported[ContractNumbers.Format(n + 1)].AutoRenews);
        Assert.Equal(60, imported[ContractNumbers.Format(n + 1)].NoticePeriodDays);
        Assert.Matches(@"^JBSAUS\d{7}$", imported[taken].Reference);
        Assert.NotEqual(taken, imported[taken].Reference);
        Assert.Equal((true, (int?)1), (imported[$"{u}-m"].AutoRenews, imported[$"{u}-m"].RenewalTermMonths));
        Assert.False(imported[$"{u}-f"].AutoRenews);
        Assert.False(imported[$"{u}-n"].AutoRenews);
        Assert.Equal(Today.AddDays(200), DateOnly.FromDateTime(imported[$"{u}-f"].EndDate!.Value));
        Assert.False(await db.ExecuteScalarAsync<bool>("SELECT IsEvergreen FROM contracts.Contract WHERE LegacyReference = @r", new { r = $"{u}-f" }));

        // The next new contract carries on after the highest number.
        var next = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"After import {u}" }, Ct))).GetProperty("reference").GetString()!;
        Assert.True(int.Parse(next[6..]) > n + 1, next);
        _ = ownerId;
    }

    [Fact]
    public async Task A_review_date_can_be_a_period_before_the_end_date_that_moves_with_it()
    {
        var (owner, _, _) = await PersonAsync("Remy Review", owner: true);
        var end = Today.AddDays(400);
        var created = await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = Unique("Review "), endDate = end.ToString("yyyy-MM-dd"), reviewMonthsBeforeEnd = 6 }, Ct));
        var id = created.GetProperty("id").GetInt32();
        var c = (await OkAsync(owner.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract");
        Assert.Equal(end.AddMonths(-6).ToString("yyyy-MM-dd"), c.GetProperty("reviewDate").GetString());
        Assert.Equal(6, c.GetProperty("reviewMonthsBeforeEnd").GetInt32());

        // Moving the end date moves the review; choosing a date replaces the period.
        var later = end.AddMonths(2);
        await OkAsync(owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = c.GetProperty("title").GetString(), endDate = later.ToString("yyyy-MM-dd"), reviewMonthsBeforeEnd = 6, rowVersion = c.GetProperty("rowVersion").GetString() }, Ct));
        c = (await OkAsync(owner.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract");
        Assert.Equal(later.AddMonths(-6).ToString("yyyy-MM-dd"), c.GetProperty("reviewDate").GetString());
        await OkAsync(owner.PutAsJsonAsync($"/api/contracts/{id}", new { title = c.GetProperty("title").GetString(), endDate = later.ToString("yyyy-MM-dd"), reviewDate = D(30), rowVersion = c.GetProperty("rowVersion").GetString() }, Ct));
        c = (await OkAsync(owner.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract");
        Assert.Equal(D(30), c.GetProperty("reviewDate").GetString());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("reviewMonthsBeforeEnd").ValueKind);

        // Evergreen contracts have no end date to count back from; a draft without an end date waits for one.
        var (s, b) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "Evergreen review", isEvergreen = true, reviewMonthsBeforeEnd = 3 }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, s);
        Assert.True(b.GetProperty("fields").TryGetProperty("reviewDate", out _));
        var waiting = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = Unique("No end yet "), reviewMonthsBeforeEnd = 3 }, Ct))).GetProperty("id").GetInt32();
        Assert.Equal(JsonValueKind.Null, (await OkAsync(owner.GetAsync($"/api/contracts/{waiting}", Ct))).GetProperty("contract").GetProperty("reviewDate").ValueKind);
    }

    [Theory]
    [InlineData("90", 90, "day")]
    [InlineData("90 days", 90, "day")]
    [InlineData("3 Months", 3, "month")]
    [InlineData("1 year", 1, "year")]
    [InlineData("6 wks", 6, "week")]
    [InlineData("12.0", 12, "day")]
    [InlineData("three months", null, "")]
    [InlineData("3 fortnights", null, "")]
    public void Periods_are_read_in_days_weeks_months_or_years(string text, int? number, string unit) =>
        Assert.Equal((number, unit), LegacyImportEndpoints.Period(text));

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("Automatic", true)]
    [InlineData("Auto renewal", true)]
    [InlineData("Auto-renew", true)]
    [InlineData("No", false)]
    [InlineData("Manual", false)]
    [InlineData("None", false)]
    [InlineData("Fixed term", false)]
    [InlineData("No auto renewal", false)]
    [InlineData("Option to extend", null)]
    [InlineData("AutoExtensionMonthly", true)] // Medius writes its Extension Type as one word
    [InlineData("NoExtension", false)]
    public void Renewal_settings_are_read_as_automatic_or_not(string text, bool? renews) =>
        Assert.Equal(renews, LegacyImportEndpoints.RenewsSays(text));
}
