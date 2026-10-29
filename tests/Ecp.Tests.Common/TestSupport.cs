using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Identity;
using Ecp.Contracts;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.DataMigration;
using Ecp.Lifecycle;
using Ecp.Notifications;
using Ecp.Operations;
using Ecp.Persistence;
using Ecp.Access;
using Ecp.Setup;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Ecp.Tenancy.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Tests.Common;

/// <summary>Test stand-in for the host's Data Protection based protector: reversible, not secret. Never used outside tests.</summary>
public sealed class PlainSetupProtector : ISetupProtector
{
    public string Protect(string plain) => "test:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
    public string Unprotect(string protectedText) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText["test:".Length..]));
}

public sealed class FakeClock(DateTime? start = null) : IClock
{
    public DateTime UtcNow { get; private set; } = start ?? new DateTime(2026, 10, 16, 1, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan by) => UtcNow += by;
    public DateTime Next() { UtcNow = UtcNow.AddSeconds(1); return UtcNow; }
}

/// <summary>Whether a real SQL Server is available for the SQL Server suites (decision D4). Provided through <c>ECP_TEST_SQL</c>.</summary>
public static class SqlServerEnvironment
{
    public const string Variable = "ECP_TEST_SQL";
    public static string? ConnectionString => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } v ? v : null;
    public static bool IsAvailable => ConnectionString is not null;
    public const string NotVerifiedReason = "NOT VERIFIED: ECP_TEST_SQL is not set, so no SQL Server connection is available for this test.";
}

/// <summary>A test that needs real SQL Server. Without a connection it is skipped and reported as NOT VERIFIED, never as passed.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute() { if (!SqlServerEnvironment.IsAvailable) Skip = SqlServerEnvironment.NotVerifiedReason; }
}

public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute() { if (!SqlServerEnvironment.IsAvailable) Skip = SqlServerEnvironment.NotVerifiedReason; }
}

/// <summary>A tenant scope inside a DI scope. Dispose to leave the scope.</summary>
public sealed class TenantScope(AsyncServiceScope scope, IDisposable token, Guid tenantId) : IAsyncDisposable
{
    public IServiceProvider Services => scope.ServiceProvider;
    public Guid TenantId { get; } = tenantId;
    public T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();
    public async ValueTask DisposeAsync() { token.Dispose(); await scope.DisposeAsync(); }
}

/// <summary>Composes the application services for tests on SQLite (provider-independent, not SQL Server proof) or on real SQL Server.</summary>
/// <summary>Pack identifiers the test hosts use.</summary>
public static class TestPacks
{
    public const string Reference = "jbs-procurement";
    public const string Blank = "blank";
}

public sealed class TestApp : IAsyncDisposable
{
    private readonly SqliteConnection? _keeper;
    private readonly string? _sqlServerDatabase;
    private readonly string? _adminConnection;
    private readonly string _documentRoot;

    private TestApp(ServiceProvider services, DatabaseProvider provider, string connectionString, SqliteConnection? keeper, string? sqlDb, string? admin, FakeClock clock, string root)
    {
        _documentRoot = root; Services = services; Provider = provider; ConnectionString = connectionString; _keeper = keeper; _sqlServerDatabase = sqlDb; _adminConnection = admin; Clock = clock;
    }

    public ServiceProvider Services { get; }
    public DatabaseProvider Provider { get; }
    public string ConnectionString { get; }
    public FakeClock Clock { get; }
    public string DocumentRoot => _documentRoot;

    public static async Task<TestApp> CreateSqliteAsync(EcpMode setupMode = EcpMode.Test, Action<IServiceCollection>? configure = null)
    {
        var cs = $"Data Source=file:ecp_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var keeper = new SqliteConnection(cs); keeper.Open(); // keeps the shared in-memory database alive
        var app = Build(DatabaseProvider.Sqlite, cs, keeper, null, null, setupMode, configure);
        await app.Services.GetRequiredService<MigrationRunner>().RunAsync();
        return app;
    }

