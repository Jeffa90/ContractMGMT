using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Ecp.Audit;
using Ecp.Configuration;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecp.Recovery;

public enum CheckOutcome { Passed, Warning, Failed, NotVerified }
public enum CheckSeverity { Info, Low, Medium, High, Critical }
public enum ValidationOutcome { Passed, PassedWithWarnings, Failed, NotVerified }

/// <summary>
/// One result of post-restore validation. The code is stable (VAL-nnn). The description is plain language for anyone; the technical detail is shown only to people who hold <c>operations.validate</c> and is redacted before
/// it leaves the platform in a report.
/// </summary>
public sealed record ValidationCheck(string Code, string Title, CheckOutcome Outcome, CheckSeverity Severity, string Description, string TechnicalDetail, string Remediation, string CorrelationId);

public sealed record ValidationReport(string Subject, DateTime StartedUtc, DateTime CompletedUtc, ValidationOutcome Outcome, IReadOnlyList<ValidationCheck> Checks, string CorrelationId)
{
    public bool AllowsRelease => Outcome is ValidationOutcome.Passed or ValidationOutcome.PassedWithWarnings;
    public int Count(CheckOutcome outcome) => Checks.Count(c => c.Outcome == outcome);

    public static ValidationOutcome Summarise(IEnumerable<ValidationCheck> checks)
    {
        var list = checks.ToList();
        if (list.Any(c => c.Outcome == CheckOutcome.Failed)) return ValidationOutcome.Failed;
        if (list.Any(c => c.Outcome == CheckOutcome.NotVerified)) return ValidationOutcome.NotVerified;
        return list.Any(c => c.Outcome == CheckOutcome.Warning) ? ValidationOutcome.PassedWithWarnings : ValidationOutcome.Passed;
    }
}

/// <summary>How much validation to do. Full is the post-restore profile. Preflight judges an installation that is about to be upgraded (so it does not expect the tables a newer build adds, and skips the physical check). PostUpgrade judges an installation that has just been upgraded against the backup it came from and the upgrade manifest.</summary>
public enum ValidationProfile { Full, Preflight, PostUpgrade }

/// <summary>What an upgrade was supposed to produce, for the post-upgrade profile: the manifest that governs it, the inventory and isolation policies taken immediately before the first migration ran (what the upgraded data is compared with).</summary>
public sealed record UpgradeExpectation(UpgradeManifest Manifest, InventorySnapshot? BeforeInventory, IReadOnlyList<string>? PoliciesBefore);

/// <summary>What validation looks at: an installation reachable through <see cref="Services"/> (the running application, or a restored copy opened beside it) and, for a restore, the manifest it was restored from.</summary>
public sealed record ValidationTarget(IServiceProvider Services, string ConnectionString, string DatabaseName, bool Restored, BackupManifest? Manifest, bool HashDocumentContent, string Subject,
    ValidationProfile Profile = ValidationProfile.Full, UpgradeExpectation? Upgrade = null);

public interface IInstallationValidator
{
    Task<ValidationReport> ValidateAsync(ValidationTarget target, CancellationToken ct = default);
}

internal sealed class InstallationValidator(IInstallationInventory inventory, IClock clock, Microsoft.Extensions.Options.IOptions<RecoveryOptions> recovery) : IInstallationValidator
{
    private string _correlation = "";

    public async Task<ValidationReport> ValidateAsync(ValidationTarget target, CancellationToken ct = default)
    {
        var started = clock.UtcNow;
        _correlation = Guid.NewGuid().ToString("N");
        var checks = new List<ValidationCheck>();
        async Task Run(string code, string title, Func<Task<IEnumerable<ValidationCheck>>> body)
        {
            try { checks.AddRange(await body()); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                checks.Add(new(code, title, CheckOutcome.NotVerified, CheckSeverity.High, "This check could not be completed, so it is not verified.", Redaction.Clean(ex.GetType().Name + ": " + ex.Message),
                    "Fix the cause shown in the technical detail and run the validation again.", _correlation));
            }
        }

        // Connectivity first: nothing else means anything without it.
        var connected = true;
        await Run("VAL-001", "Database connectivity", async () => { var r = await ConnectivityAsync(target, ct); connected = r.Outcome == CheckOutcome.Passed; return [r]; });
        if (!connected)
        {
            checks.Add(Check("VAL-000", "Remaining checks", CheckOutcome.NotVerified, CheckSeverity.High, "The other checks were not run because the database could not be reached.", "", "Restore the connection and run the validation again."));
            return Finish(target, started, checks);
        }

        InventorySnapshot? snapshot = null;
        var preflight = target.Profile == ValidationProfile.Preflight;
        if (!preflight) await Run("VAL-002", "Migration history", () => MigrationsAsync(target, ct));
        if (!preflight) await Run("VAL-003", "Module schemas and tables", () => TablesAsync(target, ct));
        await Run("VAL-005", "Counts and content against the backup", async () =>
        {
            snapshot = await inventory.CollectAsync(target.Services, recovery.Value.TableChecksums && !preflight, ct);
            return preflight ? [] : CompareInventory(target, snapshot);
        });
        await Run("VAL-006", "Referential integrity", () => ConstraintsAsync(target, ct));
        await Run("VAL-007", "Row-level security policies", () => PoliciesAsync(target, ct));
        await Run("VAL-008", "Tenant isolation behaviour (session context)", () => IsolationAsync(target, ct));
        if (snapshot is not null)
        {
            checks.AddRange(AuditChecks(target, snapshot));
            checks.AddRange(RegionalChecks(target, snapshot));
        }
        await Run("VAL-010", "Document metadata and content", () => DocumentsAsync(target, ct));
        await Run("VAL-011", "Installed packages and signatures", () => PacksAsync(target, ct));
        await Run("VAL-013", "Configuration release hashes", () => ReleasesAsync(target, snapshot, ct));
        await Run("VAL-014", "Pinned workflows and links", () => LinksAsync(target, ct));
        await Run("VAL-016", "Notification and outbox consistency", () => OutboxAsync(target, ct));
        await Run("VAL-019", "Organisation readiness (assigned packs resolve)", () => ReadinessAsync(target, snapshot, ct));
        if (!preflight) await Run("VAL-021", "Database physical consistency", () => PhysicalAsync(target, ct));
        return Finish(target, started, checks);
    }

