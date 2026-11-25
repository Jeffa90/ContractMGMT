using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

[assembly: AssemblyFixture(typeof(JbsContracts.Api.Tests.AppFixture))]

namespace JbsContracts.Api.Tests;

/// <summary>
/// Runs the real app against a real SQL Server: a fresh database per test run, created from database/*.sql and dropped afterwards.
/// JBS_TEST_SQL must be a connection string to a SQL Server where the test login can create databases. Without it the tests fail
/// (they are never silently skipped or reported as passed).
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public const string AdminAccount = @"TEST\admin";
    public const string AdminName = "Alex Admin";

    private readonly string serverConnection;
    private readonly string databaseName = $"JbsContractsTest_{Guid.NewGuid():N}";
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"jbs-contracts-test-{Guid.NewGuid():N}");
    public string ConnectionString { get; }
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public AppFixture()
    {
        serverConnection = Environment.GetEnvironmentVariable("JBS_TEST_SQL")
            ?? throw new InvalidOperationException("Set JBS_TEST_SQL to a SQL Server connection string (a login that can create databases) to run the API tests.");
        ConnectionString = new SqlConnectionStringBuilder(serverConnection) { InitialCatalog = databaseName }.ConnectionString;
    }

    public async ValueTask InitializeAsync()
    {
        await using (var master = new SqlConnection(serverConnection))
        {
            await master.OpenAsync();
            await master.ExecuteAsync($"CREATE DATABASE [{databaseName}]");
        }
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("Database:ConnectionString", ConnectionString);
            b.UseSetting("Contracts:AuthMode", "Header");
            b.UseSetting("Contracts:ApplyDatabaseScripts", "true");
            b.UseSetting("Contracts:DataDirectory", DataDirectory);
            b.UseSetting("Contracts:SiteUrl", "https://contracts.example.com");
            b.UseSetting("Contracts:Email:PollSeconds", "3600"); // tests send emails themselves (EmailDispatcher.SendDueAsync)
            // The old app's variable names, as on the GitHub QA environment (decision D-36).
            b.UseSetting("Ecp:Security:FailsafeAdmins:0:Account", AdminAccount);
            b.UseSetting("Ecp:Security:FailsafeAdmins:0:DisplayName", AdminName);
            b.UseSetting("Ecp:Security:FailsafeAdmins:0:Email", "alex.admin@example.com");
        });
        _ = Factory.Server; // start the app: applies the scripts and adds the first admin
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(serverConnection);
        await master.OpenAsync();
        await master.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];");
        if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
    }

    /// <summary>A browser-like client signed in as the given Windows account, sending the request-forgery header on changes.</summary>
    public HttpClient ClientFor(string account, bool withForgeryHeader = true)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add(Auth.HeaderName, account);
        if (withForgeryHeader) client.DefaultRequestHeaders.Add(WebSecurity.RequestHeader, "1");
        return client;
    }

    public async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Adds a person through the API as the first admin and returns them.</summary>
    public async Task<JsonElement> AddPersonAsync(string account, string name, object roles)
    {
        using var admin = ClientFor(AdminAccount);
        var response = await admin.PostAsJsonAsync("/api/people", new { account, name, email = $"{name.Replace(' ', '.').ToLowerInvariant()}@example.com", roles });
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Adding {account} failed: {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    public static object NoRoles => new { contractOwner = false, legal = false, approver = false, groupHead = false, admin = false, auditor = false };
}
