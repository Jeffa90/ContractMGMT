using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Features;
using JbsContracts.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace JbsContracts.Api.Tests;

/// <summary>
/// Slice 6: renewal decisions, renewing, ending, archiving, review done, the daily reminder job and the admin reminder settings.
/// One class, so these run one after another: the job and the settings apply to every contract.
/// </summary>
public sealed class RenewalTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private async Task<(HttpClient Client, int Id, string Name)> PersonAsync(string name, object roles)
    {
        var user = Unique("rn");
        var full = $"{name} {user[^4..]}";
        var person = await app.AddPersonAsync(user, full, roles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32(), full);
    }

    private static object Roles(bool owner = false, bool approver = false, bool admin = false) =>
        new { contractOwner = owner, legal = false, approver, groupHead = false, admin, auditor = false };

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

    private static async Task OkAsync(HttpClient client, int id, string action, object? extra = null)
    {
        var (status, body) = await ActAsync(client, id, action, extra);
        Assert.True(status == HttpStatusCode.OK, $"{action}: {body}");
    }

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
        await SignAndApproveAsync(owner, approver, id);
        return id;
    }

    private static async Task SignAndApproveAsync(HttpClient owner, HttpClient approver, int id)
    {
        await OkAsync(owner, id, "ready-for-signing");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("%PDF signed"u8.ToArray()), "file", "signed.pdf");
            form.Add(new StringContent("SignedCopy"), "kind");
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
        }
        await OkAsync(owner, id, "send-for-approval", new { effectiveDate = D(-100) });
        await OkAsync(approver, id, "approval/approve");
    }

    private Task<ReminderJob.Result> RunJobAsync(int dayOffset) => app.Factory.Services.GetRequiredService<ReminderJob>().RunAsync(Today.AddDays(dayOffset), Ct);

    private static string Stage(JsonElement detail) => detail.GetProperty("contract").GetProperty("stage").GetString()!;

    [Fact]
    public async Task Renewal_decisions_renewing_ending_archiving_and_review_done()
    {
        var (owner, _, _) = await PersonAsync("Rita Renew", Roles(owner: true));
        var (approver, _, _) = await PersonAsync("Abe Approver", Roles(approver: true));
        var (viewer, _, _) = await PersonAsync("Val Viewer", AppFixture.NoRoles);
        using var admin = app.ClientFor(AppFixture.AdminAccount);

        // ---- Renew: a linked draft; approving it ends the old contract.
        var old = await EffectiveAsync(owner, approver, new { title = "Freight renewal test", endDate = D(60), noticePeriodDays = 30, value = 500000 });
        var effective = await DetailAsync(owner, old);
        // "anyTimeFields" depends on the shared layout, which LayoutTests changes, so it is left out here.
        Assert.Equal(["recordDecision", "endEarly", "reviewDone", "handOver", "startVariation"], effective.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).Where(a => a != "anyTimeFields").ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(viewer, old, "renewal-decision", new { decision = "Renew" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, old, "renewal-decision", new { decision = "Maybe" })).Status);
        var (renewStatus, renew) = await ActAsync(owner, old, "renewal-decision", new { decision = "Renew", note = "Rates are good; renew for two years." });
        Assert.Equal(HttpStatusCode.OK, renewStatus);
        var renewalId = renew.GetProperty("result").GetProperty("renewalId").GetInt32();
        var renewal = await DetailAsync(owner, renewalId);
        Assert.Equal("Draft", Stage(renewal));
        Assert.Equal("Freight renewal test (renewal)", renewal.GetProperty("contract").GetProperty("title").GetString());
        Assert.Equal(old, renewal.GetProperty("contract").GetProperty("renewsContractId").GetInt32());
        Assert.Equal(D(61), renewal.GetProperty("contract").GetProperty("startDate").GetString());
        Assert.Equal("Renew", (await DetailAsync(owner, old)).GetProperty("renewal").GetProperty("decision").GetString());
        // Recording Renew again doesn't start a second renewal.
        var (_, again) = await ActAsync(owner, old, "renewal-decision", new { decision = "Renew" });
        Assert.Equal(renewalId, again.GetProperty("result").GetProperty("renewalId").GetInt32());
        // The renewal goes through the normal journey; when it's approved, the old one ends.
        var rv = (await DetailAsync(owner, renewalId)).GetProperty("contract").GetProperty("rowVersion").GetString();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/contracts/{renewalId}", new
        {
            title = "Freight renewal test (renewal)", supplierId = renewal.GetProperty("contract").GetProperty("supplierId").GetInt32(),
            categoryId = renewal.GetProperty("contract").GetProperty("categoryId").GetInt32(), needsLegalReview = false, startDate = D(61), endDate = D(61 + 730), rowVersion = rv,
        }, Ct)).StatusCode);
        await SignAndApproveAsync(owner, approver, renewalId);
        var ended = await DetailAsync(owner, old);
        Assert.Equal("Ended", Stage(ended));
        Assert.Contains("Renewed by", ended.GetProperty("contract").GetProperty("endedReason").GetString());
        Assert.Equal(renewalId, ended.GetProperty("contract").GetProperty("renewedById").GetInt32());

        // ---- Archive: admins only, Ended only.
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(owner, old, "archive")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await ActAsync(admin, renewalId, "archive")).Status); // Effective, not Ended
        await OkAsync(admin, old, "archive");
        Assert.Equal("Archived", Stage(await DetailAsync(owner, old)));

        // ---- End early: today ends it now; a later date ends it then (and stops reminders).
        var now = await EffectiveAsync(owner, approver, new { endDate = D(200) });
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, now, "end", new { date = D(0) })).Status); // reason required
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, now, "end", new { note = "x", date = D(300) })).Status); // after its end date
        await OkAsync(owner, now, "end", new { note = "Supplier went into administration.", date = D(0) });
        Assert.Equal("Ended", Stage(await DetailAsync(owner, now)));
        var later = await EffectiveAsync(owner, approver, new { endDate = D(200) });
        await OkAsync(owner, later, "end", new { note = "Site is closing.", date = D(20) });
        var planned = await DetailAsync(owner, later);
        Assert.Equal(("Effective", D(20), "LetItEnd"), (Stage(planned), planned.GetProperty("renewal").GetProperty("plannedEndDate").GetString(), planned.GetProperty("renewal").GetProperty("decision").GetString()));

        // ---- Let it end on an evergreen contract needs the date it ends (D-68).
        var evergreen = await EffectiveAsync(owner, approver, new { isEvergreen = true, reviewDate = D(100), noticePeriodDays = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, evergreen, "renewal-decision", new { decision = "LetItEnd" })).Status);
        await OkAsync(owner, evergreen, "renewal-decision", new { decision = "LetItEnd", date = D(45) });

        // ---- Review done: the next date must be later than today; allowed on a locked contract (D-69).
        Assert.Equal(HttpStatusCode.BadRequest, (await ActAsync(owner, evergreen, "review-done", new { date = D(0) })).Status);
        await OkAsync(owner, evergreen, "review-done", new { date = D(365) });
        var reviewed = (await DetailAsync(owner, evergreen)).GetProperty("contract");
        Assert.Equal((D(0), D(365)), (reviewed.GetProperty("lastReviewedOn").GetString(), reviewed.GetProperty("reviewDate").GetString()));

        // ---- The daily job ends contracts on their date.
        await RunJobAsync(20);
        Assert.Equal("Ended", Stage(await DetailAsync(owner, later)));
        Assert.Equal("Site is closing.", (await DetailAsync(owner, later)).GetProperty("contract").GetProperty("endedReason").GetString());
        Assert.Equal("Effective", Stage(await DetailAsync(owner, evergreen)));
        await RunJobAsync(45);
        Assert.Equal("Ended", Stage(await DetailAsync(owner, evergreen)));

        await using var db = await app.OpenAsync();
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task The_daily_job_sends_each_reminder_once_escalates_and_stops_after_a_decision()
    {
        var (owner, ownerId, _) = await PersonAsync("Remi Owner", Roles(owner: true));
        var (backup, backupId, _) = await PersonAsync("Bea Backup", Roles(owner: true));
        var (approver, _, _) = await PersonAsync("Ari Approver", Roles(approver: true));
        // Notice deadline in 20 days (end in 50, notice 30); end date in 50 days; review in 25 days.
        var id = await EffectiveAsync(owner, approver, new { endDate = D(50), noticePeriodDays = 30, reviewDate = D(25), backupOwnerId = backupId });
        await using var db = await app.OpenAsync();
        async Task<List<(string Kind, int DaysBefore)>> Sent() =>
            (await db.QueryAsync<(string, int)>("SELECT Kind, DaysBefore FROM contracts.Reminder WHERE ContractId = @id ORDER BY Id", new { id })).ToList();
        async Task<int> Emails(string kind) => await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = @kind", new { id, kind });

        // Today: the 30-day notice step (not the 90 and 60 ones it started too late for), the 90-day end step and the 30-day review step.
        await RunJobAsync(0);
        Assert.Equal([("Notice", 30), ("End", 90), ("Review", 30)], await Sent());
        Assert.Equal(2, await Emails("reminder-notice")); // owner and backup
        var email = await db.QuerySingleAsync<(string Subject, string BodyText)>("SELECT TOP 1 Subject, BodyText FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'reminder-notice' AND PersonId = @ownerId", new { id, ownerId });
        Assert.Contains("notice deadline in 20 days", email.Subject);
        Assert.Contains("Hi Remi,", email.BodyText);

        // Running again the same day, or after a restart, sends nothing more.
        await RunJobAsync(0);
        Assert.Equal(3, (await Sent()).Count);

        // 14 days before the deadline: the 14-day step; 7 days before: the 7-day step and the escalation (owner and backup are copied).
        await RunJobAsync(6);
        Assert.Contains(("Notice", 14), await Sent());
        await RunJobAsync(13);
        var sent = await Sent();
        Assert.Contains(("Notice", 7), sent);
        Assert.Contains(("Escalation", 7), sent);
        Assert.True(await Emails("reminder-escalation") >= 2);

        // A decision stops notice, escalation and end-date reminders; review reminders carry on.
        await OkAsync(owner, id, "renewal-decision", new { decision = "Renegotiate" });
        var before = (await Sent()).Count;
        await RunJobAsync(20); // end date in 30 days, review in 5 days
        // (End date in 30 days would be due, but the decision stops it; the review's 30-day step was already sent.)
        Assert.Equal(before, (await Sent()).Count);
        Assert.DoesNotContain((await Sent()).Skip(before), r => r.Kind is "Notice" or "Escalation" or "End");

        // The reminders are listed on the contract.
        var reminders = (await DetailAsync(owner, id)).GetProperty("reminders").EnumerateArray().ToList();
        Assert.NotEmpty(reminders);
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task Admins_change_reminder_days_and_wording_with_history_and_closed_placeholders()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var (owner, ownerId, _) = await PersonAsync("Wendy Wording", Roles(owner: true));
        var (approver, _, _) = await PersonAsync("Al Approver", Roles(approver: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/admin/reminders", Ct)).StatusCode);
        var start = await admin.GetFromJsonAsync<JsonElement>("/api/admin/reminders", Ct);
        var review = start.GetProperty("emails").EnumerateArray().Single(k => k.GetProperty("kind").GetString() == "Review");
        var (oldSubject, oldBody) = (review.GetProperty("subject").GetString()!, review.GetProperty("body").GetString()!);
        try
        {
            // Days: whole days between 1 and 365, largest first.
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/reminders/days", new { kind = "Review", days = new[] { 0 } }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/reminders/days", new { kind = "Review", days = new[] { 10, 45 } }, Ct)).StatusCode);
            // Wording: only the fixed placeholders; anything else is refused in plain words.
            var bad = await admin.PutAsJsonAsync("/api/admin/reminders/wording", new { kind = "Review", subject = "Review {Contract}", body = "Hi" }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Contains("{Contract} isn't a placeholder", (await ReadAsync(bad)).GetProperty("message").GetString());
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/reminders/wording", new
            {
                kind = "Review", subject = "Review {Reference} by {Due date}", body = "G'day {First name},\n\nPlease review <b>{Contract title}</b>.",
            }, Ct)).StatusCode);

            // The next reminder uses the new days and wording; wording is plain text, so tags are encoded, not run.
            var id = await EffectiveAsync(owner, approver, new { endDate = D(400), reviewDate = D(40) });
            await RunJobAsync(0);
            await using var db = await app.OpenAsync();
            Assert.Equal(45, await db.ExecuteScalarAsync<int>("SELECT DaysBefore FROM contracts.Reminder WHERE ContractId = @id AND Kind = 'Review'", new { id }));
            var email = await db.QuerySingleAsync<(string Subject, string BodyHtml)>("SELECT Subject, BodyHtml FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'reminder-review' AND PersonId = @ownerId", new { id, ownerId });
            Assert.StartsWith("Review JBSAUS", email.Subject);
            Assert.Contains("&lt;b&gt;", email.BodyHtml);
            Assert.DoesNotContain("<b>", email.BodyHtml);

            var after = await admin.GetFromJsonAsync<JsonElement>("/api/admin/reminders", Ct);
            var history = after.GetProperty("history").EnumerateArray().Take(2).Select(h => h.GetProperty("what").GetString()).ToArray();
            Assert.Equal(["Wording", "Days"], history);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/reminders/days", new { kind = "Review", days = new[] { 90, 60, 30 } }, Ct);
            await admin.PutAsJsonAsync("/api/admin/reminders/wording", new { kind = "Review", subject = oldSubject, body = oldBody }, Ct);
        }
    }

    [Fact]
    public async Task Every_email_is_editable_with_its_own_placeholders_and_empty_paragraphs_are_left_out()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/admin/reminders", Ct);
        var kinds = all.GetProperty("emails").EnumerateArray().Select(e => e.GetProperty("kind").GetString()!).ToArray();
        Assert.Superset(new HashSet<string> { "AccessGranted", "LegalNew", "BackFromLegal", "LegalCleared", "ApprovalWaiting", "Approved", "ApprovalSentBack", "Notice" }, kinds.ToHashSet());
        var approved = all.GetProperty("emails").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "Approved");
        var (subject, body) = (approved.GetProperty("subject").GetString()!, approved.GetProperty("body").GetString()!);
        try
        {
            // Placeholders are per email: the welcome email has no contract, so {Reference} is refused there.
            var wrong = await admin.PutAsJsonAsync("/api/admin/reminders/wording", new { kind = "AccessGranted", subject = "Welcome", body = "Hi {First name}, see {Reference}." }, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Contains("{Reference} isn't a placeholder this email can use", (await ReadAsync(wrong)).GetProperty("message").GetString());
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/reminders/wording", new
            {
                kind = "Approved", subject = "Good news: {Reference} is live", body = "G'day {First name},\n\n{Approver} approved {Contract title}.",
            }, Ct)).StatusCode);
            var (owner, ownerId, _) = await PersonAsync("Nina Newcomer", Roles(owner: true));
            var (approver, _, approverName) = await PersonAsync("Ollie Okay", Roles(approver: true));
            var id = await EffectiveAsync(owner, approver, new { title = "Wording test", endDate = D(400) });
            await using var db = await app.OpenAsync();
            var email = await db.QuerySingleAsync<(string Subject, string BodyText)>("SELECT Subject, BodyText FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = 'approved' AND PersonId = @ownerId", new { id, ownerId });
            Assert.StartsWith("Good news: JBSAUS", email.Subject);
            Assert.StartsWith($"G'day Nina,\n\n{approverName} approved Wording test.", email.BodyText);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/reminders/wording", new { kind = "Approved", subject, body }, Ct);
        }

        // A paragraph whose placeholder is empty (no note) is left out; values are never treated as HTML.
        var withNote = EmailWording.Compose("LegalNew", "{Reference}", "Hi {First name},\n\nSent.\n\n{Sent by} wrote: \"{Note}\"", new Dictionary<string, string?>
        {
            ["First name"] = "Sam", ["Reference"] = "JBSAUS-0001", ["Sent by"] = "<Jo>", ["Note"] = "Please check clause 12.",
        }, null);
        Assert.Contains("&lt;Jo&gt; wrote", withNote.Html);
        var noNote = EmailWording.Compose("LegalNew", "{Reference}", "Hi {First name},\n\nSent.\n\n{Sent by} wrote: \"{Note}\"", new Dictionary<string, string?>
        {
            ["First name"] = "Sam", ["Reference"] = "JBSAUS-0001", ["Sent by"] = "Jo", ["Note"] = null,
        }, null);
        Assert.DoesNotContain("wrote", noNote.Text);
        Assert.StartsWith("Hi Sam,\n\nSent.", noNote.Text);
    }
}
