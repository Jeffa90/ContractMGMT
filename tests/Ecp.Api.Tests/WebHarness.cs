using System.Net;
using System.Text.RegularExpressions;
using Ecp.Host;
using Ecp.Identity;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Api.Tests;

/// <summary>Hosts the whole application in Demo mode on the lightweight provider with the synthetic demo data seeded, and drives it like a browser.</summary>
public sealed class DemoApp : IDisposable
{
    private readonly SqliteConnection? _keeper;
    private readonly Action? _drop;
    public EcpFactory Factory { get; }
    /// <summary>True when the host runs over real SQL Server (so the run is SQL Server evidence), false on the lightweight provider.</summary>
    public bool IsSqlServer { get; private init; }

    private DemoApp(EcpFactory factory, SqliteConnection? keeper, Action? drop = null) { Factory = factory; _keeper = keeper; _drop = drop; }

    /// <summary>
    /// The same Demo host over a new database on the real SQL Server named by <c>ECP_TEST_SQL</c> (created for the run and dropped afterwards). The pages, services and migrations are the same; what changes is the
    /// provider, so a run here is evidence that the pages work with real constraints, row-level security and concurrency. Without a server the caller's test is skipped as NOT VERIFIED.
    /// </summary>
    public static DemoApp StartSqlServer(Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null)
    {
        var admin = Ecp.Tests.Common.SqlServerEnvironment.ConnectionString ?? throw new InvalidOperationException(Ecp.Tests.Common.SqlServerEnvironment.NotVerifiedReason);
        var db = "ecp_api_" + Guid.NewGuid().ToString("N");
        using (var c = new Microsoft.Data.SqlClient.SqlConnection(admin))
        {
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = $"CREATE DATABASE [{db}]"; cmd.ExecuteNonQuery();
        }
        var settings = EcpFactory.DemoSqlite();
        settings["Database:Provider"] = "SqlServer";
        settings["Database:ConnectionString"] = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(admin) { InitialCatalog = db, ConnectTimeout = 15 }.ConnectionString;
        configure?.Invoke(settings);
        void Drop()
        {
            try
            {
                Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
                using var c = new Microsoft.Data.SqlClient.SqlConnection(admin); c.Open();
                using var cmd = c.CreateCommand(); cmd.CommandText = $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]"; cmd.ExecuteNonQuery();
            }
            catch (Exception) { /* a leftover test database is harmless and named for the run */ }
        }
        return new DemoApp(new EcpFactory(settings, services), null, Drop) { IsSqlServer = true };
    }

    public static DemoApp Start(Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null)
    {
        var settings = EcpFactory.DemoSqlite();
        configure?.Invoke(settings);
        var keeper = new SqliteConnection(settings["Database:ConnectionString"]!);
        keeper.Open(); // keeps the shared in-memory database alive for the life of the host
        return new DemoApp(new EcpFactory(settings, services), keeper);
    }

    public HttpClient NewClient(bool followRedirects = false)
        => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects, HandleCookies = true, BaseAddress = new Uri("https://localhost") });

    /// <summary>Finds a seeded demo user by email in any tenant (including the installation).</summary>
    public async Task<(Guid TenantId, Guid UserId)> FindUserAsync(string email)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<TenantContext>();
        var tenants = (await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync()).Select(t => t.Id).Append(SystemTenant.Id);
        foreach (var t in tenants)
        {
            using var _ = tenantContext.Use(t);
            var user = await scope.ServiceProvider.GetRequiredService<IUserService>().FindByEmailAsync(email);
            if (user is not null) return (t, user.Id);
        }
        throw new InvalidOperationException($"Demo user {email} was not found.");
    }

    /// <summary>Signs in through the real sign-in page (with antiforgery) as a seeded demo user.</summary>
    public async Task<HttpClient> SignInAsync(string email, bool followRedirects = false)
    {
        var client = NewClient(followRedirects);
        var (tenant, user) = await FindUserAsync(email);
        var token = await TokenAsync(client, "/SignIn");
        var response = await client.PostAsync("/SignIn", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["pick"] = $"{tenant}:{user}", ["returnUrl"] = "/", ["__RequestVerificationToken"] = token
        }));
        if (response.StatusCode != HttpStatusCode.Redirect && response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"Sign-in as {email} failed: {(int)response.StatusCode}");
        return client;
    }

    public async Task<T> WithTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
        return await action(scope.ServiceProvider);
    }

    /// <summary>Reads a page and returns its antiforgery request token (the matching cookie is stored by the client).</summary>
    public static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return ExtractToken(html) ?? throw new InvalidOperationException($"No antiforgery token on {url}.");
    }

    public static string? ExtractToken(string html)
    {
        var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Posts a form the way a browser does: fetch the page for its token, then submit it with the cookie.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, IEnumerable<KeyValuePair<string, string>> fields, string? postUrl = null)
    {
        var token = await TokenAsync(client, pageUrl);
        var all = fields.Append(new("__RequestVerificationToken", token));
        return await client.PostAsync(postUrl ?? pageUrl, new FormUrlEncodedContent(all));
    }

    public void Dispose() { Factory.Dispose(); _keeper?.Dispose(); _drop?.Invoke(); }
}
