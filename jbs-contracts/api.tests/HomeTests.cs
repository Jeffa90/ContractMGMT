using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace JbsContracts.Api.Tests;

/// <summary>Slice 5: Home tiles, Urgent, "Choose what I see" and Ctrl+K search (product-spec section 12).</summary>
public sealed class HomeTests(AppFixture app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private static object Roles(bool owner = false, bool approver = false, bool legal = false, bool admin = false) =>
        new { contractOwner = owner, legal, approver, groupHead = false, admin, auditor = false };

    private async Task<(HttpClient Client, int Id)> PersonAsync(string name, object roles)
    {
        var user = Unique("hm");
        var person = await app.AddPersonAsync(user, $"{name} {user[^4..]}", roles);
        return (app.ClientFor($@"TEST\{user}"), person.GetProperty("id").GetInt32());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task ActAsync(HttpClient client, int id, string action, object? extra = null)
    {
        var body = JsonSerializer.SerializeToNode(extra ?? new { })!.AsObject();
        body["rowVersion"] = (await ReadAsync(await client.GetAsync($"/api/contracts/{id}", Ct))).GetProperty("contract").GetProperty("rowVersion").GetString();
        var response = await client.PostAsJsonAsync($"/api/contracts/{id}/{action}", body, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{action}: {await response.Content.ReadAsStringAsync(Ct)}");
    }

    private static readonly DateOnly Today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")));
    private static string D(int days) => Today.AddDays(days).ToString("yyyy-MM-dd");

    private async Task<int> CreateAsync(HttpClient owner, string title, object extra)
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var category = await ReadAsync(await admin.PostAsJsonAsync("/api/categories", new { name = Unique("Cat") }, Ct));
        var supplier = await ReadAsync(await owner.PostAsJsonAsync("/api/suppliers", new { name = Unique("Supplier ") }, Ct));
        var body = JsonSerializer.SerializeToNode(extra)!.AsObject();
        body["title"] = title;
        body["supplierId"] = supplier.GetProperty("id").GetInt32();
        body["categoryId"] = category.GetProperty("id").GetInt32();
        var created = await owner.PostAsJsonAsync("/api/contracts", body, Ct);
        return (await ReadAsync(created)).GetProperty("id").GetInt32();
    }

    /// <summary>Takes a contract that skips Legal through signing and approval to Effective.</summary>
    private static async Task MakeEffectiveAsync(HttpClient owner, HttpClient approver, int id)
    {
        await ActAsync(owner, id, "ready-for-signing");
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("%PDF signed"u8.ToArray()), "file", "signed.pdf");
        form.Add(new StringContent("SignedCopy"), "kind");
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsync($"/api/contracts/{id}/documents", form, Ct)).StatusCode);
        await ActAsync(owner, id, "send-for-approval", new { effectiveDate = D(-30) });
        await ActAsync(approver, id, "approval/approve");
    }

    private static int Count(JsonElement home, string key) =>
        home.GetProperty("tiles").EnumerateArray().FirstOrDefault(t => t.GetProperty("key").GetString() == key) is { ValueKind: JsonValueKind.Object } t ? t.GetProperty("count").GetInt32() : -1;

    private static async Task<int[]> TileListAsync(HttpClient client, string tile) =>
        (await ReadAsync(await client.GetAsync($"/api/contracts?view=all&tile={tile}", Ct))).EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ToArray();

    [Fact]
    public async Task Each_person_sees_their_own_tiles_and_urgent_dates()
    {
        var (owner, _) = await PersonAsync("Holly Home", Roles(owner: true));
        var (approver, _) = await PersonAsync("Arlo Approver", Roles(approver: true));
        var (viewer, _) = await PersonAsync("Vera Viewer", AppFixture.NoRoles);

        var draft = await CreateAsync(owner, "Draft one", new { });
        var soon = await CreateAsync(owner, "Notice soon", new { needsLegalReview = false, endDate = D(40), noticePeriodDays = 20 });     // notice in 20 days
        var urgent = await CreateAsync(owner, "Notice this week", new { needsLegalReview = false, endDate = D(10), noticePeriodDays = 5 }); // notice in 5 days
        var passed = await CreateAsync(owner, "Notice passed", new { needsLegalReview = false, endDate = D(60), noticePeriodDays = 63 });  // notice 3 days ago
        foreach (var id in new[] { soon, urgent, passed }) await MakeEffectiveAsync(owner, approver, id);
        var waiting = await CreateAsync(owner, "Waiting approval", new { needsLegalReview = false, endDate = D(400) });
        await ActAsync(owner, waiting, "ready-for-signing");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent("%PDF"u8.ToArray()), "file", "signed.pdf");
            form.Add(new StringContent("SignedCopy"), "kind");
            await owner.PostAsync($"/api/contracts/{waiting}/documents", form, Ct);
        }
        await ActAsync(owner, waiting, "send-for-approval", new { effectiveDate = D(0) });

        var home = await ReadAsync(await owner.GetAsync("/api/home", Ct));
        Assert.Equal(1, Count(home, "drafts"));
        Assert.Equal(2, Count(home, "notice-30"));  // 20 days and 5 days (not the one that passed)
        Assert.Equal(3, Count(home, "ending-90"));  // 40, 10 and 60 days
        Assert.Equal(-1, Count(home, "to-approve")); // not an approver: no such tile
        Assert.Equal([draft], await TileListAsync(owner, "drafts"));
        Assert.Equal(new[] { soon, urgent }.Order(), (await TileListAsync(owner, "notice-30")).Order());

        // Urgent: the passed deadline first, then this week's; the one in 20 days isn't urgent yet.
        var urgentItems = home.GetProperty("urgent").EnumerateArray().ToList();
        Assert.Equal([passed, urgent], urgentItems.Select(u => u.GetProperty("id").GetInt32()).ToArray());
        Assert.Equal((-3, 5), (urgentItems[0].GetProperty("days").GetInt32(), urgentItems[1].GetProperty("days").GetInt32()));

        // The approver's tile includes this contract; people with no roles have no tiles; a tile someone doesn't have filters nothing.
        Assert.Contains(waiting, await TileListAsync(approver, "to-approve"));
        Assert.True(Count(await ReadAsync(await approver.GetAsync("/api/home", Ct)), "to-approve") >= 1);
        Assert.Empty((await ReadAsync(await viewer.GetAsync("/api/home", Ct))).GetProperty("tiles").EnumerateArray());
        Assert.Contains(draft, await TileListAsync(viewer, "drafts"));

        // Choose what I see: kept per person; unknown tiles are ignored.
        var saved = await owner.PutAsJsonAsync("/api/home/preferences", new { hiddenTiles = new[] { "drafts", "nonsense" }, firstGroup = "coming" }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var prefs = (await ReadAsync(await owner.GetAsync("/api/home", Ct))).GetProperty("preferences");
        Assert.Equal(["drafts"], prefs.GetProperty("hiddenTiles").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.Equal("coming", prefs.GetProperty("firstGroup").GetString());
        Assert.Equal("turn", (await ReadAsync(await approver.GetAsync("/api/home", Ct))).GetProperty("preferences").GetProperty("firstGroup").GetString());
    }

    [Fact]
    public async Task Search_finds_contracts_and_suppliers_but_never_hidden_Confidential_contracts()
    {
        var (owner, _) = await PersonAsync("Sid Search", Roles(owner: true));
        var (other, _) = await PersonAsync("Olly Other", Roles(owner: true));
        var word = Unique("Zebra");
        var open = await CreateAsync(owner, $"{word} freight", new { });
        var secret = await CreateAsync(owner, $"{word} executive", new { isConfidential = true });
        await owner.PostAsJsonAsync("/api/suppliers", new { name = $"{word} Logistics" }, Ct);

        var mine = await ReadAsync(await owner.GetAsync($"/api/search?q={word}", Ct));
        Assert.Equal(new[] { open, secret }.Order(), mine.GetProperty("contracts").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).Order());
        Assert.Contains(mine.GetProperty("suppliers").EnumerateArray(), s => s.GetProperty("name").GetString() == $"{word} Logistics");
        Assert.Empty(mine.GetProperty("people").EnumerateArray()); // only admins search people

        var theirs = await ReadAsync(await other.GetAsync($"/api/search?q={word}", Ct));
        Assert.Equal([open], theirs.GetProperty("contracts").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ToArray());

        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var people = await ReadAsync(await admin.GetAsync("/api/search?q=Sid Search", Ct));
        Assert.Single(people.GetProperty("people").EnumerateArray());
        Assert.Empty((await ReadAsync(await owner.GetAsync("/api/search?q=", Ct))).GetProperty("contracts").EnumerateArray());
    }
}