    private ValidationReport Finish(ValidationTarget target, DateTime started, List<ValidationCheck> checks)
        => new(target.Subject, started, clock.UtcNow, ValidationReport.Summarise(checks), checks.OrderBy(c => c.Code, StringComparer.Ordinal).ToList(), _correlation);

    private ValidationCheck Check(string code, string title, CheckOutcome outcome, CheckSeverity severity, string description, string detail, string remediation)
        => new(code, title, outcome, severity, description, Redaction.Clean(detail), remediation, _correlation);

    private ValidationCheck Pass(string code, string title, string description, string detail = "") => Check(code, title, CheckOutcome.Passed, CheckSeverity.Info, description, detail, "");

    private static async Task<List<Guid>> TenantsAsync(ValidationTarget t, CancellationToken ct)
    {
        await using var scope = t.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        var list = (await scope.ServiceProvider.GetRequiredService<Ecp.Tenancy.ITenantDirectory>().ListAsync(ct)).Where(x => x.Id != SystemTenant.Id).Select(x => x.Id).OrderBy(x => x).ToList();
        list.Add(SystemTenant.Id);
        return list;
    }

    private static async Task<long> ScalarAsync(AsyncServiceScope scope, string sql, CancellationToken ct)
    {
        var c = scope.ServiceProvider.GetRequiredService<IScopedConnection>();
        await c.ApplyTenantScopeAsync(ct);
        await using var cmd = c.Connection.CreateCommand();
        cmd.Transaction = c.Transaction; cmd.CommandText = InstallationInventory.ForProvider(c, sql);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o is null or DBNull ? 0 : Convert.ToInt64(o);
    }

    private async Task<ValidationCheck> ConnectivityAsync(ValidationTarget t, CancellationToken ct)
    {
        try
        {
            await using var scope = t.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
            var one = await ScalarAsync(scope, "SELECT 1", ct);
            return one == 1 ? Pass("VAL-001", "Database connectivity", "The application can open the database and run a query.") : Check("VAL-001", "Database connectivity", CheckOutcome.Failed, CheckSeverity.Critical, "The database answered unexpectedly.", "", "Check the connection.");
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            return Check("VAL-001", "Database connectivity", CheckOutcome.Failed, CheckSeverity.Critical, "The application could not open the database.", ex.Message,
                "Check the connection string, that the database exists and that the application login can open it (a restored database may have an orphaned database user: see the runbook).");
        }
    }

    private async Task<IEnumerable<ValidationCheck>> MigrationsAsync(ValidationTarget t, CancellationToken ct)
    {
        await using var scope = t.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        var connection = scope.ServiceProvider.GetRequiredService<IScopedConnection>();
        if (connection.Provider != DatabaseProvider.SqlServer)
            return [Check("VAL-002", "Migration history", CheckOutcome.NotVerified, CheckSeverity.Medium, "Migration history is only kept by SQL Server, so it is not verified for this provider.", "", "Use SQL Server for any installation whose recovery matters.")];
        var results = new List<ValidationCheck>();
        var schema = await SchemaInspector.ReadAsync(scope.ServiceProvider, ct);
        if (t.Profile == ValidationProfile.PostUpgrade && t.Upgrade is { } upgrade)
        {
            // After an upgrade the applied migrations are exactly the target inventory of the manifest: nothing missing, nothing extra, in the declared order.
            var wrong = new List<string>();
            foreach (var expected in upgrade.Manifest.TargetModules)
            {
                var actual = schema.Modules.FirstOrDefault(m => m.Module == expected.Module);
                if (actual is null) { wrong.Add($"{expected.Module}: module not found"); continue; }
                if (!actual.AppliedMigrations.SequenceEqual(expected.Migrations.Select(m => m.Id))) wrong.Add($"{expected.Module}: applied {actual.AppliedMigrations.Count}, expected {expected.Migrations.Count} (schema version {actual.AppliedMigrations.LastOrDefault() ?? "none"}, expected {expected.SchemaVersion})");
            }
            foreach (var extra in schema.Modules.Where(m => upgrade.Manifest.TargetModules.All(e => e.Module != m.Module))) wrong.Add($"{extra.Module}: not in the manifest");
            return [wrong.Count == 0
                ? Pass("VAL-002", "Migration history", $"Every module is at the schema version the upgrade manifest declares ({upgrade.Manifest.TargetModules.Sum(m => m.Migrations.Count)} migrations, applied in order).")
                : Check("VAL-002", "Migration history", CheckOutcome.Failed, CheckSeverity.Critical, "The database is not at the schema version the upgrade manifest declares.", string.Join("; ", wrong.Take(10)), "Do not release this installation. Roll back by restoring the verified pre-upgrade backup.")];
        }
        var problems = new List<string>(); var pending = new List<string>(); var differs = new List<string>();
        foreach (var module in schema.Modules)
        {
            var descriptor = scope.ServiceProvider.GetServices<ModuleDescriptor>().Single(m => m.Name == module.Module);
            var known = ((DbContext)scope.ServiceProvider.GetRequiredService(descriptor.DbContextType)).Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            foreach (var applied in module.AppliedMigrations.Where(a => !known.Contains(a))) problems.Add($"{module.Module}: {applied}");
            var notApplied = known.Where(k => !module.AppliedMigrations.Contains(k)).ToList();
            if (notApplied.Count > 0) pending.Add($"{module.Module} ({notApplied.Count})");
            if (t.Manifest?.Schema.Modules.FirstOrDefault(m => m.Module == module.Module) is { } expected && !expected.AppliedMigrations.SequenceEqual(module.AppliedMigrations)) differs.Add(module.Module);
        }
        results.Add(problems.Count > 0
            ? Check("VAL-002", "Migration history", CheckOutcome.Failed, CheckSeverity.Critical, "The database was created by a newer version of the platform than the one running. It must not be used with this version.",
                string.Join("; ", problems.Take(10)), "Run the version of the platform that made the backup, or restore an older backup.")
            : differs.Count > 0
                ? Check("VAL-002", "Migration history", CheckOutcome.Failed, CheckSeverity.High, "The restored migration history does not match the backup's.", "Modules: " + string.Join(", ", differs), "Restore again; do not use this copy.")
                : Pass("VAL-002", "Migration history", t.Manifest is null ? "Every migration recorded in the database is known to this version." : "The migration history matches the backup and is known to this version."));
        if (pending.Count > 0)
            results.Add(Check("VAL-002b", "Pending migrations", CheckOutcome.Warning, CheckSeverity.Medium, "This version of the platform has migrations that the restored database has not applied. Restore never applies them by itself.",
                string.Join(", ", pending), "After accepting the restore, run the migrate command deliberately, then validate again."));
        return results;
    }

