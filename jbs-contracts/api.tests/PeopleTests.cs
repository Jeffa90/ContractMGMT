using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Tests;

public sealed class PeopleTests(AppFixture app)
{
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Health_is_live_when_the_database_is_reachable()
    {
        using var anonymous = app.Factory.CreateClient();
        var response = await anonymous.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task First_admin_from_the_old_variable_names_can_sign_in()
    {
        using var client = app.ClientFor(AppFixture.AdminAccount.ToLowerInvariant()); // matching ignores capitals
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me", TestContext.Current.CancellationToken);
        Assert.Equal(AppFixture.AdminName, me.GetProperty("person").GetProperty("name").GetString());
        Assert.True(me.GetProperty("person").GetProperty("roles").GetProperty("admin").GetBoolean());
        Assert.Equal("TEST", me.GetProperty("defaultDomain").GetString());
    }

    [Fact]
    public async Task Someone_not_added_is_told_to_ask_an_admin_and_sees_nothing_else()
    {
        using var stranger = app.ClientFor(@"TEST\stranger");
        var response = await stranger.GetAsync("/api/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("no-access", body.GetProperty("code").GetString());
        Assert.Contains(AppFixture.AdminName, body.GetProperty("extra").GetProperty("admins").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/people", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Admin_adds_a_person_by_username_and_the_domain_is_added()
    {
        var user = Unique("jsmith");
        var person = await app.AddPersonAsync(user, "Jane Smith", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        Assert.Equal($@"TEST\{user}", person.GetProperty("account").GetString());

        using var jane = app.ClientFor($@"test\{user.ToUpperInvariant()}");
        var me = await jane.GetFromJsonAsync<JsonElement>("/api/me", TestContext.Current.CancellationToken);
        Assert.True(me.GetProperty("person").GetProperty("roles").GetProperty("contractOwner").GetBoolean());

        await using var db = await app.OpenAsync();
        var summary = await db.QuerySingleAsync<string>("SELECT TOP 1 Summary FROM contracts.AuditEvent WHERE Action = 'person.added' AND SubjectId = @id ORDER BY Id DESC",
            new { id = person.GetProperty("id").GetInt32() });
        Assert.Equal($@"{AppFixture.AdminName} added Jane Smith (TEST\{user}) as Contract owner.", summary);
        Assert.Null(await AuditLog.VerifyAsync(db));
    }

    [Fact]
    public async Task The_same_logon_cannot_be_added_twice_whatever_the_capitals()
    {
        var user = Unique("dup");
        await app.AddPersonAsync($@"TEST\{user}", "Dee Up", AppFixture.NoRoles);
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var response = await admin.PostAsJsonAsync("/api/people", new { account = $@"test\{user.ToUpperInvariant()}", name = "Dee Again", email = "dee@example.com", roles = AppFixture.NoRoles },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Contains("Dee Up", body.GetProperty("fields").GetProperty("account").GetString());
    }

    [Fact]
    public async Task Bad_details_get_plain_messages_for_each_field()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var response = await admin.PostAsJsonAsync("/api/people", new { account = "bad name with spaces", name = "", email = "not-an-email", roles = AppFixture.NoRoles },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("fields");
        Assert.True(fields.TryGetProperty("account", out _));
        Assert.Equal("Add their name.", fields.GetProperty("name").GetString());
        Assert.True(fields.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Only_admins_can_see_and_change_people()
    {
        var user = Unique("owner");
        await app.AddPersonAsync(user, "Owen Owner", new { contractOwner = true, legal = true, approver = true, groupHead = false, admin = false, auditor = true });
        using var owner = app.ClientFor($@"TEST\{user}");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/people", TestContext.Current.CancellationToken)).StatusCode);
        var add = await owner.PostAsJsonAsync("/api/people", new { account = "someone", name = "Some One", email = "some.one@example.com", roles = AppFixture.NoRoles },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
    }

    [Fact]
    public async Task Changes_without_the_forgery_header_or_from_another_site_are_blocked()
    {
        using var noHeader = app.ClientFor(AppFixture.AdminAccount, withForgeryHeader: false);
        var response = await noHeader.PostAsJsonAsync("/api/people", new { account = "x1", name = "X", email = "x@example.com", roles = AppFixture.NoRoles },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("blocked", (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("code").GetString());

        using var crossSite = app.ClientFor(AppFixture.AdminAccount);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/people")
        {
            Content = JsonContent.Create(new { account = "x2", name = "X", email = "x@example.com", roles = AppFixture.NoRoles }),
        };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await crossSite.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_remove_their_own_admin_access()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var me = (await admin.GetFromJsonAsync<JsonElement>("/api/me", TestContext.Current.CancellationToken)).GetProperty("person");
        var response = await admin.PutAsJsonAsync($"/api/people/{me.GetProperty("id").GetInt32()}", new
        {
            account = me.GetProperty("account").GetString(), name = me.GetProperty("name").GetString(), email = me.GetProperty("email").GetString(),
            roles = AppFixture.NoRoles, isActive = true, rowVersion = me.GetProperty("rowVersion").GetString(),
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Switching_someone_off_removes_their_access_and_is_audited()
    {
        var user = Unique("leaver");
        var person = await app.AddPersonAsync(user, "Lee Leaver", new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false });
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var response = await admin.PutAsJsonAsync($"/api/people/{person.GetProperty("id").GetInt32()}", new
        {
            account = user, name = "Lee Leaver", email = "lee@example.com", roles = new { contractOwner = true, legal = false, approver = false, groupHead = false, admin = false, auditor = false },
            isActive = false, rowVersion = person.GetProperty("rowVersion").GetString(),
        }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var lee = app.ClientFor($@"TEST\{user}");
        Assert.Equal(HttpStatusCode.Forbidden, (await lee.GetAsync("/api/me", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_stale_edit_is_refused_instead_of_overwriting()
    {
        var user = Unique("stale");
        var person = await app.AddPersonAsync(user, "Stan Stale", AppFixture.NoRoles);
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        object Edit(string name) => new { account = user, name, email = "stan@example.com", roles = AppFixture.NoRoles, isActive = true, rowVersion = person.GetProperty("rowVersion").GetString() };
        var id = person.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/people/{id}", Edit("Stan First"), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/people/{id}", Edit("Stan Second"), TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task There_is_only_one_Group_Head_and_the_previous_one_stays_a_backup_approver()
    {
        var first = await app.AddPersonAsync(Unique("gh1"), "Gina Head", new { contractOwner = false, legal = false, approver = true, groupHead = true, admin = false, auditor = false });
        var second = await app.AddPersonAsync(Unique("gh2"), "Hal Head", new { contractOwner = false, legal = false, approver = false, groupHead = true, admin = false, auditor = false });
        await using var db = await app.OpenAsync();
        var heads = (await db.QueryAsync<int>("SELECT Id FROM contracts.Person WHERE IsGroupHead = 1")).ToList();
        Assert.Equal([second.GetProperty("id").GetInt32()], heads);
        Assert.True(await db.QuerySingleAsync<bool>("SELECT IsApprover FROM contracts.Person WHERE Id = @id", new { id = first.GetProperty("id").GetInt32() }));
        Assert.True(second.GetProperty("roles").GetProperty("approver").GetBoolean()); // Group Head implies Approver
    }

    [Fact]
    public async Task Audit_events_cannot_be_changed_or_deleted()
    {
        await app.AddPersonAsync(Unique("aud"), "Audrey Audit", AppFixture.NoRoles);
        await using var db = await app.OpenAsync();
        var error = await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync("UPDATE contracts.AuditEvent SET Summary = 'edited'"));
        Assert.Contains("cannot be changed", error.Message);
        await Assert.ThrowsAsync<SqlException>(() => db.ExecuteAsync("DELETE FROM contracts.AuditEvent"));
    }

    [Fact]
    public async Task Unknown_api_paths_return_a_plain_not_found()
    {
        using var admin = app.ClientFor(AppFixture.AdminAccount);
        var response = await admin.GetAsync("/api/nothing-here", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public sealed class WindowsAccountTests
{
    [Theory]
    [InlineData("jsmith", "AMH", @"AMH\jsmith")]
    [InlineData(@"amh\jsmith", "AMH", @"AMH\jsmith")]
    [InlineData(@"  OTHER\j.smith-2 ", "AMH", @"OTHER\j.smith-2")]
    public void Normalises_logons(string input, string domain, string expected)
    {
        Assert.True(WindowsAccount.TryNormalise(input, domain, out var account));
        Assert.Equal(expected, account);
    }

    [Theory]
    [InlineData("", "AMH")]
    [InlineData("jsmith", null)]
    [InlineData("jane smith", "AMH")]
    [InlineData("jsmith@jbssa.com.au", "AMH")]
    [InlineData(@"AMH\", "AMH")]
    public void Refuses_what_is_not_a_logon(string input, string? domain) => Assert.False(WindowsAccount.TryNormalise(input, domain, out _));
}
