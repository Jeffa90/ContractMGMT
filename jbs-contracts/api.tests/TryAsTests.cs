using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;

namespace JbsContracts.Api.Tests;

/// <summary>QA "Try as" (D-81): admins only, audited, and everything done meanwhile shows who really did it.</summary>
public sealed class TryAsTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_admin_tries_the_app_as_someone_else_and_every_action_shows_who_really_did_it()
    {
        var user = $"tl{Guid.NewGuid():N}"[..12];
        var legal = await app.AddPersonAsync(user, "Tess Tryas", new { contractOwner = false, legal = true, approver = false, groupHead = false, admin = false, auditor = false });
        var legalId = legal.GetProperty("id").GetInt32();
        using var plain = app.ClientFor($@"TEST\{user}");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync("/api/qa/try-as", new { personId = 1 }, Ct)).StatusCode);
        Assert.False((await plain.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("canTryAs").GetBoolean());

        using var admin = app.ClientFor(AppFixture.AdminAccount); // keeps its cookies, like a browser
        Assert.True((await admin.GetFromJsonAsync<JsonElement>("/api/me", Ct)).GetProperty("canTryAs").GetBoolean());
        Assert.Contains((await admin.GetFromJsonAsync<JsonElement>("/api/qa/people", Ct)).EnumerateArray(), p => p.GetProperty("id").GetInt32() == legalId);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/qa/try-as", new { personId = legalId }, Ct)).StatusCode);

        var me = await admin.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        Assert.Equal(legalId, me.GetProperty("person").GetProperty("id").GetInt32());
        Assert.True(me.GetProperty("person").GetProperty("roles").GetProperty("legal").GetBoolean());
        Assert.False(me.GetProperty("person").GetProperty("roles").GetProperty("admin").GetBoolean());
        Assert.Equal(AppFixture.AdminName, me.GetProperty("tryingAs").GetProperty("realName").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/people", Ct)).StatusCode); // not an admin while trying as Legal

        // Something done meanwhile records the admin.
        await admin.PutAsJsonAsync("/api/home/preferences", new { hiddenTiles = Array.Empty<string>(), firstGroup = "turn" }, Ct);
        await using var db = await app.OpenAsync();
        var started = await db.QuerySingleAsync<(int? PersonId, string ActorName, string Summary)>("SELECT TOP 1 PersonId, ActorName, Summary FROM contracts.AuditEvent WHERE Action = 'qa.try-as' AND SubjectId = @legalId ORDER BY Id DESC", new { legalId });
        Assert.Equal(AppFixture.AdminName, started.ActorName);
        Assert.Contains("started trying JBS Contracts as Tess Tryas", started.Summary);

        // Stopping returns the admin to themselves.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/qa/try-as", new { personId = (int?)null }, Ct)).StatusCode);
        var back = await admin.GetFromJsonAsync<JsonElement>("/api/me", Ct);
        Assert.Equal(JsonValueKind.Null, back.GetProperty("tryingAs").ValueKind);
        Assert.True(back.GetProperty("person").GetProperty("roles").GetProperty("admin").GetBoolean());
        Assert.Null(await AuditLog.VerifyAsync(db));
    }
}
