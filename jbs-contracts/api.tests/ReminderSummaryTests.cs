using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Features;
using Microsoft.Extensions.DependencyInjection;

namespace JbsContracts.Api.Tests;

/// <summary>
/// Reminder summaries and switching a reminder off (D-99). Runs on its own (with the demo data test), because switching a reminder off
/// would upset other tests that run the morning job.
/// </summary>
[Collection(nameof(DemoDataTests))]
public sealed class ReminderSummaryTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private async Task<(HttpClient Client, int Id)> OwnerAsync(string name, string? delivery)
    {
        var user = $"rs{Guid.NewGuid():N}"[..12];
        var person = await app.AddPersonAsync(user, $"{name} {user[^4..]}", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        var client = app.ClientFor($@"TEST\{user}");
        if (delivery is not null) Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/me/reminder-delivery", new { delivery }, Ct)).StatusCode);
        return (client, person.GetProperty("id").GetInt32());
    }

    private async Task<int> EffectiveAsync(HttpClient owner, int endInDays)
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var approverUser = $"ra{Guid.NewGuid():N}"[..12];
        await app.AddPersonAsync(approverUser, $"Rae Approver {approverUser[^4..]}", new { contractOwner = false, legal = false, approver = true, groupHead = false, admin = false, auditor = false });
        using var approver = app.ClientFor($@"TEST\{approverUser}");
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = $"Cat {approverUser}", parentId = await app.FamilyAsync() }, Ct));
        var supplier = await ReadAsync(await owner.PostAsJsonAsync("/api/suppliers", new { name = $"Supplier {approverUser}" }, Ct));
        var id = (await ReadAsync(await owner.PostAsJsonAsync("/api/contracts", new
        {
            title = $"Summary test {approverUser}", categoryId = category.GetProperty("id").GetInt32(), supplierId = supplier.GetProperty("id").GetInt32(), endDate = D(endInDays), noticePeriodDays = 30, needsLegalReview = false,
        }, Ct))).GetProperty("id").GetInt32();
        async Task Act(HttpClient client, string action, object? extra = null)
        {
            var detail = await ReadAsync(await client.GetAsync($"/api/contracts/{id}", Ct));
            var body = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
            body["rowVersion"] = detail.GetProperty("contract").GetProperty("rowVersion").GetString();
            var r = await client.PostAsJsonAsync($"/api/contracts/{id}/{action}", body, Ct);
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync(Ct));
        }
        await Act(owner, "ready-for-signing");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("%PDF signed"u8.ToArray()), "file", "signed.pdf");
            form.Add(new StringContent("SignedCopy"), "kind");
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
        }
        await Act(owner, "send-for-approval", new { effectiveDate = D(-100) });
        await Act(approver, "approval/approve");
        return id;
    }

    private Task<ReminderJob.Result> RunJobAsync(DateOnly day) => app.Factory.Services.GetRequiredService<ReminderJob>().RunAsync(day, Ct);

    [Fact]
    public async Task A_daily_summary_puts_someones_reminders_in_one_email()
    {
        var (owner, ownerId) = await OwnerAsync("Dee Daily", "Daily");
        var id = await EffectiveAsync(owner, endInDays: 40); // notice deadline in 10 days, end date in 40
        await RunJobAsync(Today);
        await using var db = await app.OpenAsync();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind LIKE N'reminder-%'", new { id }));
        var summary = await db.QuerySingleAsync<(string Subject, string BodyText)>(
            "SELECT Subject, BodyText FROM contracts.EmailOutbox WHERE PersonId = @ownerId AND Kind = N'reminder-summary'", new { ownerId });
        Assert.Contains("things coming up", summary.Subject);
        Assert.Contains("Notice deadline for", summary.BodyText);
        Assert.Contains("one email a day", summary.BodyText);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ReminderSummaryItem WHERE PersonId = @ownerId AND SentUtc IS NULL", new { ownerId }));
        // The same morning again sends nothing more.
        await RunJobAsync(Today);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE PersonId = @ownerId AND Kind = N'reminder-summary'", new { ownerId }));
    }

    [Fact]
    public async Task A_weekly_summary_waits_for_Monday_unless_something_is_close()
    {
        var (owner, ownerId) = await OwnerAsync("Wes Weekly", "Weekly");
        // The next Tuesday, and a contract whose notice deadline is 40 days after it (the 60-day notice step is due).
        var tuesday = Today.AddDays(((int)DayOfWeek.Tuesday - (int)Today.DayOfWeek + 7) % 7 + 7);
        await EffectiveAsync(owner, endInDays: tuesday.DayNumber - Today.DayNumber + 70);
        await RunJobAsync(tuesday);
        await using var db = await app.OpenAsync();
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE PersonId = @ownerId AND Kind = N'reminder-summary'", new { ownerId }));
        Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ReminderSummaryItem WHERE PersonId = @ownerId AND SentUtc IS NULL", new { ownerId }) > 0);
        await RunJobAsync(tuesday.AddDays(6)); // Monday
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE PersonId = @ownerId AND Kind = N'reminder-summary'", new { ownerId }));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.ReminderSummaryItem WHERE PersonId = @ownerId AND SentUtc IS NULL", new { ownerId }));
    }

    [Fact]
    public async Task A_reminder_switched_off_isnt_sent_and_the_default_can_change()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var (owner, ownerId) = await OwnerAsync("Ollie Off", null);
        Assert.Equal("Each", (await ReadAsync(await owner.GetAsync("/api/me", Ct))).GetProperty("defaultReminderDelivery").GetString());
        var id = await EffectiveAsync(owner, endInDays: 40);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/reminders/on", new { kind = "Notice", isOn = false }, Ct)).StatusCode);
        try
        {
            var settings = await ReadAsync(await admin.GetAsync("/api/admin/reminders", Ct));
            Assert.False(settings.GetProperty("emails").EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "Notice").GetProperty("isOn").GetBoolean());
            await RunJobAsync(Today);
            await using var db = await app.OpenAsync();
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = N'reminder-notice'", new { id }));
            Assert.True(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE ContractId = @id AND Kind = N'reminder-end'", new { id }) > 0); // others still go
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/admin/reminders/on", new { kind = "Notice", isOn = true }, Ct)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/me/reminder-delivery", new { delivery = "Hourly" }, Ct)).StatusCode);
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/reminders/on", new { kind = "Notice", isOn = true }, Ct)).StatusCode);
        }
        // The default for everyone (admins only), shown to people who haven't chosen; put back afterwards.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/reminders/delivery", new { delivery = "Weekly" }, Ct)).StatusCode);
        Assert.Equal("Weekly", (await ReadAsync(await owner.GetAsync("/api/me", Ct))).GetProperty("defaultReminderDelivery").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/reminders/delivery", new { delivery = "Each" }, Ct)).StatusCode);
        _ = ownerId;
    }
}