    public static async Task<TestApp> CreateSqlServerAsync(bool migrate = true, EcpMode setupMode = EcpMode.Test, Action<IServiceCollection>? configure = null)
    {
        var admin = SqlServerEnvironment.ConnectionString ?? throw new InvalidOperationException(SqlServerEnvironment.NotVerifiedReason);
        var db = "ecp_test_" + Guid.NewGuid().ToString("N");
        await using (var c = new SqlConnection(admin))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{db}]";
            await cmd.ExecuteNonQueryAsync();
        }
        var cs = new SqlConnectionStringBuilder(admin) { InitialCatalog = db, ConnectTimeout = 15 }.ConnectionString;
        var app = Build(DatabaseProvider.SqlServer, cs, null, db, admin, setupMode, configure);
        if (migrate) await app.Services.GetRequiredService<MigrationRunner>().RunAsync();
        return app;
    }

    /// <summary>An application pointed at an arbitrary SQL Server connection string, without migrating (for unreachable-database tests).</summary>
    public static TestApp ForConnection(string connectionString) => Build(DatabaseProvider.SqlServer, connectionString, null, null, null, EcpMode.Test);

    private static TestApp Build(DatabaseProvider provider, string cs, SqliteConnection? keeper, string? sqlDb, string? admin, EcpMode setupMode, Action<IServiceCollection>? configure = null)
    {
        var clock = new FakeClock();
        var root = Path.Combine(Path.GetTempPath(), "ecp-tests-" + Guid.NewGuid().ToString("N"));
        var sp = BuildServices(provider, cs, clock, root, setupMode, configure);
        return new TestApp(sp, provider, cs, keeper, sqlDb, admin, clock, root) { SetupMode = setupMode, Configure = configure };
    }

    /// <summary>Extra registrations applied after the standard composition (a test scanner, fault injection, module options).</summary>
    public Action<IServiceCollection>? Configure { get; private init; }

    public EcpMode SetupMode { get; private set; } = EcpMode.Test;
    private readonly List<ServiceProvider> _restarted = [];

    /// <summary>A second set of services over the same database, data directory and clock: what the application looks like after a process restart (no in-memory state survives).</summary>
    public ServiceProvider RestartServices()
    {
        var sp = BuildServices(Provider, ConnectionString, Clock, _documentRoot, SetupMode, Configure);
        _restarted.Add(sp);
        return sp;
    }

    /// <summary>
    /// Wraps an installation that someone else owns (a restored copy opened beside the application) so the test worlds can drive it through the same helpers. Disposing the wrapper does not drop a database or delete files:
    /// the owner of the copy does that.
    /// </summary>
    public static TestApp Attach(ServiceProvider services, string connectionString, FakeClock clock, string root)
        => new(services, DatabaseProvider.SqlServer, connectionString, null, null, null, clock, root) { SetupMode = EcpMode.Test };

    /// <summary>The standard composition over another database, data directory and clock, with no migration and no ownership of any database: a restored copy opened beside the application for validation.</summary>
    public static ServiceProvider BuildDetached(DatabaseProvider provider, string cs, IClock clock, string root, EcpMode setupMode, Action<IServiceCollection>? configure = null)
        => BuildServices(provider, cs, clock, root, setupMode, configure);

    private static ServiceProvider BuildServices(DatabaseProvider provider, string cs, IClock clock, string root, EcpMode setupMode, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClock>(clock);
        services.AddEcpPersistence(new DatabaseOptions { Provider = provider, ConnectionString = cs });
        services.AddAuditModule();
        services.AddTenancyModule();
        services.AddIdentityModule(o => o.Mode = setupMode);
        services.AddConfigurationModule(o => o.DefaultPackId = TestPacks.Reference, packs: o =>
        {
            // Shipped packs are external files, built from the pack sources. Production hosts see the copy signed by a throw-away key that only this process knows and trusts.
            var production = setupMode == EcpMode.Production;
            o.Mode = setupMode;
            o.ShippedDirectory = production ? TestShippedPacks.SignedDirectory : TestShippedPacks.UnsignedDirectory;
            o.StorageDirectory = Path.Combine(root, "packs");
            o.AllowUnsignedSyntheticPacks = !production;
            if (production) o.TrustedKeys.Add(new ConfiguredTrustKey { KeyId = TestShippedPacks.KeyId, PublicKeyPem = TestShippedPacks.PublicKeyPem, Label = "test process key" });
        });
        services.AddContractsModule(o => o.InitialStatusCode = "Draft");
        services.AddDocumentsModule(_ => { }, new LocalFileSystemOptions { RootPath = Path.Combine(root, "documents") });
        services.AddMigrationModule();
        // The test worlds create organisations without a solution pack and exercise the reference (JBS) lifecycle and notification policy, so the installation default names that pack here and only here.
        services.AddLifecycleModule(o => { o.Mode = setupMode; o.DefaultPackId = TestPacks.Reference; });
        services.AddNotificationsModule(o => { o.Mode = setupMode; o.DefaultPackId = TestPacks.Reference; });
        services.AddAccessModule();
        services.AddOperationsModule(root);
        services.AddSetupModule(new SetupEnvironment(setupMode, root, DemoIdentityEnabled: false, DemoSeedEnabled: false, ConnectionFromSecretReference: false,
            provider.ToString(), "test", AllowRemoteSetup: false));
        services.AddSingleton<ISetupProtector, PlainSetupProtector>();
        services.AddSingleton(new StartupFacts(setupMode, provider, false, false, false, false, root));
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    /// <summary>Creates a DI scope with the given tenant established.</summary>
    public TenantScope ScopeFor(Guid tenantId)
    {
        var scope = Services.CreateAsyncScope();
        var token = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
        return new TenantScope(scope, token, tenantId);
    }

    /// <summary>A DI scope with no tenant established (to prove data access fails closed).</summary>
    public AsyncServiceScope ScopeWithoutTenant() => Services.CreateAsyncScope();

    public async Task<(Guid TenantId, Guid OrganisationId)> ProvisionAsync(string name, string prefix, EcpMode mode = EcpMode.Demo)
    {
        await using var scope = Services.CreateAsyncScope();
        var provisioned = await scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>().ProvisionAsync(new ProvisionTenantRequest(
            name, TenancyMode.MultiOrganisation, mode, name + " Organisation", "Australia/Melbourne", "en-AU", "AUD", prefix));
        return (provisioned.TenantId, provisioned.OrganisationId);
    }

    /// <summary>Every (schema, table) mapped by any module context, taken from the EF models.</summary>
    public async Task<IReadOnlyList<(string Schema, string Table, string Module)>> EntityTablesAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var result = new List<(string, string, string)>();
        foreach (var module in Services.GetServices<ModuleDescriptor>())
        {
            var ctx = (Microsoft.EntityFrameworkCore.DbContext)scope.ServiceProvider.GetRequiredService(module.DbContextType);
            foreach (var et in ctx.Model.GetEntityTypes())
                if (et.GetTableName() is { } table) result.Add((et.GetSchema() ?? module.Schema, table, module.Name));
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var extra in _restarted) await extra.DisposeAsync();
        await Services.DisposeAsync();
        _keeper?.Dispose();
        if (_sqlServerDatabase is not null && _adminConnection is not null)
        {
            SqlConnection.ClearAllPools();
            await using var c = new SqlConnection(_adminConnection);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"ALTER DATABASE [{_sqlServerDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_sqlServerDatabase}];";
            await cmd.ExecuteNonQueryAsync();
        }
        if (Directory.Exists(_documentRoot)) Directory.Delete(_documentRoot, true);
    }
}

public static class RepoRoot
{
    /// <summary>Finds the repository root (the folder containing global.json) from the test output directory.</summary>
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found.");
    }
}
