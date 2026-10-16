using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecp.Host;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Secrets;
using Ecp.Tenancy;
using Ecp.Tests.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Api.Tests;

/// <summary>
/// Hosts the real application. Settings are passed as environment variables because the host reads configuration at start-up;
/// API test classes share one collection so only one factory (and one set of variables) is active at a time.
/// </summary>
public sealed class EcpFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _previous = [];

    public EcpFactory(Dictionary<string, string?> settings)
    {
        foreach (var (key, value) in settings)
        {
            var name = key.Replace(":", "__");
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public static Dictionary<string, string?> DemoSqlite(string? dataDir = null) => new()
    {
        ["Ecp:Mode"] = "Demo",
        ["Ecp:DemoTenantResolution"] = "true",
        ["Ecp:DemoSeed"] = "true",
        ["Ecp:DataDirectory"] = dataDir ?? Path.Combine(Path.GetTempPath(), "ecp-api-" + Guid.NewGuid().ToString("N")),
        ["Database:Provider"] = "Sqlite",
        ["Database:ConnectionString"] = $"Data Source=file:ecp_api_{Guid.NewGuid():N}?mode=memory&cache=shared",
        ["Database:ConnectionStringRef"] = "",
        ["Database:AutoMigrate"] = "true",
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var (name, value) in _previous) Environment.SetEnvironmentVariable(name, value);
    }
}

[CollectionDefinition("Api", DisableParallelization = true)]
public sealed class ApiCollection;

[Collection("Api")]
[Trait("Category", "Api")]
public class DemoHostTests
{
    // An in-memory shared-cache SQLite database lives only while one connection stays open; keep one for the life of the factory.
    private static (EcpFactory Factory, Microsoft.Data.Sqlite.SqliteConnection Keeper) Start()
    {
        var settings = EcpFactory.DemoSqlite();
        var keeper = new Microsoft.Data.Sqlite.SqliteConnection(settings["Database:ConnectionString"]!);
        keeper.Open();
        return (new EcpFactory(settings), keeper);
    }

    [Fact]
    public async Task The_demo_starts_without_any_production_credentials_and_reports_ready()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/startup")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var doc = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains(doc.RootElement.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "R-DB-01" && c.GetProperty("status").GetString() == "Healthy");

        var info = JsonDocument.Parse(await client.GetStringAsync("/api/v1/info")).RootElement;
        Assert.Equal("Enterprise Contract Platform", info.GetProperty("name").GetString());
        Assert.Equal("Demo", info.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task The_demo_tenant_is_seeded_idempotently_and_resolvable()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient(); // starts the host: migrations and seeding run
        await using var scope = factory.Services.CreateAsyncScope();
        var info = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByNameAsync("Demo Tenant");
        Assert.NotNull(info);
        Assert.NotNull(info!.FirstOrganisationId);
        // A second seeding pass must not create another tenant.
        await scope.ServiceProvider.GetRequiredService<DemoSeeder>().SeedAsync();
        Assert.Equal(info.Id, (await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByNameAsync("Demo Tenant"))!.Id);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health/live")]
    [InlineData("/api/v1/info")]
    public async Task Responses_carry_the_security_headers_and_a_strict_content_security_policy(string path)
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();
        var response = await client.GetAsync(path);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        if (path != "/") Assert.Contains("no-store", response.Headers.GetValues("Cache-Control").Single());
    }

    [Fact]
    public async Task A_valid_correlation_id_is_echoed_and_a_malformed_one_is_replaced()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();
        var ok = new HttpRequestMessage(HttpMethod.Get, "/health/live"); ok.Headers.Add("X-Correlation-Id", "req-123.abc");
        Assert.Equal("req-123.abc", (await client.SendAsync(ok)).Headers.GetValues("X-Correlation-Id").Single());
        var bad = new HttpRequestMessage(HttpMethod.Get, "/health/live"); bad.Headers.Add("X-Correlation-Id", "bad id with spaces\"<script>");
        var echoed = (await client.SendAsync(bad)).Headers.GetValues("X-Correlation-Id").Single();
        Assert.Matches("^[0-9a-f]{32}$", echoed);
    }

    [Fact]
    public async Task Unknown_routes_return_generic_problem_details_without_internals()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain(" at ", body);
    }

    [Fact] // ARC-008
    public async Task Every_endpoint_is_either_protected_or_on_the_explicit_anonymous_allowlist()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();
        string[] anonymousAllowlist = ["/", "/health/live", "/health/startup", "/health/ready", "/api/v1/info", "/api/v1/openapi.json"];
        var endpoints = factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);
        foreach (var endpoint in endpoints)
        {
            var route = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            var protectedEndpoint = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null;
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            Assert.True(protectedEndpoint || (anonymous && anonymousAllowlist.Contains(route)), $"Endpoint {route} has no authorisation requirement and is not on the anonymous allowlist");
        }
    }

    [Fact]
    public async Task OpenApi_is_published_in_demo_only()
    {
        var (factory, keeper) = Start();
        using var _ = factory; using var __ = keeper;
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("openapi", await response.Content.ReadAsStringAsync());
    }
}

