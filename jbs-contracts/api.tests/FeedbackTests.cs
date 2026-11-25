using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace JbsContracts.Api.Tests;

/// <summary>Feedback (D-101): your own for everyone, everyone's for admins, an email to admins, nothing deleted.</summary>
public sealed class FeedbackTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync(Ct);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private async Task<HttpClient> PersonAsync(string name)
    {
        var user = $"fb{Guid.NewGuid():N}"[..12];
        await app.AddPersonAsync(user, $"{name} {user[^4..]}", AppFixture.NoRoles);
        return app.ClientFor($@"TEST\{user}");
    }

    [Fact]
    public async Task People_see_their_own_feedback_and_admins_see_and_answer_all_of_it()
    {
        using var fran = await PersonAsync("Fran Feedback");
        using var other = await PersonAsync("Otto Other");
        using var admin = app.ClientFor(AppFixture.AdminAccount);

        Assert.Equal(HttpStatusCode.BadRequest, (await fran.PostAsJsonAsync("/api/feedback", new { kind = "Bug", comment = "  " }, Ct)).StatusCode);
        var sent = await fran.PostAsJsonAsync("/api/feedback", new { kind = "Sideways", comment = "The search box could be bigger.", screen = "Contracts (/contracts)" }, Ct);
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var id = (await ReadAsync(sent)).GetProperty("id").GetInt32();

        // Unknown kinds are saved as Other; the admin is emailed.
        var mine = await ReadAsync(await fran.GetAsync("/api/feedback", Ct));
        var item = mine.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == id);
        Assert.Equal("Other", item.GetProperty("kind").GetString());
        Assert.Equal("Open", item.GetProperty("status").GetString());
        Assert.False(mine.GetProperty("canSeeAll").GetBoolean());
        await using (var db = await app.OpenAsync())
            Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.EmailOutbox WHERE Kind = N'feedback-new' AND DedupeKey LIKE @key", new { key = $"feedback-new:{id}:%" }));

        // Someone else can't see, reply to, change or archive it; asking for everyone's just gives their own.
        Assert.DoesNotContain((await ReadAsync(await other.GetAsync("/api/feedback?all=true", Ct))).GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt32() == id);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/feedback/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/feedback/{id}/replies", new { text = "Me too" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "Done" }, Ct)).StatusCode);

        // The admin sees everyone's, replies and sets the status; an unknown status is refused; only admins archive.
        var all = await ReadAsync(await admin.GetAsync("/api/feedback?all=true", Ct));
        Assert.True(all.GetProperty("showingAll").GetBoolean());
        Assert.Contains(all.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetInt32() == id);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/feedback/{id}/replies", new { text = "" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/feedback/{id}/replies", new { text = "Thanks, we'll look at it." }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "Maybe" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "InProgress" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fran.PutAsJsonAsync($"/api/feedback/{id}/archive", new { archived = true }, Ct)).StatusCode);

        // The person who sent it sees the reply and can close it themselves.
        var detail = await ReadAsync(await fran.GetAsync($"/api/feedback/{id}", Ct));
        Assert.Equal("Thanks, we'll look at it.", detail.GetProperty("replies")[0].GetProperty("text").GetString());
        Assert.Equal("In progress", detail.GetProperty("item").GetProperty("statusName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await fran.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "Done" }, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/feedback/{id}/archive", new { archived = true }, Ct)).StatusCode);
        Assert.True((await ReadAsync(await fran.GetAsync("/api/feedback", Ct))).GetProperty("counts").GetProperty("archived").GetInt32() >= 1);
        await using var check = await app.OpenAsync();
        Assert.True(await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM contracts.AuditEvent WHERE SubjectType = N'Feedback' AND SubjectId = @id", new { id }) >= 5);
    }
}
