using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Documents;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Ecp.SharedKernel.Localisation;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Recovery;

/// <summary>
/// Counts and fingerprints of an installation, taken through whichever service provider is handed in (the running application, or a restored copy opened beside it). The same code produces the snapshot a backup
/// records and the snapshot a restore is compared with, so the two can only differ if the data differs.
/// </summary>
public interface IInstallationInventory
{
    Task<InventorySnapshot> CollectAsync(IServiceProvider services, bool tableChecksums, CancellationToken ct = default);
}

/// <summary>Tables whose content changes while the platform merely runs (leases, job history), so their counts are recorded but are not part of the "nothing changed" comparison.</summary>
public static class VolatileTables
{
    public static bool Is(string table) => table.StartsWith("ops.", StringComparison.OrdinalIgnoreCase) || table.StartsWith("Operation", StringComparison.Ordinal)
        || table.Equals("notifications.JobRun", StringComparison.OrdinalIgnoreCase) || table.Equals("notifications.DispatchCursor", StringComparison.OrdinalIgnoreCase)
        || table.Equals("JobRun", StringComparison.Ordinal) || table.Equals("DispatchCursor", StringComparison.Ordinal)
        || table.Equals("MaintenanceState", StringComparison.Ordinal) || table.Equals("OperationLease", StringComparison.Ordinal) || table.Equals("OperationRun", StringComparison.Ordinal);
}

