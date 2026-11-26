using System.Net.Http.Json;
using System.Text.Json;

namespace JbsContracts.Api.Tests;

/// <summary>
/// Pick lists reach the browser as { id, name }. Untyped database rows kept their capitals ({ Id, Name }), which left the Owner, Backup and
/// "Give to someone in Legal" drop-downs blank (found recording the help videos).
/// </summary>
public sealed class PickListTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetAsync(HttpClient client, string url) =>
        JsonDocument.Parse(await (await client.GetAsync(url, Ct)).Content.ReadAsStringAsync(Ct)).RootElement;

    private static void HasIdAndName(JsonElement item)
    {
        Assert.Equal(JsonValueKind.Number, item.GetProperty("id").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Owner_people_and_Legal_pick_lists_have_id_and_name()
    {
        var user = $"pl{Guid.NewGuid():N}"[..12];
        await app.AddPersonAsync(user, $"Lee Legal {user[^4..]}", new { contractOwner = true, legal = true, approver = false, groupHead = false, admin = false, auditor = false });
        using var client = app.ClientFor($@"TEST\{user}");

        HasIdAndName((await GetAsync(client, "/api/lists")).GetProperty("owners")[0]);
        HasIdAndName((await GetAsync(client, "/api/people/picker"))[0]);

        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var category = JsonDocument.Parse(await (await admin.PostAsJsonAsync("/api/categories", new { name = $"Cat {user}", parentId = await app.FamilyAsync() }, Ct)).Content.ReadAsStringAsync(Ct)).RootElement;
        var created = JsonDocument.Parse(await (await client.PostAsJsonAsync("/api/contracts", new { title = $"Pick list {user}", categoryId = category.GetProperty("id").GetInt32() }, Ct))
            .Content.ReadAsStringAsync(Ct)).RootElement;
        var detail = await GetAsync(client, $"/api/contracts/{created.GetProperty("id").GetInt32()}");
        HasIdAndName(detail.GetProperty("legalPeople")[0]);
    }
}
