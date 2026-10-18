using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Recovery;

public enum UpgradeStateKind
{
    /// <summary>No module has any migration applied: a new database, not an installation to upgrade.</summary>
    Empty,
    /// <summary>The applied migrations are exactly those of a released source version.</summary>
    AtSource,
    /// <summary>Everything this build knows is applied: nothing to upgrade.</summary>
    AtTarget,
    /// <summary>Applied migrations are a consistent prefix of this build's, but are not any released version: an upgrade that stopped part-way, or a hand-made state.</summary>
    Partial,
    /// <summary>The database holds migrations this build does not know: it was written by a newer version. Downgrading an application against it is not supported.</summary>
    Ahead,
    /// <summary>The applied migrations leave a gap or an order this build cannot explain.</summary>
    Inconsistent,
}

/// <summary>Where an installation's schema stands relative to the migrations compiled into this build and the frozen baselines of released versions.</summary>
public sealed record UpgradeState(UpgradeStateKind Kind, string? SourceVersion, IReadOnlyList<string> Pending, IReadOnlyList<string> Unknown, IReadOnlyList<string> Gaps, IReadOnlyList<string> PartialModules);

public static class UpgradeStateDetector
{
    /// <param name="applied">Applied migration identifiers by module, as the database's history tables report them.</param>
    public static UpgradeState Detect(IReadOnlyDictionary<string, IReadOnlyList<string>> applied, MigrationLedger compiled, UpgradeCatalogue catalogue, IEnumerable<string> supportedSources)
    {
        var pending = new List<string>(); var unknown = new List<string>(); var gaps = new List<string>();
        foreach (var module in compiled.Modules)
        {
            var have = applied.TryGetValue(module.Module, out var list) ? list.Order(StringComparer.Ordinal).ToList() : [];
            var known = module.Migrations.Select(m => m.Id).ToList();
            unknown.AddRange(have.Where(a => !known.Contains(a)).Select(a => $"{module.Module}/{a}"));
            if (have.Where(known.Contains).SequenceEqual(known.Take(have.Count(known.Contains)))) { /* a prefix: no gap */ }
            else gaps.Add(module.Module);
            pending.AddRange(known.Where(k => !have.Contains(k)).Select(k => $"{module.Module}/{k}"));
        }
        foreach (var extra in applied.Where(a => a.Value.Count > 0 && compiled.Find(a.Key) is null)) unknown.AddRange(extra.Value.Select(v => $"{extra.Key}/{v}"));
        if (applied.Values.All(v => v.Count == 0)) return new(UpgradeStateKind.Empty, null, pending, unknown, gaps, []);
        if (unknown.Count > 0) return new(UpgradeStateKind.Ahead, null, pending, unknown, gaps, []);
        if (gaps.Count > 0) return new(UpgradeStateKind.Inconsistent, null, pending, unknown, gaps, []);
        if (pending.Count == 0) return new(UpgradeStateKind.AtTarget, null, pending, unknown, gaps, []);
        foreach (var version in supportedSources)
        {
            var baseline = catalogue.Baseline(version);
            if (baseline is null) continue;
            var matches = compiled.Modules.All(m =>
            {
                var have = applied.TryGetValue(m.Module, out var list) ? list.Order(StringComparer.Ordinal).ToList() : [];
                var expected = baseline.Modules.FirstOrDefault(b => b.Module == m.Module)?.Migrations.Select(x => x.Id).ToList() ?? [];
                return have.SequenceEqual(expected);
            });
            if (matches) return new(UpgradeStateKind.AtSource, version, pending, unknown, gaps, []);
        }
        var partial = compiled.Modules.Where(m => (applied.TryGetValue(m.Module, out var list) ? list.Count : 0) is var n && n > 0 && n < m.Migrations.Count).Select(m => m.Module).ToList();
        return new(UpgradeStateKind.Partial, null, pending, unknown, gaps, partial);
    }
}

/// <summary>Helpers that run something once per tenant, in that tenant's scope (so row-level security applies), against a service provider that may be the running application or a restored copy.</summary>
internal static class UpgradeScopes
{
    public static async Task<List<Guid>> TenantsAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        var list = (await scope.ServiceProvider.GetRequiredService<Ecp.Tenancy.ITenantDirectory>().ListAsync(ct)).Where(x => x.Id != SystemTenant.Id).Select(x => x.Id).OrderBy(x => x).ToList();
        list.Add(SystemTenant.Id);
        return list;
    }

    public static async Task<Dictionary<string, IReadOnlyList<string>>> AppliedAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        var schema = await SchemaInspector.ReadAsync(scope.ServiceProvider, ct);
        return schema.Modules.ToDictionary(m => m.Module, m => (IReadOnlyList<string>)m.AppliedMigrations.ToList(), StringComparer.Ordinal);
    }
}

public sealed record DriftItem(string Module, string Object, string Problem);

