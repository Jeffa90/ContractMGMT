using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Recovery;

/// <summary>Reads which migrations each module has applied and which row-level security policies are enabled: the schema facts a restore must reproduce.</summary>
internal static class SchemaInspector
{
    public static async Task<SchemaSection> ReadAsync(IServiceProvider scope, CancellationToken ct)
    {
        var modules = new List<ModuleSchema>();
        foreach (var module in scope.GetServices<ModuleDescriptor>().OrderBy(m => m.Tier).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            var context = (DbContext)scope.GetRequiredService(module.DbContextType);
            IEnumerable<string> applied;
            try { applied = context.Database.IsSqlServer() ? await context.Database.GetAppliedMigrationsAsync(ct) : []; }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException) { applied = []; }
            modules.Add(new ModuleSchema(module.Name, module.Schema, applied.OrderBy(m => m, StringComparer.Ordinal).ToList()));
        }
        var policies = await PoliciesAsync(scope, ct);
        return new SchemaSection(modules, policies.Count, policies);
    }

    public static async Task<List<string>> PoliciesAsync(IServiceProvider scope, CancellationToken ct)
    {
        var connection = scope.GetRequiredService<IScopedConnection>();
        var list = new List<string>();
        if (connection.Provider != DatabaseProvider.SqlServer) return list;
        await connection.ApplyTenantScopeAsync(ct);
        await using var cmd = connection.Connection.CreateCommand();
        cmd.Transaction = connection.Transaction;
        cmd.CommandText = "SELECT name FROM sys.security_policies WHERE is_enabled = 1 ORDER BY name";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }
}

/// <summary>Compares two inventories of the same installation, ignoring tables whose content changes simply by the platform running.</summary>
public static class InventoryComparer
{
    public static bool Equivalent(InventorySnapshot a, InventorySnapshot b) => Differences(a, b).Count == 0;

    /// <param name="allowRestoreEvents">A restore appends events to the installation's own audit chain (its record that the restore happened). That growth is expected and is not a difference; everything else is.</param>
    public static List<string> Differences(InventorySnapshot expected, InventorySnapshot actual, bool allowRestoreEvents = false)
    {
        var list = new List<string>();
        var left = expected.Tables.Where(t => !VolatileTables.Is(t.Table)).ToDictionary(t => (t.Table, t.TenantId));
        var right = actual.Tables.Where(t => !VolatileTables.Is(t.Table)).ToDictionary(t => (t.Table, t.TenantId));
        foreach (var (key, e) in left)
        {
            if (!right.TryGetValue(key, out var a)) { list.Add($"{key.Table}: missing"); continue; }
            if (allowRestoreEvents && key.TenantId == SystemTenant.Id && key.Table.EndsWith("AuditEvent", StringComparison.Ordinal) && a.Rows >= e.Rows) continue;
            if (e.Rows != a.Rows) list.Add($"{key.Table}{Scope(key.TenantId)}: {e.Rows} row(s) expected, {a.Rows} found");
            else if (e.Checksum is not null && a.Checksum is not null && e.Checksum != a.Checksum) list.Add($"{key.Table}{Scope(key.TenantId)}: content differs (checksum)");
        }
        foreach (var key in right.Keys.Where(k => !left.ContainsKey(k))) list.Add($"{key.Table}: not expected");
        var tenants = actual.Tenants.ToDictionary(t => t.TenantId);
        foreach (var e in expected.Tenants)
        {
            if (!tenants.TryGetValue(e.TenantId, out var a)) { list.Add($"tenant {e.Name}: missing"); continue; }
            if (e.AuditHeadSequence != a.AuditHeadSequence || e.AuditHeadHash != a.AuditHeadHash) list.Add($"tenant {e.Name}: audit chain head differs");
            if (!e.Releases.SequenceEqual(a.Releases)) list.Add($"tenant {e.Name}: configuration releases differ");
            if (!e.Organisations.SequenceEqual(a.Organisations)) list.Add($"tenant {e.Name}: organisations or regional settings differ");
            if (e.Documents != a.Documents || e.DocumentVersions != a.DocumentVersions) list.Add($"tenant {e.Name}: document counts differ");
        }
        return list;
    }

    private static string Scope(Guid? tenant) => tenant is null ? "" : $" (tenant {tenant.Value.ToString("N")[..8]})";
}