    private async Task<IEnumerable<ValidationCheck>> TablesAsync(ValidationTarget t, CancellationToken ct)
    {
        await using var scope = t.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        var connection = scope.ServiceProvider.GetRequiredService<IScopedConnection>();
        if (connection.Provider != DatabaseProvider.SqlServer)
            return [Check("VAL-003", "Module schemas and tables", CheckOutcome.NotVerified, CheckSeverity.Low, "Table presence is verified against SQL Server only.", "", "")];
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await connection.ApplyTenantScopeAsync(ct);
        await using (var cmd = connection.Connection.CreateCommand())
        {
            cmd.Transaction = connection.Transaction;
            cmd.CommandText = "SELECT s.name + '.' + t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) existing.Add(r.GetString(0));
        }
        var missing = new List<string>(); var awaiting = new List<string>();
        var applied = await UpgradeScopes.AppliedAsync(scope.ServiceProvider, ct);
        foreach (var module in scope.ServiceProvider.GetServices<ModuleDescriptor>())
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(module.DbContextType);
            // A module with migrations still to apply (a backup from an earlier version) legitimately lacks the tables they create: that is reported by the pending-migrations note, not as damage.
            var pending = context.Database.GetMigrations().Count() > (applied.TryGetValue(module.Name, out var ids) ? ids.Count : 0);
            foreach (var type in context.Model.GetEntityTypes().Where(e => e.GetTableName() is not null && !e.IsOwned()))
                if (!existing.Contains($"{type.GetSchema()}.{type.GetTableName()}")) (pending ? awaiting : missing).Add($"{type.GetSchema()}.{type.GetTableName()}");
        }
        if (missing.Count == 0 && awaiting.Count > 0)
            return [Check("VAL-003", "Module schemas and tables", CheckOutcome.Warning, CheckSeverity.Low, $"{awaiting.Count} table(s) that this version expects are not there yet; they are created by migrations this database has not applied (the backup is from an earlier version).", string.Join(", ", awaiting),
                "Rehearse the upgrade on this copy before upgrading the installation (docs/UPGRADE_RUNBOOK.md).")];
        return [missing.Count == 0 ? Pass("VAL-003", "Module schemas and tables", "Every table that this version expects exists in the restored database.")
            : Check("VAL-003", "Module schemas and tables", CheckOutcome.Failed, CheckSeverity.High, $"{missing.Count} table(s) that this version expects are missing. The backup may predate them; migrations are not applied automatically.", string.Join(", ", missing.Take(15)),
                "If the backup is from an older version, run the migrate command deliberately after accepting the restore.")];
    }

    private List<ValidationCheck> CompareInventory(ValidationTarget t, InventorySnapshot actual)
    {
        var list = new List<ValidationCheck>();
        var totals = $"tenants {actual.Tenants_ - 0}, organisations {actual.Total("tenancy.Organisation") + actual.Total("Organisation")}, contracts {actual.Total("contracts.Contract") + actual.Total("Contract")}, counterparties {actual.Total("contracts.Supplier") + actual.Total("Supplier")}, audit events {actual.Total("audit.AuditEvent") + actual.Total("AuditEvent")}";
        if (t.Profile == ValidationProfile.PostUpgrade && t.Upgrade is { BeforeInventory: not null } upgraded)
        {
            // Every table holds what it held immediately before the first migration, except the tables the upgrade created, and the system tenant's own chain, which gains the upgrade's own events.
            var created = upgraded.Manifest.NewMigrations.SelectMany(n => n.Objects).Where(o => o.StartsWith("create table ", StringComparison.Ordinal)).Select(o => o["create table ".Length..]).ToHashSet(StringComparer.Ordinal);
            var changed = InventoryComparer.Differences(upgraded.BeforeInventory, actual, allowRestoreEvents: true)
                .Where(d => !d.StartsWith("tenant Installation", StringComparison.Ordinal) && !created.Any(c => d.StartsWith(c + ": not expected", StringComparison.Ordinal))).ToList();
            return [changed.Count == 0
                ? Pass("VAL-005", "Counts and content against the pre-upgrade data", "Every table, in every tenant, holds the same rows as immediately before the upgrade (counts and content checksums), apart from the tables the upgrade created.", totals)
                : Check("VAL-005", "Counts and content against the pre-upgrade data", CheckOutcome.Failed, CheckSeverity.Critical, $"{changed.Count} difference(s) between the data after the upgrade and the data before it. A migration changed something it was not declared to change.",
                    string.Join("; ", changed.Take(10)), "Do not release this installation. Roll back by restoring the verified pre-upgrade backup.")];
        }
        if (t.Manifest is null)
            return [Pass("VAL-005", "Counts and content against the backup", "No backup manifest was supplied, so counts were recorded but not compared.", totals)];
        var m = t.Manifest;
        List<string> differences;
        if (m.Quiescent) differences = InventoryComparer.Differences(m.InventoryBefore, actual, allowRestoreEvents: true);
        else
        {
            // An online backup: each table must lie between its count before and after the database backup. Content checksums cannot be compared.
            differences = [];
            var before = m.InventoryBefore.Tables.Where(x => !VolatileTables.Is(x.Table)).ToDictionary(x => (x.Table, x.TenantId));
            var after = m.InventoryAfter.Tables.Where(x => !VolatileTables.Is(x.Table)).ToDictionary(x => (x.Table, x.TenantId));
            foreach (var a in actual.Tables.Where(x => !VolatileTables.Is(x.Table)))
            {
                if (!before.TryGetValue((a.Table, a.TenantId), out var b)) { differences.Add($"{a.Table}: not in the backup's inventory"); continue; }
                var lo = Math.Min(b.Rows, after.TryGetValue((a.Table, a.TenantId), out var af) ? af.Rows : b.Rows); var hi = Math.Max(b.Rows, af?.Rows ?? b.Rows);
                if (a.Rows < lo || a.Rows > hi) differences.Add($"{a.Table}: {a.Rows} row(s), expected between {lo} and {hi}");
            }
            foreach (var e in m.InventoryBefore.Tenants)
            {
                var a = actual.Tenants.FirstOrDefault(x => x.TenantId == e.TenantId);
                if (a is null) differences.Add($"tenant {e.Name}: missing");
                else if (a.AuditHeadSequence < e.AuditHeadSequence) differences.Add($"tenant {e.Name}: audit chain is shorter than at backup");
            }
        }
        // The system tenant's own chain gains the restore's own events; operational tables are volatile. Neither counts as a difference of the customer data.
        differences = differences.Where(d => !d.StartsWith("tenant Installation", StringComparison.Ordinal)).ToList();
        list.Add(differences.Count == 0
            ? Pass("VAL-005", "Counts and content against the backup", m.Quiescent ? "Every table, in every tenant, holds the same rows as when the backup was made (counts and content checksums)." : "Every table lies within the counts recorded before and after this online backup.", totals)
            : Check("VAL-005", "Counts and content against the backup", CheckOutcome.Failed, CheckSeverity.Critical, $"{differences.Count} difference(s) between the restored data and the backup's inventory. The restore is not a faithful copy.", string.Join("; ", differences.Take(12)),
                "Do not use this copy. Verify the backup again and restore into a new target."));
        return list;
    }

    private async Task<IEnumerable<ValidationCheck>> ConstraintsAsync(ValidationTarget t, CancellationToken ct)
    {
        await using var scope = t.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        var connection = scope.ServiceProvider.GetRequiredService<IScopedConnection>();
        await connection.ApplyTenantScopeAsync(ct);
        await using var cmd = connection.Connection.CreateCommand();
        cmd.Transaction = connection.Transaction;
        var rows = 0; var sample = new List<string>();
        if (connection.Provider == DatabaseProvider.Sqlite) cmd.CommandText = "PRAGMA foreign_key_check";
        else cmd.CommandText = "SELECT s.name + '.' + o.name, f.name FROM sys.foreign_keys f JOIN sys.objects o ON o.object_id = f.parent_object_id JOIN sys.schemas s ON s.schema_id = o.schema_id WHERE f.is_disabled = 1 OR f.is_not_trusted = 1";
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) { rows++; if (sample.Count < 8) sample.Add(string.Join("/", Enumerable.Range(0, Math.Min(2, r.FieldCount)).Select(i => Convert.ToString(r.GetValue(i))))); }
        var detail = rows > 0 ? string.Join("; ", sample) : "";
        if (connection.Provider == DatabaseProvider.SqlServer && rows == 0)
        {
            // Untrusted/disabled constraints are the cheap signal; the full check rereads every row against every constraint.
            await using var dbcc = connection.Connection.CreateCommand();
            dbcc.Transaction = connection.Transaction;
            dbcc.CommandText = "DBCC CHECKCONSTRAINTS WITH ALL_CONSTRAINTS, NO_INFOMSGS";
            dbcc.CommandTimeout = 0;
            try
            {
                await using var r = await dbcc.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) { rows++; if (sample.Count < 8) sample.Add(Convert.ToString(r.GetValue(0)) + "/" + Convert.ToString(r.GetValue(1))); }
                detail = string.Join("; ", sample);
            }
            catch (SqlException ex) { return [Check("VAL-006", "Referential integrity", CheckOutcome.NotVerified, CheckSeverity.Medium, "The full constraint check could not be run with this account.", ex.Message, "Run DBCC CHECKCONSTRAINTS with an account that may, or accept this as not verified.")]; }
        }
        return [rows == 0 ? Pass("VAL-006", "Referential integrity", "Every foreign key, including the composite tenant keys, is enabled, trusted and satisfied.")
            : Check("VAL-006", "Referential integrity", CheckOutcome.Failed, CheckSeverity.Critical, $"{rows} constraint problem(s) were found.", detail, "Do not use this copy. Restore again from a verified backup.")];
    }

    private async Task<IEnumerable<ValidationCheck>> PoliciesAsync(ValidationTarget t, CancellationToken ct)
    {
        await using var scope = t.Services.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        if (scope.ServiceProvider.GetRequiredService<IScopedConnection>().Provider != DatabaseProvider.SqlServer)
            return [Check("VAL-007", "Row-level security policies", CheckOutcome.NotVerified, CheckSeverity.Medium, "Row-level security exists on SQL Server only.", "", "")];
        var policies = await SchemaInspector.PoliciesAsync(scope.ServiceProvider, ct);
        if (policies.Count == 0) return [Check("VAL-007", "Row-level security policies", CheckOutcome.Failed, CheckSeverity.Critical, "No tenant isolation policy is enabled in this database.", "", "Do not use this copy.")];
        if (t.Profile == ValidationProfile.PostUpgrade && t.Upgrade is { PoliciesBefore: not null } upgraded)
        {
            // An upgrade may add policies (for the tables it creates) and must never remove one.
            var before = upgraded.PoliciesBefore.ToHashSet(StringComparer.Ordinal);
            var removed = before.Where(p => !policies.Contains(p)).ToList();
            var created = upgraded.Manifest.NewMigrations.SelectMany(n => n.Objects).Where(o => o.StartsWith("create table ", StringComparison.Ordinal)).Select(o => o["create table ".Length..].Replace('.', '_')).ToHashSet(StringComparer.Ordinal);
            var unexplained = policies.Where(p => !before.Contains(p) && !created.Any(c => p.EndsWith("TenantPolicy_" + c, StringComparison.Ordinal))).ToList();
            return removed.Count == 0 && unexplained.Count == 0
                ? [Pass("VAL-007", "Row-level security policies", $"{policies.Count} tenant isolation policies are enabled: all {before.Count} from before the upgrade, and {policies.Count - before.Count} added for the tables the upgrade created.")]
                : [Check("VAL-007", "Row-level security policies", CheckOutcome.Failed, CheckSeverity.Critical, "The isolation policies after the upgrade are not the ones before it plus those the manifest explains.", $"removed: {string.Join(", ", removed.Take(5))}; unexplained: {string.Join(", ", unexplained.Take(5))}", "Do not release this installation. Roll back by restoring the verified pre-upgrade backup.")];
        }
        if (t.Manifest is not null && !t.Manifest.Schema.SecurityPolicies.OrderBy(p => p, StringComparer.Ordinal).SequenceEqual(policies.OrderBy(p => p, StringComparer.Ordinal)))
            return [Check("VAL-007", "Row-level security policies", CheckOutcome.Failed, CheckSeverity.Critical, "The enabled isolation policies are not the ones the backup had.", $"{policies.Count} enabled, {t.Manifest.Schema.SecurityPolicyCount} expected", "Do not use this copy.")];
        return [Pass("VAL-007", "Row-level security policies", $"{policies.Count} tenant isolation policies are enabled" + (t.Manifest is null ? "." : ", the same as the backup."))];
    }

    private async Task<IEnumerable<ValidationCheck>> IsolationAsync(ValidationTarget t, CancellationToken ct)
    {
        await using var probe = t.Services.CreateAsyncScope();
        if (probe.ServiceProvider.GetRequiredService<IScopedConnection>().Provider != DatabaseProvider.SqlServer)
            return [Check("VAL-008", "Tenant isolation behaviour (session context)", CheckOutcome.NotVerified, CheckSeverity.Medium, "Session-context isolation exists on SQL Server only.", "", "")];
        var tenants = await TenantsAsync(t, ct);
        var customer = tenants.FirstOrDefault(x => x != SystemTenant.Id);
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(t.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(ct);
        async Task<(long Rows, long Tenants)> Read(string? tenant)
        {
            await using (var set = connection.CreateCommand())
            {
                set.CommandText = "EXEC sp_set_session_context N'TenantId', @t; EXEC sp_set_session_context N'IsInstallationScope', 0;";
                set.Parameters.Add(new SqlParameter("@t", SqlDbType.NVarChar, 36) { Value = (object?)tenant ?? DBNull.Value });
                await set.ExecuteNonQueryAsync(ct);
            }
            await using var q = connection.CreateCommand();
            q.CommandText = "SELECT COUNT_BIG(*), COUNT(DISTINCT TenantId) FROM audit.AuditEvent";
            await using var r = await q.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetInt64(0), r.GetInt32(1));
        }
        var none = await Read(null);
        if (none.Rows != 0) return [Check("VAL-008", "Tenant isolation behaviour (session context)", CheckOutcome.Failed, CheckSeverity.Critical, "With no tenant set, data was still visible: isolation does not fail closed in this database.", $"{none.Rows} row(s) visible", "Do not use this copy.")];
        if (customer == Guid.Empty) return [Pass("VAL-008", "Tenant isolation behaviour (session context)", "With no tenant set nothing is visible. (There is no customer tenant to test a positive case.)")];
        var one = await Read(customer.ToString());
        if (one.Tenants > 1) return [Check("VAL-008", "Tenant isolation behaviour (session context)", CheckOutcome.Failed, CheckSeverity.Critical, "With one tenant set, another tenant's rows were visible.", $"{one.Tenants} tenants visible", "Do not use this copy.")];
        return [Pass("VAL-008", "Tenant isolation behaviour (session context)", "With no tenant set nothing is visible, and with one tenant set only that tenant's rows are.", $"{one.Rows} row(s) for the sample tenant")];
    }

    private IEnumerable<ValidationCheck> AuditChecks(ValidationTarget t, InventorySnapshot actual)
    {
        // A chain that was already broken when the backup was made is preserved as it was; that is reported, and it is not a failure of the restore.
        var knownBroken = t.Manifest?.InventoryAfter.Tenants.Where(x => !x.AuditChainValid).Select(x => x.TenantId).ToHashSet() ?? [];
        var bad = actual.Tenants.Where(x => !x.AuditChainValid && !knownBroken.Contains(x.TenantId)).ToList();
        if (bad.Count > 0)
            return [Check("VAL-009", "Audit chain integrity", CheckOutcome.Failed, CheckSeverity.Critical, $"The audit chain does not verify for {bad.Count} tenant(s).", string.Join("; ", bad.Select(b => $"{b.Name}: {b.AuditChainProblem}")),
                "Do not use this copy. Investigate who changed the audit records; the chain shows where.")];
        var result = new List<ValidationCheck>();
        var events = actual.Tenants.Sum(x => x.AuditEvents);
        if (t.Manifest is null) return [Pass("VAL-009", "Audit chain integrity", "Every tenant's audit chain verifies from its first event.", $"{events} events")];
        var notes = new List<string>();
        foreach (var e in t.Manifest.InventoryBefore.Tenants.Where(x => x.TenantId != SystemTenant.Id))
        {
            var a = actual.Tenants.FirstOrDefault(x => x.TenantId == e.TenantId);
            if (a is null) notes.Add($"{e.Name}: tenant missing");
            else if (a.AuditHeadSequence < e.AuditHeadSequence) notes.Add($"{e.Name}: shorter than at backup");
            else if (a.AuditHeadSequence == e.AuditHeadSequence && a.AuditHeadHash != e.AuditHeadHash) notes.Add($"{e.Name}: head hash differs");
        }
        if (notes.Count > 0) result.Add(Check("VAL-009", "Audit chain integrity", CheckOutcome.Failed, CheckSeverity.Critical, "The restored audit history does not match the backup's.", string.Join("; ", notes), "Do not use this copy."));
        else if (knownBroken.Count > 0)
            result.Add(Check("VAL-009", "Audit chain integrity", CheckOutcome.Warning, CheckSeverity.High, "An audit chain was already broken when the backup was made. The restore preserved it exactly; it was neither repaired nor regenerated.",
                string.Join("; ", actual.Tenants.Where(x => knownBroken.Contains(x.TenantId)).Select(b => $"{b.Name}: {b.AuditChainProblem}")), "Investigate the source installation: the chain shows where the history stops matching."));
        else result.Add(Pass("VAL-009", "Audit chain integrity", "Every tenant's audit chain verifies, and each ends where the backup recorded it (history was neither regenerated nor rewritten).", $"{events} events"));
        return result;
    }

    private IEnumerable<ValidationCheck> RegionalChecks(ValidationTarget t, InventorySnapshot actual)
    {
        var bad = actual.Tenants.SelectMany(x => x.Organisations.Where(o => o.RegionalFingerprint == "unresolvable").Select(o => $"{x.Name}/{o.Name}")).ToList();
        if (bad.Count > 0)
            return [Check("VAL-017", "Regional settings resolve", CheckOutcome.Failed, CheckSeverity.High, "Some organisations' regional settings cannot be resolved.", string.Join("; ", bad.Take(10)), "Check the organisations' regional profiles.")];
        var gaps = actual.Tenants.Sum(x => x.IdentifierProjectionGaps);
        var result = new List<ValidationCheck>();
        if (t.Manifest is null)
        {
            result.Add(Pass("VAL-017", "Regional settings resolve", $"{actual.Tenants.Sum(x => x.Organisations.Count)} organisation(s) resolve their regional settings."));
        }
        else
        {
            var diffs = new List<string>();
            foreach (var e in t.Manifest.InventoryBefore.Tenants.SelectMany(x => x.Organisations))
            {
                var a = actual.Tenants.SelectMany(x => x.Organisations).FirstOrDefault(o => o.Id == e.Id);
                if (a is null) diffs.Add($"{e.Name}: missing");
                else if (a.RegionalFingerprint != e.RegionalFingerprint) diffs.Add($"{e.Name}: regional settings differ");
            }
            result.Add(diffs.Count == 0 ? Pass("VAL-017", "Regional settings resolve", "Every organisation resolves the same locale, currency, time zone, identifier and address profile as before the backup.")
                : Check("VAL-017", "Regional settings resolve", CheckOutcome.Failed, CheckSeverity.High, "Some organisations resolve different regional settings from the backup.", string.Join("; ", diffs.Take(10)), "Do not use this copy."));
            var expectedGaps = t.Manifest.InventoryAfter.Tenants.Sum(x => x.IdentifierProjectionGaps);
            result.Add(gaps <= expectedGaps ? Pass("VAL-018", "Counterparty tax identifiers", gaps == 0 ? "Every counterparty that shows a tax identifier has a stored identifier record." : $"{gaps} counterparty identifier(s) lack a stored record, exactly as they did when the backup was made.")
                : Check("VAL-018", "Counterparty tax identifiers", CheckOutcome.Failed, CheckSeverity.High, "More counterparties show an identifier without a stored record than when the backup was made.", $"{gaps} found, {expectedGaps} expected", "Do not use this copy."));
        }
        if (t.Manifest is null) result.Add(gaps == 0 ? Pass("VAL-018", "Counterparty tax identifiers", "Every counterparty that shows a tax identifier has a stored identifier record.")
            : Check("VAL-018", "Counterparty tax identifiers", CheckOutcome.Warning, CheckSeverity.Low, $"{gaps} counterparty identifier(s) show a value without a stored identifier record.", "", "Review the counterparties."));
        return result;
    }

    private async Task<IEnumerable<ValidationCheck>> DocumentsAsync(ValidationTarget t, CancellationToken ct)
    {
        var store = t.Services.GetService<IDocumentStoreInventory>();
        var provider = t.Services.GetRequiredService<IDocumentStorageProvider>();
        if (t.Manifest is { Documents.ContentBackedUp: false })
            return [Check("VAL-010", "Document metadata and content", CheckOutcome.NotVerified, CheckSeverity.High, "The backup did not include document content, so content cannot be verified here.", t.Manifest.Documents.Note, "Restore the document store with its own tooling and verify it.")];
        if (store is null) return [Check("VAL-010", "Document metadata and content", CheckOutcome.NotVerified, CheckSeverity.Medium, "The document store provider cannot list its objects, so content is not verified.", "", "")];
        var skipped = 0; var present = new Dictionary<string, StoredObjectRef>(StringComparer.Ordinal);
        await foreach (var o in store.EnumerateAsync(_ => skipped++, ct)) present[o.StorageKey] = o;
        long metadata = 0, missing = 0, mismatch = 0; var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tenantId in await TenantsAsync(t, ct))
        {
            await using var scope = t.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            await foreach (var v in scope.ServiceProvider.GetRequiredService<IDocumentIntegrity>().VersionsAsync(ct))
            {
                if (v.StorageProvider != provider.ProviderKey) continue;
                metadata++; referenced.Add(v.StorageKey);
                if (!present.TryGetValue(v.StorageKey, out var o)) { missing++; continue; }
                if (o.SizeBytes != v.SizeBytes) { mismatch++; continue; }
                if (t.HashDocumentContent)
                {
                    await using var stream = await provider.OpenReadAsync(tenantId, v.StorageKey, ct);
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                    if (hash != v.Sha256) mismatch++;
                }
            }
        }
        var orphan = present.Keys.Count(k => !referenced.Contains(k));
        var expectedMissing = t.Manifest?.Documents.MissingContent ?? 0; var expectedMismatch = t.Manifest?.Documents.HashMismatch ?? 0;
        var detail = $"{metadata} version(s) recorded, {present.Count} object(s) held, {missing} missing, {mismatch} hash/size mismatch, {orphan} orphan, {skipped} skipped entries";
        // A source object that was already damaged is not restored (it would not hash to its own address), so it reappears here as missing: the two kinds are weighed together against what the backup recorded.
        if (missing + mismatch > expectedMissing + expectedMismatch)
            return [Check("VAL-010", "Document metadata and content", CheckOutcome.Failed, CheckSeverity.Critical, "Document content is missing or damaged compared with what the database records, beyond anything the backup recorded.", detail,
                "Do not release this copy. Restore again with the document store from the same backup; no restore continues silently with missing documents.")];
        if (missing > 0 || mismatch > 0 || orphan > 0 || skipped > 0)
            return [Check("VAL-010", "Document metadata and content", CheckOutcome.Warning, CheckSeverity.Medium, "Document content matches the database except for differences that already existed when the backup was made.", detail, "Investigate the source installation's document store.")];
        return [Pass("VAL-010", "Document metadata and content", t.HashDocumentContent ? "Every document version has its content, and the content hashes to the recorded SHA-256." : "Every document version has its content (sizes match; content was not re-hashed).", detail)];
    }

    private async Task<IEnumerable<ValidationCheck>> PacksAsync(ValidationTarget t, CancellationToken ct)
    {
        var problems = new List<string>(); var warnings = new List<string>(); var checkedCount = 0;
        foreach (var tenantId in await TenantsAsync(t, ct))
        {
            await using var scope = t.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var inv = scope.ServiceProvider.GetRequiredService<IConfigurationInventory>();
            var installed = await inv.InstalledPacksAsync(ct);
            var entries = await scope.ServiceProvider.GetRequiredService<IPackLoader>().ListAsync(ct);
            foreach (var p in installed)
            {
                checkedCount++;
                var entry = entries.FirstOrDefault(e => !e.IsShipped && e.PackKey == p.PackKey && e.Version == p.Version);
                if (entry is null) { if (!entries.Any(e => e.IsShipped && e.PackKey == p.PackKey && e.Version == p.Version)) problems.Add($"{p.PackKey} {p.Version}: not listed"); continue; }
                if (entry.Unavailable) problems.Add($"{p.PackKey} {p.Version}: {entry.UnavailableReason}");
                else if (entry.PackageSha256 != p.PackageSha256) problems.Add($"{p.PackKey} {p.Version}: package hash differs from the record");
                else if (!entry.Trust.IsAcceptable && !entry.Retired) problems.Add($"{p.PackKey} {p.Version}: signature is {entry.Trust.Status}");
                else if (!entry.IsCompatible) warnings.Add($"{p.PackKey} {p.Version}: compatibility issue");
                if (t.Manifest is not null && !t.Manifest.Packs.Installed.Any(m => m.TenantId == tenantId && m.PackKey == p.PackKey && m.Version == p.Version && m.PackageSha256 == p.PackageSha256))
                    warnings.Add($"{p.PackKey} {p.Version}: not in the backup's pack inventory");
            }
            if (t.Manifest is not null)
                foreach (var m in t.Manifest.Packs.Installed.Where(m => m.TenantId == tenantId && !installed.Any(i => i.PackKey == m.PackKey && i.Version == m.Version && i.PackageSha256 == m.PackageSha256)))
                    problems.Add($"{m.PackKey} {m.Version}: in the backup but not restored");
            if (t.Manifest is not null)
            {
                var keys = (await inv.TrustKeysAsync(ct)).Select(k => (k.KeyId, k.Fingerprint, k.Status)).ToHashSet();
                foreach (var k in t.Manifest.Packs.TrustKeys.Where(k => k.TenantId == tenantId))
                    if (!keys.Contains((k.KeyId, k.Fingerprint, k.Status))) problems.Add($"trust key {k.KeyId}: differs from the backup");
            }
        }
        if (problems.Count > 0)
            return [Check("VAL-011", "Installed packages and signatures", CheckOutcome.Failed, CheckSeverity.Critical, $"{problems.Count} installed package(s) are missing, altered or no longer trusted.", string.Join("; ", problems.Take(12)),
                "Do not use this copy. Restore the package files from the same backup.")];
        if (warnings.Count > 0)
            return [Check("VAL-011", "Installed packages and signatures", CheckOutcome.Warning, CheckSeverity.Low, "Installed packages verify; some differences were noted.", string.Join("; ", warnings.Take(12)), "Review the notes.")];
        return [Pass("VAL-011", "Installed packages and signatures", $"{checkedCount} installed package(s) are present, hash to their recorded SHA-256, and their signatures verify against the restored trust store.")];
    }

    private async Task<IEnumerable<ValidationCheck>> ReleasesAsync(ValidationTarget t, InventorySnapshot? snapshot, CancellationToken ct)
    {
        var broken = new List<string>(); var total = 0;
        foreach (var tenantId in await TenantsAsync(t, ct))
        {
            await using var scope = t.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            foreach (var r in await scope.ServiceProvider.GetRequiredService<IConfigurationInventory>().ReleasesAsync(ct)) { total++; if (!r.HashMatchesContent) broken.Add($"{r.Kind} #{r.Number}"); }
        }
        var diffs = new List<string>();
        if (t.Manifest is not null && snapshot is not null)
            foreach (var e in t.Manifest.InventoryBefore.Tenants)
            {
                var a = snapshot.Tenants.FirstOrDefault(x => x.TenantId == e.TenantId);
                if (a is null) continue;
                foreach (var r in e.Releases) if (!a.Releases.Contains(r)) diffs.Add($"{r.Kind} #{r.Number}");
            }
        if (broken.Count > 0 || diffs.Count > 0)
            return [Check("VAL-013", "Configuration release hashes", CheckOutcome.Failed, CheckSeverity.Critical, "Published configuration no longer matches its recorded hash, or a release from the backup is missing.", string.Join("; ", broken.Concat(diffs).Take(12)), "Do not use this copy.")];
        return [Pass("VAL-013", "Configuration release hashes", $"{total} published configuration release(s) hash to their recorded values" + (t.Manifest is null ? "." : " and every release in the backup is present, unchanged."))];
    }

    private async Task<IEnumerable<ValidationCheck>> LinksAsync(ValidationTarget t, CancellationToken ct)
    {
        var results = new List<ValidationCheck>();
        long orphanPins = 0, orphanDocs = 0;
        foreach (var tenantId in await TenantsAsync(t, ct))
        {
            await using var scope = t.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var connection = scope.ServiceProvider.GetRequiredService<IScopedConnection>();
            string Q(string schema, string table) => InstallationInventory.Qualified(connection, schema, table);
            var tables = scope.ServiceProvider.GetServices<ModuleDescriptor>().SelectMany(m => ((DbContext)scope.ServiceProvider.GetRequiredService(m.DbContextType)).Model.GetEntityTypes().Select(e => e.GetTableName())).ToHashSet();
            if (tables.Contains("Contract") && tables.Contains("ConfigurationRelease"))
                orphanPins += await ScalarAsync(scope, $"SELECT COUNT_BIG(*) FROM {Q("contracts", "Contract")} c WHERE c.ConfigReleaseId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {Q("configuration", "ConfigurationRelease")} r WHERE r.Id = c.ConfigReleaseId)", ct);
            if (tables.Contains("Document") && tables.Contains("Contract"))
                orphanDocs += await ScalarAsync(scope, $"SELECT COUNT_BIG(*) FROM {Q("documents", "Document")} d WHERE d.ContractId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM {Q("contracts", "Contract")} c WHERE c.Id = d.ContractId)", ct);
        }
        results.Add(orphanPins == 0 ? Pass("VAL-014", "Pinned workflows", "Every contract's pinned configuration release exists, so its workflow resolves exactly as before.")
            : Check("VAL-014", "Pinned workflows", CheckOutcome.Failed, CheckSeverity.Critical, $"{orphanPins} contract(s) are pinned to a configuration release that does not exist.", "", "Do not use this copy."));
        results.Add(orphanDocs == 0 ? Pass("VAL-015", "Contract and document links", "Every document that belongs to a contract finds that contract.")
            : Check("VAL-015", "Contract and document links", CheckOutcome.Failed, CheckSeverity.High, $"{orphanDocs} document(s) point to a contract that does not exist.", "", "Do not use this copy."));
        return results;
    }

    private async Task<IEnumerable<ValidationCheck>> OutboxAsync(ValidationTarget t, CancellationToken ct)
    {
        long orphan = 0, pending = 0;
        foreach (var tenantId in await TenantsAsync(t, ct))
        {
            await using var scope = t.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var connection = scope.ServiceProvider.GetRequiredService<IScopedConnection>();
            string Q(string schema, string table) => InstallationInventory.Qualified(connection, schema, table);
            var tables = scope.ServiceProvider.GetServices<ModuleDescriptor>().SelectMany(m => ((DbContext)scope.ServiceProvider.GetRequiredService(m.DbContextType)).Model.GetEntityTypes().Select(e => e.GetTableName())).ToHashSet();
            if (!tables.Contains("NotificationDelivery") || !tables.Contains("Notification")) continue;
            orphan += await ScalarAsync(scope, $"SELECT COUNT_BIG(*) FROM {Q("notifications", "NotificationDelivery")} d WHERE NOT EXISTS (SELECT 1 FROM {Q("notifications", "Notification")} n WHERE n.Id = d.NotificationId)", ct);
            pending += await ScalarAsync(scope, $"SELECT COUNT_BIG(*) FROM {Q("notifications", "NotificationDelivery")} d WHERE d.Status IN ('Pending','Retrying')", ct);
        }
        return [orphan == 0 ? Pass("VAL-016", "Notification and outbox consistency", $"Every queued delivery has its notification. {pending} delivery(ies) are waiting; they resume when maintenance mode is released.")
            : Check("VAL-016", "Notification and outbox consistency", CheckOutcome.Failed, CheckSeverity.High, $"{orphan} queued delivery(ies) have no notification.", "", "Do not use this copy.")];
    }

    private async Task<IEnumerable<ValidationCheck>> ReadinessAsync(ValidationTarget t, InventorySnapshot? snapshot, CancellationToken ct)
    {
        if (snapshot is null) return [Check("VAL-019", "Organisation readiness", CheckOutcome.NotVerified, CheckSeverity.Medium, "Organisations could not be listed.", "", "")];
        var results = new List<ValidationCheck>(); var bad = new List<string>(); var ready = 0; var noPack = 0;
        foreach (var tenant in snapshot.Tenants.Where(x => x.Organisations.Count > 0))
        {
            await using var scope = t.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant.TenantId);
            var loader = scope.ServiceProvider.GetRequiredService<IPackLoader>();
            foreach (var org in tenant.Organisations)
            {
                if (org.ActivePack is null) { noPack++; continue; }
                var parts = org.ActivePack.Split('@');
                var entry = await loader.FindAsync(parts[0], parts[1], usableOnly: true, ct);
                if (entry is null) bad.Add($"{tenant.Name}/{org.Name}: pack {org.ActivePack} is not usable");
                else ready++;
            }
        }
        results.Add(bad.Count == 0 ? Pass("VAL-019", "Organisation readiness (assigned packs resolve)", $"{ready} organisation(s) resolve their assigned solution pack, and {noPack} work without one, as before the backup.")
            : Check("VAL-019", "Organisation readiness (assigned packs resolve)", CheckOutcome.Failed, CheckSeverity.Critical, $"{bad.Count} organisation(s) cannot resolve the solution pack they were assigned.", string.Join("; ", bad.Take(10)), "Do not release this copy."));
        return results;
    }

    private async Task<IEnumerable<ValidationCheck>> PhysicalAsync(ValidationTarget t, CancellationToken ct)
    {
        await using var probe = t.Services.CreateAsyncScope();
        if (probe.ServiceProvider.GetRequiredService<IScopedConnection>().Provider != DatabaseProvider.SqlServer)
            return [Check("VAL-021", "Database physical consistency", CheckOutcome.NotVerified, CheckSeverity.Low, "A physical consistency check exists for SQL Server only.", "", "")];
        if (!SafeFiles.IsTargetDatabaseName(t.DatabaseName)) return [Check("VAL-021", "Database physical consistency", CheckOutcome.NotVerified, CheckSeverity.Low, "The database name is not one this tool will pass to a maintenance command.", "", "Run DBCC CHECKDB yourself.")];
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(t.ConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"DBCC CHECKDB (N'{t.DatabaseName}') WITH PHYSICAL_ONLY, NO_INFOMSGS";
        cmd.CommandTimeout = 0;
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (SqlException ex) { return [Check("VAL-021", "Database physical consistency", CheckOutcome.Failed, CheckSeverity.Critical, "The database server reports physical damage, or could not run the check with this account.", ex.Message, "Do not use this copy; run DBCC CHECKDB with full output.")]; }
        return [Pass("VAL-021", "Database physical consistency", "DBCC CHECKDB (physical only) found no problems.")];
    }
}