[Collection("Api")]
[Trait("Category", "Api")]
public class ProductionRefusalTests
{
    private static async Task<string> StartupFailureAsync(Dictionary<string, string?> settings)
    {
        using var factory = new EcpFactory(settings);
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => { using var c = factory.CreateClient(); await Task.CompletedTask; });
        for (var e = ex; e is not null; e = e.InnerException) if (e is StartupRefusedException r) return r.Message;
        throw new Xunit.Sdk.XunitException("Start-up failed for a different reason: " + ex);
    }

    [Fact]
    public async Task Production_with_demonstration_settings_refuses_to_start_and_names_each_problem()
    {
        var settings = EcpFactory.DemoSqlite();
        settings["Ecp:Mode"] = "Production";
        var message = await StartupFailureAsync(settings);
        Assert.Contains("[R-ID-01]", message);   // demonstration tenant resolution
        Assert.Contains("[R-DB-01]", message);   // lightweight provider
        Assert.Contains("[R-SEC-02]", message);  // inline connection string
        Assert.Contains("[R-DEMO-01]", message); // demonstration data
    }

    [Fact]
    public async Task An_unrecognised_mode_fails_safe_to_production_rules()
    {
        var settings = EcpFactory.DemoSqlite();
        settings["Ecp:Mode"] = "Prodution"; // typo
        Assert.Contains("[R-DB-01]", await StartupFailureAsync(settings));
    }
}

public class HostConfigurationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static readonly ISecretProvider Secrets = new EnvironmentAndFileSecretProvider();

    [Fact]
    public void Demo_defaults_resolve_to_a_local_sqlite_file_under_the_content_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecp-cfg-" + Guid.NewGuid().ToString("N"));
        var resolved = HostConfiguration.Resolve(Config(new()
        {
            ["Ecp:Mode"] = "Demo", ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=data/ecp-demo.db", ["Ecp:DataDirectory"] = "data",
        }), Secrets, root);
        Assert.Equal(EcpMode.Demo, resolved.Host.Mode);
        Assert.StartsWith(root, resolved.Database.ConnectionString.Replace("Data Source=", ""));
        Assert.True(Directory.Exists(Path.Combine(root, "data")));
        Directory.Delete(root, true);
    }

    [Fact]
    public void A_compliant_production_configuration_resolves_the_connection_from_a_secret_reference()
    {
        var name = "ECP_CFG_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "Server=db.example;Database=ecp;User Id=app;Password=not-a-real-secret");
        try
        {
            var resolved = HostConfiguration.Resolve(Config(new()
            {
                ["Ecp:Mode"] = "Production", ["Ecp:DemoTenantResolution"] = "false", ["Ecp:DemoSeed"] = "false", ["Ecp:DataDirectory"] = "/var/lib/ecp",
                ["Database:Provider"] = "SqlServer", ["Database:ConnectionStringRef"] = "env:" + name,
            }), Secrets, "/app");
            Assert.Equal(DatabaseProvider.SqlServer, resolved.Database.Provider);
            Assert.True(resolved.Facts.ConnectionFromSecretReference);
            Assert.DoesNotContain("not-a-real-secret", Redactor.MaskConnectionString(resolved.Database.ConnectionString));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    [Fact]
    public void Production_with_an_unresolvable_secret_reference_is_refused()
    {
        var ex = Assert.Throws<StartupRefusedException>(() => HostConfiguration.Resolve(Config(new()
        {
            ["Ecp:Mode"] = "Production", ["Ecp:DataDirectory"] = "/var/lib/ecp", ["Database:Provider"] = "SqlServer", ["Database:ConnectionStringRef"] = "env:ECP_DOES_NOT_EXIST_" + Guid.NewGuid().ToString("N"),
        }), Secrets, "/app"));
        Assert.Contains("[R-SEC-02]", ex.Message);
    }

    [Fact]
    public void Startup_refusal_messages_never_contain_secret_values()
    {
        var ex = Assert.Throws<StartupRefusedException>(() => HostConfiguration.Resolve(Config(new()
        {
            ["Ecp:Mode"] = "Production", ["Ecp:DataDirectory"] = "/var/lib/ecp", ["Database:Provider"] = "SqlServer",
            ["Database:ConnectionString"] = "Server=x;Password=SuperSecretValue123",
        }), Secrets, "/app"));
        Assert.DoesNotContain("SuperSecretValue123", ex.Message);
    }
}
