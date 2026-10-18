using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecp.Persistence;

/// <summary>Describes a module's persistence for ordering and migration. Lower tiers migrate first.</summary>
public sealed record ModuleDescriptor(string Name, int Tier, Type DbContextType, string Schema);

public sealed class MigrationStatus
{
    public List<string> Applied { get; } = [];
    public List<string> Pending { get; } = [];
    public bool IsCurrent => Pending.Count == 0;
}

/// <summary>Applies module migrations in tier order. SQLite (Demo/Test only) creates tables from the model instead.</summary>
public sealed class MigrationRunner(IServiceProvider root, IEnumerable<ModuleDescriptor> modules, DatabaseOptions options, ILogger<MigrationRunner> logger)
{
    private const string SqliteMarkerTable = "__ecp_modules";

    /// <summary>The modules in the order they migrate: lowest tier first, then by name.</summary>
    public IReadOnlyList<ModuleDescriptor> OrderedModules => modules.OrderBy(m => m.Tier).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Migrates one module on SQL Server, up to <paramref name="targetMigration"/> or to the latest when it is null. Used by the upgrade procedure (one module at a time, so a failure names the module) and by the rehearsal fixture
    /// that builds an earlier schema. SQLite (Demo and Test only) has no migration history and is not supported here.
    /// </summary>
    public async Task MigrateModuleAsync(string module, string? targetMigration, CancellationToken ct = default)
    {
        if (options.Provider != DatabaseProvider.SqlServer) throw new InvalidOperationException("Module migration is only available on SQL Server.");
        var descriptor = modules.Single(m => m.Name == module);
        await using var scope = root.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        using var _ = tenant.UseInstallationScope();
        var context = (DbContext)scope.ServiceProvider.GetRequiredService(descriptor.DbContextType);
        await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(context).MigrateAsync(targetMigration, ct);
        logger.LogInformation("Migrations applied for module {Module}", module);
    }

    /// <summary>Migrates every module in order, each to the named migration (modules not named go to their latest).</summary>
    public async Task RunToAsync(IReadOnlyDictionary<string, string?> targets, CancellationToken ct = default)
    {
        foreach (var module in OrderedModules) await MigrateModuleAsync(module.Name, targets.TryGetValue(module.Name, out var t) ? t : null, ct);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var scope = root.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        using var _ = tenant.UseInstallationScope();
        foreach (var module in modules.OrderBy(m => m.Tier).ThenBy(m => m.Name))
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(module.DbContextType);
            if (options.Provider == DatabaseProvider.SqlServer)
            {
                await context.Database.MigrateAsync(ct);
                logger.LogInformation("Migrations applied for module {Module}", module.Name);
            }
            else
            {
                await InitialiseSqliteAsync(scope.ServiceProvider.GetRequiredService<IScopedConnection>(), context, module, ct);
            }
        }
    }

    public async Task<MigrationStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var status = new MigrationStatus();
        await using var scope = root.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        using var _ = tenant.UseInstallationScope();
        foreach (var module in modules.OrderBy(m => m.Tier).ThenBy(m => m.Name))
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(module.DbContextType);
            if (options.Provider == DatabaseProvider.SqlServer)
            {
                var pending = await context.Database.GetPendingMigrationsAsync(ct);
                (pending.Any() ? status.Pending : status.Applied).Add(module.Name);
            }
            else
            {
                var conn = scope.ServiceProvider.GetRequiredService<IScopedConnection>();
                await conn.EnsureOpenAsync(ct);
                (await SqliteModuleExistsAsync(conn, module.Name, ct) ? status.Applied : status.Pending).Add(module.Name);
            }
        }
        return status;
    }

    private static async Task<bool> SqliteModuleExistsAsync(IScopedConnection conn, string module, CancellationToken ct)
    {
        await using var cmd = conn.Connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{SqliteMarkerTable}'";
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 0) return false;
        cmd.CommandText = $"SELECT COUNT(*) FROM {SqliteMarkerTable} WHERE name=@n";
        var p = cmd.CreateParameter(); p.ParameterName = "@n"; p.Value = module; cmd.Parameters.Add(p);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task InitialiseSqliteAsync(IScopedConnection conn, DbContext context, ModuleDescriptor module, CancellationToken ct)
    {
        await conn.EnsureOpenAsync(ct);
        await using (var create = conn.Connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE IF NOT EXISTS {SqliteMarkerTable} (name TEXT PRIMARY KEY)";
            await create.ExecuteNonQueryAsync(ct);
        }
        if (await SqliteModuleExistsAsync(conn, module.Name, ct)) return;
        var script = context.Database.GenerateCreateScript();
        await using (var cmd = conn.Connection.CreateCommand())
        {
            cmd.CommandText = script;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var mark = conn.Connection.CreateCommand();
        mark.CommandText = $"INSERT INTO {SqliteMarkerTable}(name) VALUES (@n)";
        var p = mark.CreateParameter(); p.ParameterName = "@n"; p.Value = module.Name; mark.Parameters.Add(p);
        await mark.ExecuteNonQueryAsync(ct);
    }
}
