using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace JbsContracts.Api.Tests;

/// <summary>
/// D-121: a contract that continues until replaced keeps its end date; past it with no decision it reads "Continuing until replaced",
/// not overdue (Renewals, Home, Team). It can't also be evergreen or renew automatically.
/// </summary>
public sealed class ContinuesUntilReplacedTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];
    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));

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

    [Fact]
    public async Task Past_its_end_date_it_is_continuing_until_replaced_not_overdue()
    {
        var u = Unique("c");
        var user = Unique("cu");
        await app.AddPersonAsync(user, $"Cara Continues {u[^4..]}", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = true, auditor = false });
        using var owner = app.ClientFor($@"TEST\{user}");

        // It can't also be evergreen or renew automatically.
        var (s1, b1) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "Both", isEvergreen = true, continuesUntilReplaced = true }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, s1);
        Assert.True(b1.GetProperty("fields").TryGetProperty("continuesUntilReplaced", out _));
        var (s2, _) = await SendAsync(owner.PostAsJsonAsync("/api/contracts", new { title = "Renews", endDate = Today.AddDays(30).ToString("yyyy-MM-dd"), autoRenews = true, renewalTermMonths = 12, continuesUntilReplaced = true }, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, s2);

        var endedOn = Today.AddDays(-40).ToString("yyyy-MM-dd");
        var carries = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"Carries on {u}", endDate = endedOn, noticePeriodDays = 30, continuesUntilReplaced = true }, Ct))).GetProperty("id").GetInt32();
        var plain = (await OkAsync(owner.PostAsJsonAsync("/api/contracts", new { title = $"Plain {u}", endDate = endedOn, noticePeriodDays = 30 }, Ct))).GetProperty("id").GetInt32();
        Assert.True((await OkAsync(owner.GetAsync($"/api/contracts/{carries}", Ct))).GetProperty("contract").GetProperty("continuesUntilReplaced").GetBoolean());
        await using var db = await app.OpenAsync();
        await db.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Effective' WHERE Id IN @ids", new { ids = new[] { carries, plain } });

        // Renewals: the plain one is overdue by its notice deadline; the other is Continuing, dated by its end date.
        var items = (await OkAsync(owner.GetAsync("/api/renewals?days=30&mine=true", Ct))).GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetInt32());
        Assert.Equal(("Continuing", endedOn), (items[carries].GetProperty("keyKind").GetString(), items[carries].GetProperty("keyDate").GetString()));
        Assert.Equal("Notice", items[plain].GetProperty("keyKind").GetString());

        // Home's urgent list leaves it out; the plain one is there.
        var urgent = (await OkAsync(owner.GetAsync("/api/home", Ct))).GetProperty("urgent").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToList();
        Assert.Contains(plain, urgent);
        Assert.DoesNotContain(carries, urgent);

        // Switched off on a draft: Activity says so in plain words.
        await db.ExecuteAsync("UPDATE contracts.Contract SET Stage = N'Draft' WHERE Id = @carries", new { carries });
        var rv = (await OkAsync(owner.GetAsync($"/api/contracts/{carries}", Ct))).GetProperty("contract").GetProperty("rowVersion").GetString();
        await OkAsync(owner.PutAsJsonAsync($"/api/contracts/{carries}", new { title = $"Carries on {u}", endDate = endedOn, noticePeriodDays = 30, continuesUntilReplaced = false, rowVersion = rv }, Ct));
        var changed = await db.ExecuteScalarAsync<string>("SELECT TOP (1) Summary FROM contracts.AuditEvent WHERE ContractId = @carries AND Action = N'contract.changed' ORDER BY Id DESC", new { carries });
        Assert.Contains("continues until replaced from yes to no", changed);
    }
}