internal sealed class InstallationInventory : IInstallationInventory
{
    public async Task<InventorySnapshot> CollectAsync(IServiceProvider services, bool tableChecksums, CancellationToken ct = default)
    {
        var clock = services.GetRequiredService<IClock>();
        var tables = new List<TableEntry>();
        var tenants = new List<TenantEntry>();
        IReadOnlyList<Ecp.Tenancy.TenantInfo> directory;
        await using (var scope = services.CreateAsyncScope())
        {
            var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
            using var _ = tenant.UseInstallationScope();
            directory = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().ListAsync(ct);
            tables.AddRange(await CountTablesAsync(scope.ServiceProvider, registryTables: true, tableChecksums, ct));
        }
        var everyone = directory.Where(t => t.Id != SystemTenant.Id).OrderBy(t => t.Id).Select(t => (t.Id, t.Name)).Append((SystemTenant.Id, "Installation")).ToList();
        foreach (var (tenantId, name) in everyone)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var counted = await CountTablesAsync(scope.ServiceProvider, registryTables: false, tableChecksums, ct);
            tables.AddRange(counted.Select(t => t with { TenantId = tenantId }));
            tenants.Add(await DescribeTenantAsync(scope.ServiceProvider, tenantId, name, counted, ct));
        }
        return new InventorySnapshot(clock.UtcNow, tenants.OrderBy(t => t.TenantId).ToList(), tables.OrderBy(t => t.Table, StringComparer.Ordinal).ThenBy(t => t.TenantId).ToList());
    }

    /// <summary>The lightweight provider has no COUNT_BIG.</summary>
    internal static string ForProvider(IScopedConnection connection, string sql) => connection.Provider == DatabaseProvider.SqlServer ? sql : sql.Replace("COUNT_BIG(", "COUNT(", StringComparison.Ordinal);

    internal static string Qualified(IScopedConnection connection, string? schema, string table)
        => connection.Provider == DatabaseProvider.SqlServer && schema is not null ? $"[{schema}].[{table}]" : $"\"{table}\"";

    private static string TableName(IScopedConnection connection, IEntityType type)
        => connection.Provider == DatabaseProvider.SqlServer && type.GetSchema() is { } schema ? $"{schema}.{type.GetTableName()}" : type.GetTableName()!;

    private static async Task<List<TableEntry>> CountTablesAsync(IServiceProvider scope, bool registryTables, bool checksums, CancellationToken ct)
    {
        var connection = scope.GetRequiredService<IScopedConnection>();
        var result = new List<TableEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in scope.GetServices<ModuleDescriptor>().OrderBy(m => m.Tier).ThenBy(m => m.Name))
        {
            var context = (DbContext)scope.GetRequiredService(module.DbContextType);
            foreach (var type in context.Model.GetEntityTypes().Where(t => t.GetTableName() is not null && !t.IsOwned()))
            {
                var hasTenant = type.FindProperty("TenantId") is not null;
                if (hasTenant == registryTables) continue;
                var name = TableName(connection, type);
                if (!seen.Add(name)) continue;
                var sql = $"SELECT COUNT_BIG(*){(checksums && connection.Provider == DatabaseProvider.SqlServer && !VolatileTables.Is(name) ? ", CHECKSUM_AGG(BINARY_CHECKSUM(*))" : ", NULL")} FROM {Qualified(connection, type.GetSchema(), type.GetTableName()!)}";
                await connection.ApplyTenantScopeAsync(ct);
                await using var cmd = connection.Connection.CreateCommand();
                cmd.Transaction = connection.Transaction;
                cmd.CommandText = ForProvider(connection, sql);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                result.Add(new TableEntry(name, null, Convert.ToInt64(r.GetValue(0)), r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1))));
            }
        }
        return result;
    }

    private static async Task<long> ScalarAsync(IServiceProvider scope, string sql, CancellationToken ct)
    {
        var connection = scope.GetRequiredService<IScopedConnection>();
        await connection.ApplyTenantScopeAsync(ct);
        await using var cmd = connection.Connection.CreateCommand();
        cmd.Transaction = connection.Transaction;
        cmd.CommandText = ForProvider(connection, sql);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is null or DBNull ? 0 : Convert.ToInt64(o);
    }

    private static async Task<TenantEntry> DescribeTenantAsync(IServiceProvider scope, Guid tenantId, string name, List<TableEntry> counted, CancellationToken ct)
    {
        var connection = scope.GetRequiredService<IScopedConnection>();
        var sqlServer = connection.Provider == DatabaseProvider.SqlServer;
        string T(string schema, string table) => Qualified(connection, schema, table);

        // Audit chain: verify it and record where it ends, so a restored copy can be shown to hold the same history.
        var verifier = scope.GetRequiredService<AuditChainVerifier>();
        var chain = await verifier.VerifyAsync(ct);
        var head = await scope.GetRequiredService<IAuditEventSource>().Events.OrderByDescending(e => e.Sequence).Select(e => new { e.Sequence, e.Hash }).FirstOrDefaultAsync(ct);

        var organisations = new List<OrganisationEntry>();
        var assignments = await scope.GetRequiredService<IConfigurationInventory>().AssignmentsAsync(ct);
        var ids = new List<(Guid Id, string Name)>();
        if (counted.Any(t => t.Table.EndsWith("Organisation", StringComparison.Ordinal)))
        {
            await connection.ApplyTenantScopeAsync(ct);
            await using var cmd = connection.Connection.CreateCommand();
            cmd.Transaction = connection.Transaction;
            cmd.CommandText = $"SELECT Id, Name FROM {T("tenancy", "Organisation")} ORDER BY Id";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ids.Add((r.GetGuid(0), r.GetString(1)));
        }
        var resolver = scope.GetRequiredService<IRegionalSettingsResolver>();
        foreach (var (id, orgName) in ids)
        {
            string fingerprint; var complete = false;
            try { var s = await resolver.GetAsync(id, ct); fingerprint = RegionalFingerprint(s); complete = s.IsComplete; }
            catch (Exception ex) when (ex is DomainException or InvalidOperationException) { fingerprint = "unresolvable"; }
            var active = assignments.Where(a => a.OrganisationId == id && a.Status == "Active").Select(a => $"{a.PackKey}@{a.Version}").FirstOrDefault();
            organisations.Add(new OrganisationEntry(id, orgName, fingerprint, complete, active));
        }

        var releases = (await scope.GetRequiredService<IConfigurationInventory>().ReleasesAsync(ct)).Select(r => new ReleaseEntry(r.ReleaseId, r.OrganisationId, r.Kind, r.Number, r.StoredHash)).ToList();
        var openDrafts = await scope.GetRequiredService<IConfigurationInventory>().OpenDraftCountAsync(ct);
        var documents = await scope.GetRequiredService<IDocumentIntegrity>().DocumentCountAsync(ct);
        long versions = counted.Where(t => t.Table.EndsWith("DocumentVersion", StringComparison.Ordinal)).Sum(t => t.Rows);

        long gaps = 0;
        if (counted.Any(t => t.Table.EndsWith("SupplierIdentifier", StringComparison.Ordinal)) && ColumnOf(scope, "Supplier", "TaxIdentifier") is { } projection)
        {
            var column = sqlServer ? $"[{projection}]" : "\"" + projection + "\"";
            gaps = await ScalarAsync(scope,
                $"SELECT COUNT_BIG(*) FROM {T("contracts", "Supplier")} s WHERE s.{column} IS NOT NULL AND s.{column} <> '' AND NOT EXISTS (SELECT 1 FROM {T("contracts", "SupplierIdentifier")} i WHERE i.SupplierId = s.Id AND i.EffectiveToUtc IS NULL)", ct);
        }

        return new TenantEntry(tenantId, name, chain.EventsChecked, head?.Sequence ?? 0, head?.Hash ?? "", chain.IsValid, chain.Reason, organisations.OrderBy(o => o.Id).ToList(),
            releases.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.OrganisationId).ThenBy(r => r.Number).ToList(), openDrafts, documents, versions, gaps);
    }

    /// <summary>The database column a model property is stored in, read from the model so the inventory does not repeat a historical column name.</summary>
    private static string? ColumnOf(IServiceProvider scope, string table, string property)
    {
        foreach (var module in scope.GetServices<ModuleDescriptor>())
        {
            var type = ((DbContext)scope.GetRequiredService(module.DbContextType)).Model.GetEntityTypes().FirstOrDefault(e => e.GetTableName() == table);
            if (type?.FindProperty(property) is { } p) return p.GetColumnName();
        }
        return null;
    }

    /// <summary>A hash of the regional settings that decide how an organisation's dates, amounts and identifiers behave. Equal before a backup and after a restore means the same behaviour.</summary>
    public static string RegionalFingerprint(RegionalSettings s)
    {
        var sb = new StringBuilder();
        sb.Append(s.CountryCode).Append('|').Append(s.PrimaryLocale).Append('|').Append(string.Join(',', s.SupportedLocales)).Append('|').Append(s.DefaultCurrency).Append('|')
            .Append(string.Join(',', s.SupportedCurrencies)).Append('|').Append(s.TimeZoneId).Append('|').Append(s.TimeDisplay).Append('|').Append(s.PhoneRegion).Append('|')
            .Append(string.Join(',', s.Identifiers.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Key + ":" + i.Label + ":" + i.Rule + ":" + i.Checksum + ":" + i.Length + ":" + i.Mask + ":" + i.Required + ":" + i.Unique)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