/// <summary>
/// Compares the database with the schema its own migration history says it has: for each module, the model recorded by its last applied migration. Anything missing, extra or of a different type is drift: the database was
/// changed outside the platform's migrations (or a migration did not complete). Reads catalogue views only.
/// </summary>
internal static class SchemaDrift
{
    public static async Task<IReadOnlyList<DriftItem>?> CheckAsync(IServiceProvider scopeServices, IReadOnlyDictionary<string, IReadOnlyList<string>> applied, CancellationToken ct)
    {
        var connection = scopeServices.GetRequiredService<IScopedConnection>();
        if (connection.Provider != DatabaseProvider.SqlServer) return null;
        await connection.ApplyTenantScopeAsync(ct);
        var columns = new Dictionary<(string Schema, string Table), Dictionary<string, (string Type, bool Nullable)>>();
        await using (var cmd = connection.Connection.CreateCommand())
        {
            cmd.Transaction = connection.Transaction;
            cmd.CommandText = "SELECT s.name, t.name, c.name, ty.name, c.is_nullable FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!columns.TryGetValue(key, out var set)) columns[key] = set = new(StringComparer.OrdinalIgnoreCase);
                set[r.GetString(2)] = (r.GetString(3).ToLowerInvariant(), r.GetBoolean(4));
            }
        }
        var items = new List<DriftItem>();
        foreach (var module in scopeServices.GetServices<ModuleDescriptor>().OrderBy(m => m.Tier).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            if (!applied.TryGetValue(module.Name, out var ids) || ids.Count == 0) continue;
            var context = (DbContext)scopeServices.GetRequiredService(module.DbContextType);
            var assembly = context.GetService<IMigrationsAssembly>();
            var last = ids.Order(StringComparer.Ordinal).Last();
            if (!assembly.Migrations.TryGetValue(last, out var type)) continue;           // reported elsewhere as an unknown migration
            var migration = assembly.CreateMigration(type, context.Database.ProviderName!);
            var model = migration.TargetModel as IModel;
            if (model is IMutableModel mutable) model = mutable.FinalizeModel();
            if (model is not null) model = context.GetService<IModelRuntimeInitializer>().Initialize(model, designTime: true, validationLogger: null);
            var relational = model?.GetRelationalModel();
            if (relational is null) continue;
            var expectedTables = new HashSet<(string, string)>();
            foreach (var table in relational.Tables.Where(t => string.Equals(t.Schema, module.Schema, StringComparison.OrdinalIgnoreCase)))
            {
                expectedTables.Add((table.Schema!, table.Name));
                if (!columns.TryGetValue((table.Schema!, table.Name), out var actual)) { items.Add(new(module.Name, $"{table.Schema}.{table.Name}", "table is missing")); continue; }
                foreach (var column in table.Columns)
                {
                    if (!actual.TryGetValue(column.Name, out var found)) { items.Add(new(module.Name, $"{table.Schema}.{table.Name}.{column.Name}", "column is missing")); continue; }
                    var expectedType = BaseType(column.StoreType);
                    if (!string.Equals(expectedType, found.Type, StringComparison.OrdinalIgnoreCase)) items.Add(new(module.Name, $"{table.Schema}.{table.Name}.{column.Name}", $"type is {found.Type}, expected {expectedType}"));
                    else if (column.IsNullable != found.Nullable && expectedType != "timestamp") items.Add(new(module.Name, $"{table.Schema}.{table.Name}.{column.Name}", found.Nullable ? "column allows nulls, expected not" : "column does not allow nulls, expected nullable"));
                }
                foreach (var extra in actual.Keys.Where(a => table.Columns.All(c => !string.Equals(c.Name, a, StringComparison.OrdinalIgnoreCase)))) items.Add(new(module.Name, $"{table.Schema}.{table.Name}.{extra}", "column is not in the module's model"));
            }
            foreach (var (schema, name) in columns.Keys.Where(k => string.Equals(k.Schema, module.Schema, StringComparison.OrdinalIgnoreCase) && k.Table != "__EFMigrationsHistory" && !expectedTables.Contains(k)))
                items.Add(new(module.Name, $"{schema}.{name}", "table is not in the module's model"));
        }
        return items;
    }

    private static string BaseType(string? storeType)
    {
        var t = (storeType ?? "").ToLowerInvariant();
        var paren = t.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0) t = t[..paren];
        return t == "rowversion" ? "timestamp" : t.Trim();
    }
}

internal static class UpgradeBackups
{
    /// <summary>
    /// The released version whose schema a backup holds: the version a rollback to it would return the installation to. Judged from the migrations the backup recorded, not from the text of the version that made it,
    /// because a backup is a recovery point for the schema it contains. Null when the backup's schema is not any version this build knows.
    /// </summary>
    public static string? SchemaVersionOf(BackupManifest backup, UpgradeCatalogue catalogue, MigrationLedger compiled, string targetVersion)
    {
        var recorded = backup.Schema.Modules.ToDictionary(m => m.Module, m => (IReadOnlyList<string>)m.AppliedMigrations.OrderBy(x => x, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        bool Matches(IEnumerable<(string Module, IReadOnlyList<string> Ids)> expected)
        {
            var list = expected.ToList();
            return list.All(e => (recorded.TryGetValue(e.Module, out var have) ? have : []).SequenceEqual(e.Ids)) && recorded.Where(r => r.Value.Count > 0).All(r => list.Any(e => e.Module == r.Key));
        }
        foreach (var baseline in catalogue.Baselines.OrderBy(b => b.PlatformVersion, StringComparer.Ordinal))
            if (Matches(baseline.Modules.Select(m => (m.Module, (IReadOnlyList<string>)m.Migrations.Select(x => x.Id).ToList())))) return baseline.PlatformVersion;
        return Matches(compiled.Modules.Select(m => (m.Module, (IReadOnlyList<string>)m.Migrations.Select(x => x.Id).ToList()))) ? targetVersion : null;
    }
}
