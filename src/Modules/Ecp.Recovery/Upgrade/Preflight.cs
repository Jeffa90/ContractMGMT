using System.Text.Json;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Packs;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

public enum PreflightOutcome { Ready, ReadyWithWarnings, Blocked, NotVerified }

/// <summary>Advisory: an operator looking before deciding (maintenance mode and the lease are not yet required). Apply: the checks made immediately before migrations run (maintenance mode on, a named verified backup, no other operation).</summary>
public enum PreflightPhase { Advisory, Apply }

/// <summary>The installation an upgrade is being judged for: the running application, or a restored copy opened beside it for a rehearsal.</summary>
public sealed record UpgradeTarget(IServiceProvider Services, string ConnectionString, string DatabaseName, bool IsActive, string Label);

public sealed record PreflightOptions(string? BackupId = null, PreflightPhase Phase = PreflightPhase.Advisory, bool CheckBackupMatchesInstallation = true);

/// <summary>
/// The answer to "can this installation be upgraded safely?" (docs/UPGRADES.md, "Pre-flight"). Findings use the same record as post-restore validation: a stable <c>UPF-nnn</c> code, a severity, a plain-language
/// explanation, technical detail for authorised operators and a remediation, all with the correlation id. A blocker is a check whose outcome is Failed; a warning needs the owner's explicit acceptance to proceed.
/// </summary>
public sealed record PreflightReport(string Subject, string SourceVersion, string TargetVersion, string? ManifestSha256, string? BackupId, UpgradeStateKind State, DateTime StartedUtc, DateTime CompletedUtc,
    PreflightOutcome Outcome, IReadOnlyList<ValidationCheck> Checks, IReadOnlyList<string> PendingMigrations, string CorrelationId, PreflightPhase Phase)
{
    public bool CanApply => Outcome is PreflightOutcome.Ready or PreflightOutcome.ReadyWithWarnings && State == UpgradeStateKind.AtSource;
    public IEnumerable<ValidationCheck> Blockers => Checks.Where(c => c.Outcome == CheckOutcome.Failed);
    public IEnumerable<ValidationCheck> Warnings => Checks.Where(c => c.Outcome == CheckOutcome.Warning);
    public int Count(CheckOutcome outcome) => Checks.Count(c => c.Outcome == outcome);

    public static PreflightOutcome Summarise(IEnumerable<ValidationCheck> checks)
    {
        var list = checks.ToList();
        if (list.Any(c => c.Outcome == CheckOutcome.Failed)) return PreflightOutcome.Blocked;
        if (list.Any(c => c.Outcome == CheckOutcome.NotVerified)) return PreflightOutcome.NotVerified;
        return list.Any(c => c.Outcome == CheckOutcome.Warning) ? PreflightOutcome.ReadyWithWarnings : PreflightOutcome.Ready;
    }
}

public interface IUpgradePreflight
{
    Task<PreflightReport> RunAsync(UpgradeTarget target, PreflightOptions options, CancellationToken ct = default);
}

/// <summary>What platform version this build is, as a manifest names it.</summary>
public static class UpgradeVersions
{
    public static string Current => PlatformVersion.Current.ToString();
}

internal sealed class UpgradePreflight(
    UpgradeCatalogue catalogue, IDatabaseBackupProvider database, IBackupCatalogue backups, IBackupVerifier verifier, IInstallationValidator validator, IInstallationInventory inventory,
    IOptions<RecoveryOptions> recovery, IClock clock, StartupFacts facts) : IUpgradePreflight
{
    private static readonly object Gate = new();
    private static MigrationLedger? _compiled;

    /// <summary>The ledger of this build's migrations. Computed once: it cannot change while the process runs.</summary>
    internal static MigrationLedger Compiled(IEnumerable<ModuleDescriptor> modules)
    {
        lock (Gate) return _compiled ??= MigrationInventory.Build(modules);
    }

    private string _correlation = "";
    private RecoveryOptions Options => recovery.Value;

    public async Task<PreflightReport> RunAsync(UpgradeTarget target, PreflightOptions options, CancellationToken ct = default)
    {
        var started = clock.UtcNow;
        _correlation = Guid.NewGuid().ToString("N");
        var checks = new List<ValidationCheck>();
        var targetVersion = UpgradeVersions.Current;
        string detected = "unknown"; var state = UpgradeStateKind.Inconsistent; IReadOnlyList<string> pending = [];

        async Task Run(string code, string title, Func<Task> body)
        {
            try { await body(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                checks.Add(Make(code, title, CheckOutcome.NotVerified, CheckSeverity.High, "This check could not be completed, so it is not verified.", ex.GetType().Name + ": " + ex.Message, "Fix the cause shown in the technical detail and run the pre-flight again."));
            }
        }

        var modules = target.Services.GetServices<ModuleDescriptor>().ToList();
        var compiled = Compiled(modules);
        var manifest = catalogue.ManifestTo(targetVersion);

        // ---- The release itself: does this build have a manifest, and is the manifest the build's own? ----
        await Run("UPF-001", "Upgrade manifest", () =>
        {
            if (manifest is null)
            {
                checks.Add(Make("UPF-001", "Upgrade manifest", CheckOutcome.Failed, CheckSeverity.Critical, $"This build (platform {targetVersion}) has no upgrade manifest, so no upgrade path to it can be called safe.",
                    $"Manifests known: {string.Join(", ", catalogue.Manifests.Select(m => m.TargetPlatformVersion))}", "Use a release that carries an upgrade manifest, or do not upgrade."));
                return Task.CompletedTask;
            }
            var problems = UpgradeManifestValidator.Validate(manifest, catalogue, compiled);
            var critical = problems.Where(p => p.Critical).ToList();
            checks.Add(critical.Count == 0
                ? Make("UPF-001", "Upgrade manifest", problems.Count == 0 ? CheckOutcome.Passed : CheckOutcome.Warning, problems.Count == 0 ? CheckSeverity.Info : CheckSeverity.Low,
                    $"The upgrade manifest for {manifest.SourcePlatformVersion} to {manifest.TargetPlatformVersion} is intact and describes exactly this build's migrations.", $"SHA-256 {manifest.ManifestSha256}; {string.Join("; ", problems.Select(p => p.Code + " " + p.Message))}", "")
                : Make("UPF-001", "Upgrade manifest", CheckOutcome.Failed, CheckSeverity.Critical, "The upgrade manifest cannot be trusted: it has been changed, or it does not describe this build.", string.Join("; ", critical.Select(p => p.Code + " " + p.Message)),
                    "Obtain the release again from its source and verify it. Do not upgrade with a manifest you cannot account for."));
            return Task.CompletedTask;
        });

        // ---- Where the installation stands ----
        Dictionary<string, IReadOnlyList<string>> applied = [];
        await Run("UPF-010", "Schema version and upgrade path", async () =>
        {
            applied = await UpgradeScopes.AppliedAsync(target.Services, ct);
            var sources = manifest?.SupportedSourceVersions ?? [];
            var s = UpgradeStateDetector.Detect(applied, compiled, catalogue, sources);
            state = s.Kind; pending = s.Pending; detected = s.SourceVersion ?? (s.Kind == UpgradeStateKind.AtTarget ? targetVersion : "unknown");
            switch (s.Kind)
            {
                case UpgradeStateKind.AtSource:
                    checks.Add(Make("UPF-010", "Schema version and upgrade path", CheckOutcome.Passed, CheckSeverity.Info, $"The installation is at platform {s.SourceVersion}, a supported source for this upgrade ({s.Pending.Count} migration(s) to apply).", string.Join(", ", s.Pending), ""));
                    break;
                case UpgradeStateKind.AtTarget:
                    checks.Add(Make("UPF-010", "Schema version and upgrade path", CheckOutcome.Passed, CheckSeverity.Info, "The installation already has every migration this build knows. There is nothing to upgrade.", "", ""));
                    break;
                case UpgradeStateKind.Empty:
                    checks.Add(Make("UPF-010", "Schema version and upgrade path", CheckOutcome.Failed, CheckSeverity.High, "The database has no migrations applied: it is a new database, not an installation to upgrade.", "", "Use the migrate command for a first installation."));
                    break;
                case UpgradeStateKind.Ahead:
                    checks.Add(Make("UPF-011", "Database newer than this build", CheckOutcome.Failed, CheckSeverity.Critical, "The database was written by a newer version of the platform than the one running. Running an older application against it is not supported and is not attempted.",
                        string.Join(", ", s.Unknown.Take(10)), "Start the version that matches the database, or restore a backup taken before the newer version ran. The platform does not downgrade."));
                    break;
                case UpgradeStateKind.Inconsistent:
                    checks.Add(Make("UPF-013", "Migration order", CheckOutcome.Failed, CheckSeverity.Critical, "The recorded migrations have a gap or an order this build cannot explain: a migration's predecessor is missing.", "Modules: " + string.Join(", ", s.Gaps), "Do not upgrade. Restore a verified backup and compare."));
                    break;
                default:
                    checks.Add(Make("UPF-014", "Partial or earlier failed upgrade", CheckOutcome.Failed, CheckSeverity.Critical,
                        "The installation is part-way between two versions: some modules have migrations the others do not. It is not any released version, so the platform cannot say what applying the rest would do.",
                        "Modules part-way: " + string.Join(", ", s.PartialModules) + "; pending: " + string.Join(", ", s.Pending.Take(10)), "Do not continue the upgrade. Roll back by restoring the verified pre-upgrade backup (docs/ROLLBACK_BY_RESTORE.md)."));
                    break;
            }
            if (s.Kind == UpgradeStateKind.AtSource && manifest is not null && !manifest.SupportedSourceVersions.Contains(s.SourceVersion!))
                checks.Add(Make("UPF-012", "Unsupported upgrade path", CheckOutcome.Failed, CheckSeverity.Critical, $"Upgrading from {s.SourceVersion} to {targetVersion} is not a path the manifest supports.", "", "Upgrade in the supported steps."));
        });

        // ---- Historical migrations and recorded checksums ----
        await Run("UPF-015", "Historical migration checksums", async () =>
        {
            var changed = new List<string>(); var unrecorded = 0; var generatorDiffers = 0;
            var baseline = detected != "unknown" ? catalogue.Baseline(detected) : null;
            foreach (var (module, ids) in applied)
            {
                var compiledModule = compiled.Find(module);
                foreach (var id in ids)
                {
                    var now = compiledModule?.Migrations.FirstOrDefault(m => m.Id == id);
                    var was = baseline?.Modules.FirstOrDefault(m => m.Module == module)?.Migrations.FirstOrDefault(m => m.Id == id);
                    if (now is not null && was is not null && !string.Equals(now.Checksum, was.Checksum, StringComparison.Ordinal)) changed.Add($"{module}/{id} (differs from the {detected} baseline)");
                }
            }
            await using var scope = target.Services.CreateAsyncScope();
            var records = await scope.ServiceProvider.GetRequiredService<IMigrationRecords>().ListAsync(ct);
            if (records is not null)
                foreach (var r in records)
                {
                    var now = compiled.Find(r.Module)?.Migrations.FirstOrDefault(m => m.Id == r.MigrationId);
                    if (now is null) continue;
                    if (!string.Equals(now.Checksum, r.Checksum, StringComparison.Ordinal))
                    {
                        if (!string.Equals(r.Generator, compiled.Generator, StringComparison.Ordinal)) generatorDiffers++;
                        else changed.Add($"{r.Module}/{r.MigrationId} (differs from the checksum recorded when it was applied)");
                    }
                }
            unrecorded = records is null ? applied.Sum(a => a.Value.Count) : applied.Sum(a => a.Value.Count(id => records.All(r => r.Module != a.Key || r.MigrationId != id)));
            if (changed.Count > 0)
                checks.Add(Make("UPF-015", "Historical migration checksums", CheckOutcome.Failed, CheckSeverity.Critical, "A migration that is already applied has been changed since it was released or applied. The schema may not be what the platform thinks it is.",
                    string.Join("; ", changed.Take(10)), "Do not upgrade. Obtain the release again, verify it, and compare the database with a verified backup."));
            else
                checks.Add(Make("UPF-015", "Historical migration checksums", generatorDiffers > 0 || unrecorded > 0 && records is not null && detected == "unknown" ? CheckOutcome.Warning : CheckOutcome.Passed, CheckSeverity.Low,
                    "Every applied migration matches its frozen baseline" + (records is null ? " (the platform has not recorded checksums for this database yet; they are recorded when the upgrade is applied)." : "; recorded checksums agree."),
                    generatorDiffers > 0 ? $"{generatorDiffers} recorded checksum(s) were produced by another data-access library version and could not be compared." : "", ""));
        });

        // ---- Schema drift ----
        await Run("UPF-016", "Schema drift", async () =>
        {
            await using var scope = target.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
            var drift = await SchemaDrift.CheckAsync(scope.ServiceProvider, applied, ct);
            if (drift is null) { checks.Add(Make("UPF-016", "Schema drift", CheckOutcome.NotVerified, CheckSeverity.Medium, "Schema drift is checked on SQL Server only.", "", "Use SQL Server for any installation whose upgrade matters.")); return; }
            checks.Add(drift.Count == 0
                ? Make("UPF-016", "Schema drift", CheckOutcome.Passed, CheckSeverity.Info, "The database matches the schema its migration history says it has: no table or column is missing, extra or of another type.", "", "")
                : Make("UPF-016", "Schema drift", CheckOutcome.Failed, CheckSeverity.Critical, $"The database differs from the schema its migration history says it has ({drift.Count} difference(s)). It was changed outside the platform's migrations.",
                    string.Join("; ", drift.Take(15).Select(d => $"{d.Object}: {d.Problem}")), "Do not upgrade. Find out who changed the schema; restore a verified backup if the change was not intended."));
        });

        // ---- Server, permissions, storage ----
        await Run("UPF-003", "Database server", () => ServerAsync(target, checks, ct));
        await Run("UPF-030", "Free storage", () => StorageAsync(target, checks, ct));

        // ---- Backup ----
        await Run("UPF-020", "Verified backup", () => BackupAsync(target, options, manifest, detected, checks, ct));

        // ---- Maintenance mode, leases and background work ----
        await Run("UPF-040", "Maintenance mode and operations in progress", () => OperationsStateAsync(target, options, checks, ct));

        // ---- Installation health: the post-restore checks that apply before an upgrade ----
        await Run("UPF-050", "Installation health", async () =>
        {
            var report = await validator.ValidateAsync(new ValidationTarget(target.Services, target.ConnectionString, target.DatabaseName, Restored: false, Manifest: null, HashDocumentContent: true, Subject: target.Label, Profile: ValidationProfile.Preflight), ct);
            foreach (var c in report.Checks) checks.Add(Translate(c));
        });

        // ---- What each module says about its own data and in-flight work ----
        await Run("UPF-060", "Compatibility and in-flight work", () => ProbesAsync(target, checks, ct));

        var outcome = PreflightReport.Summarise(checks);
        return new PreflightReport(target.Label, detected, targetVersion, manifest?.ManifestSha256, options.BackupId, state, started, clock.UtcNow, outcome,
            checks.OrderBy(c => c.Code, StringComparer.Ordinal).ThenBy(c => c.Title, StringComparer.Ordinal).ToList(), pending, _correlation, options.Phase);
    }

    private ValidationCheck Make(string code, string title, CheckOutcome outcome, CheckSeverity severity, string description, string detail, string remediation)
        => new(code, title, outcome, severity, description, Redaction.Clean(detail), remediation, _correlation);

    /// <summary>Maps an installation-health check to the pre-flight code space. The original code stays in the title so the two catalogues can be read together.</summary>
    private ValidationCheck Translate(ValidationCheck c)
    {
        var code = c.Code switch
        {
            "VAL-001" => "UPF-004", "VAL-006" => "UPF-052", "VAL-007" => "UPF-053", "VAL-008" => "UPF-054", "VAL-009" => "UPF-055", "VAL-010" => "UPF-056", "VAL-011" => "UPF-057", "VAL-013" => "UPF-061",
            "VAL-014" => "UPF-062", "VAL-015" => "UPF-063", "VAL-016" => "UPF-064", "VAL-017" => "UPF-065", "VAL-018" => "UPF-066", "VAL-019" => "UPF-067", "VAL-000" => "UPF-005", _ => "UPF-059"
        };
        return c with { Code = code, Title = $"{c.Title} ({c.Code})", CorrelationId = _correlation };
    }

    private async Task ServerAsync(UpgradeTarget target, List<ValidationCheck> checks, CancellationToken ct)
    {
        if (!database.Supported)
        {
            checks.Add(Make("UPF-003", "Database server", CheckOutcome.Failed, CheckSeverity.Critical, "Upgrades are supported on SQL Server only.", database.UnsupportedReason, "Use SQL Server for any installation whose upgrade matters."));
            return;
        }
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(target.ConnectionString) { Pooling = false }.ConnectionString);
        try { await connection.OpenAsync(ct); }
        catch (SqlException ex)
        {
            checks.Add(Make("UPF-004", "Database connection", CheckOutcome.Failed, CheckSeverity.Critical, "The database cannot be reached with the configured account.", ex.Message, "Fix the connection and run the pre-flight again."));
            return;
        }
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(40)), CAST(SERVERPROPERTY('Edition') AS nvarchar(80)), CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER'), 0) AS int), CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'CREATE TABLE'), 0) AS int)";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var major = r.IsDBNull(0) ? 0 : r.GetInt32(0);
        var version = r.GetString(1); var edition = r.GetString(2); var canAlter = r.GetInt32(3) == 1; var canCreate = r.GetInt32(4) == 1;
        checks.Add(major >= 15
            ? Make("UPF-003", "Database server", CheckOutcome.Passed, CheckSeverity.Info, $"SQL Server {version} is a supported version.", edition, "")
            : Make("UPF-003", "Database server", CheckOutcome.Warning, CheckSeverity.Medium, "This SQL Server version is older than the versions the platform is tested on (2019 and later).", $"{version} {edition}", "Plan to move to a supported version; test the upgrade on a restored copy first."));
        checks.Add(canAlter && canCreate
            ? Make("UPF-004", "Database connection", CheckOutcome.Passed, CheckSeverity.Info, "The configured account can connect and change the schema, which migrations need.", "", "")
            : Make("UPF-004", "Database connection", CheckOutcome.Failed, CheckSeverity.High, "The configured account can connect but is not allowed to change the schema, so migrations would fail.", $"ALTER: {canAlter}; CREATE TABLE: {canCreate}",
                "Run the upgrade with the privileged migration account (docs/UPGRADE_RUNBOOK.md); the day-to-day application account should not have these rights."));
    }

    private async Task StorageAsync(UpgradeTarget target, List<ValidationCheck> checks, CancellationToken ct)
    {
        const long block = 256L * 1024 * 1024, warn = 1024L * 1024 * 1024;
        long? sqlFree = null, dbBytes = null;
        if (database.Supported)
        {
            try
            {
                await using var connection = new SqlConnection(new SqlConnectionStringBuilder(target.ConnectionString) { Pooling = false }.ConnectionString);
                await connection.OpenAsync(ct);
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT MIN(vs.available_bytes), (SELECT SUM(CAST(size AS bigint)) * 8192 FROM sys.database_files) FROM sys.master_files mf CROSS APPLY sys.dm_os_volume_stats(mf.database_id, mf.file_id) vs WHERE mf.database_id = DB_ID()";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct)) { sqlFree = r.IsDBNull(0) ? null : r.GetInt64(0); dbBytes = r.IsDBNull(1) ? null : r.GetInt64(1); }
            }
            catch (SqlException) { /* the account may lack VIEW SERVER STATE; reported below as not verified */ }
        }
        if (sqlFree is null)
        {
            checks.Add(Make("UPF-030", "Free storage", CheckOutcome.NotVerified, CheckSeverity.Medium, "The free space on the database server's volume could not be read with this account.", "VIEW SERVER STATE is needed to read it.", "Check the free space on the database volume yourself before upgrading; migrations that rebuild an index or rewrite data need room."));
            return;
        }
        var detail = $"database about {dbBytes / (1024 * 1024)} MB; {sqlFree / (1024 * 1024)} MB free on the volume";
        checks.Add(sqlFree < block ? Make("UPF-030", "Free storage", CheckOutcome.Failed, CheckSeverity.Critical, "The database volume has almost no free space left. A migration could fail half-way.", detail, "Free space or extend the volume before upgrading.")
            : sqlFree < Math.Max(warn, dbBytes ?? 0) ? Make("UPF-030", "Free storage", CheckOutcome.Warning, CheckSeverity.Medium, "Free space on the database volume is less than the size of the database. Most upgrades need far less, but a data-rewriting migration may not.", detail, "Check the migration report for rewriting steps and extend the volume if in doubt.")
            : Make("UPF-030", "Free storage", CheckOutcome.Passed, CheckSeverity.Info, "The database volume has comfortable free space.", detail, ""));
        // The backup directory and the restore area, as this application sees them.
        foreach (var (label, path) in new[] { ("backup directory", Options.BackupDirectory), ("restore area", Options.RestoreTargetDirectory) })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var free = new DriveInfo(Path.GetFullPath(path)).AvailableFreeSpace;
                if (free < block) checks.Add(Make("UPF-032", "Free storage for " + label, CheckOutcome.Failed, CheckSeverity.High, $"The volume holding the {label} is almost full.", $"{free / (1024 * 1024)} MB free", "Free space before upgrading; the pre-upgrade backup and the rehearsal both need room."));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { /* the path may be a mount DriveInfo cannot describe */ }
        }
    }

    private async Task BackupAsync(UpgradeTarget target, PreflightOptions options, UpgradeManifest? manifest, string detected, List<ValidationCheck> checks, CancellationToken ct)
    {
        if (!Options.BackupConfigured)
        {
            checks.Add(Make("UPF-020", "Verified backup", CheckOutcome.Failed, CheckSeverity.Critical, "No backup directory is configured, so there is no way to take or check the pre-upgrade backup that rollback depends on.", "", "Configure backups (docs/BACKUP_AND_RESTORE.md) and take a verified backup first."));
            return;
        }
        var list = await backups.ListAsync(ct);
        BackupSummary? chosen;
        if (options.BackupId is { Length: > 0 } id)
        {
            chosen = list.FirstOrDefault(b => string.Equals(b.BackupId, id, StringComparison.Ordinal));
            if (chosen is null)
            {
                checks.Add(Make("UPF-020", "Verified backup", CheckOutcome.Failed, CheckSeverity.Critical, "The backup named for this upgrade does not exist.", "", "Name a backup from the Backups page (or list-backups). A wrong identifier is refused, never guessed."));
                return;
            }
        }
        else
        {
            chosen = list.FirstOrDefault(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings);
            if (options.Phase == PreflightPhase.Apply)
            {
                checks.Add(Make("UPF-020", "Verified backup", CheckOutcome.Failed, CheckSeverity.Critical, "An upgrade must name the verified backup it can be rolled back to.", "", "Take a backup with the running version, verify it, and name it."));
                return;
            }
            if (chosen is null)
            {
                checks.Add(Make("UPF-020", "Verified backup", CheckOutcome.Failed, CheckSeverity.Critical, "There is no verified backup. An upgrade can only be rolled back by restoring one.", "", "Take a backup now and verify it."));
                return;
            }
        }
        if (chosen.State is not (BackupState.Verified or BackupState.VerifiedWithWarnings))
        {
            checks.Add(Make("UPF-020", "Verified backup", CheckOutcome.Failed, CheckSeverity.Critical, $"The named backup is {chosen.State}, not verified.", chosen.Detail, "Verify it (or take a new one) before upgrading."));
            return;
        }
        // Verify again now: a backup is only a recovery point if it is still intact.
        var v = await verifier.VerifyAsync(chosen.BackupId, persist: false, ct);
        if (v.Outcome is VerificationOutcome.Failed or VerificationOutcome.NotVerified)
        {
            checks.Add(Make("UPF-020", "Verified backup", v.Outcome == VerificationOutcome.Failed ? CheckOutcome.Failed : CheckOutcome.NotVerified, CheckSeverity.Critical, "The backup does not pass its integrity check now, so it cannot be relied on for rollback.",
                string.Join("; ", v.Findings.Where(f => f.Severity == FindingSeverity.Failed).Take(3).Select(f => f.Code + " " + f.Message)), "Take a new backup and verify it."));
            return;
        }
        var backup = await verifier.LoadManifestAsync(chosen.BackupId, ct);
        checks.Add(Make("UPF-020", "Verified backup", CheckOutcome.Passed, CheckSeverity.Info, $"Backup {chosen.BackupId} passes its integrity check now.", $"manifest SHA-256 {v.ManifestSha256}; made {backup.CreatedUtc:yyyy-MM-dd HH:mm}Z by platform {backup.PlatformVersion}", ""));

        var maxAge = TimeSpan.FromHours(Math.Max(1, manifest?.Requirements.MaximumBackupAgeHours ?? 24));
        if (clock.UtcNow - backup.CreatedUtc > maxAge)
            checks.Add(Make("UPF-021", "Backup age", CheckOutcome.Warning, CheckSeverity.Medium, $"The backup is more than {maxAge.TotalHours:0} hours old. Anything done since then would be discarded by a rollback.", $"made {backup.CreatedUtc:yyyy-MM-dd HH:mm}Z", "Take a fresh backup in maintenance mode immediately before the upgrade, or accept the data-loss window explicitly."));
        if (manifest is not null)
        {
            var held = UpgradeBackups.SchemaVersionOf(backup, catalogue, Compiled(target.Services.GetServices<ModuleDescriptor>()), UpgradeVersions.Current);
            checks.Add(held is not null && held == detected
                ? Make("UPF-024", "Backup version", CheckOutcome.Passed, CheckSeverity.Info, $"The backup holds exactly the schema of platform {held}, the version being upgraded from, so rolling back to it returns the installation to that version.", $"made by platform {backup.PlatformVersion}", "")
                : Make("UPF-024", "Backup version", CheckOutcome.Failed, CheckSeverity.High, held is null
                    ? "The backup holds a schema that is not any released version this upgrade knows, so a rollback to it cannot be described."
                    : $"The backup holds the schema of platform {held}, but the installation being upgraded is at {detected}. Rolling back to it would not return the installation to the version being upgraded.", $"made by platform {backup.PlatformVersion}", "Take a backup with the version that is running now."));
        }
        if (!string.Equals(backup.EnvironmentMode, facts.Mode.ToString(), StringComparison.Ordinal))
            checks.Add(Make("UPF-022", "Backup matches installation", CheckOutcome.Failed, CheckSeverity.Critical, $"The backup was made in {backup.EnvironmentMode} mode and this installation runs in {facts.Mode} mode. Rolling back to it would put another environment's data here.", "", "Name a backup made by this installation."));
        if (options.CheckBackupMatchesInstallation)
        {
            var current = await inventory.CollectAsync(target.Services, tableChecksums: false, ct);
            var mismatch = new List<string>(); var newer = 0L;
            foreach (var then in backup.InventoryAfter.Tenants)
            {
                var now = current.Tenants.FirstOrDefault(t => t.TenantId == then.TenantId);
                if (now is null) { mismatch.Add($"tenant {then.Name} is not in this installation"); continue; }
                if (now.AuditHeadSequence < then.AuditHeadSequence) mismatch.Add($"tenant {then.Name}: the backup is further on than this database");
                else if (then.TenantId != SystemTenant.Id) newer += now.AuditHeadSequence - then.AuditHeadSequence;
            }
            foreach (var now in current.Tenants.Where(t => t.TenantId != SystemTenant.Id && backup.InventoryAfter.Tenants.All(b => b.TenantId != t.TenantId))) mismatch.Add($"tenant {now.Name} was created after the backup");
            if (mismatch.Count > 0 && mismatch.Any(m => m.Contains("not in this installation", StringComparison.Ordinal) || m.Contains("further on", StringComparison.Ordinal)))
                checks.Add(Make("UPF-022", "Backup matches installation", CheckOutcome.Failed, CheckSeverity.Critical, "The backup does not belong to this installation (its tenants or history are not this database's).", string.Join("; ", mismatch.Take(5)), "Name a backup made from this installation."));
            else if (newer > 0 || mismatch.Count > 0)
                checks.Add(Make("UPF-023", "Data-loss window", CheckOutcome.Warning, CheckSeverity.Medium, "Changes have been made since the backup. Rolling back by restore would discard them.", $"{newer} audited event(s) since the backup; {string.Join("; ", mismatch.Take(3))}",
                    "Enter maintenance mode and take a fresh backup, or accept in writing that work since the backup would be lost on rollback."));
            else
                checks.Add(Make("UPF-023", "Data-loss window", CheckOutcome.Passed, CheckSeverity.Info, "Nothing has changed since the backup, so rolling back to it would lose nothing.", "", ""));
        }
    }

    private async Task OperationsStateAsync(UpgradeTarget target, PreflightOptions options, List<ValidationCheck> checks, CancellationToken ct)
    {
        await using var scope = target.Services.CreateAsyncScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<IMaintenanceService>();
        var leases = scope.ServiceProvider.GetRequiredService<IOperationLeases>();
        var history = scope.ServiceProvider.GetRequiredService<IOperationHistory>();
        var status = await maintenance.GetAsync(ct);
        if (options.Phase == PreflightPhase.Apply)
            checks.Add(status.Enabled
                ? Make("UPF-040", "Maintenance mode", CheckOutcome.Passed, CheckSeverity.Info, "The installation is in maintenance mode: changes are refused and background work takes nothing new.", status.Reason, "")
                : Make("UPF-040", "Maintenance mode", CheckOutcome.Failed, CheckSeverity.Critical, "An upgrade runs only in maintenance mode, so nobody is changing data while the schema changes.", "", "Enter maintenance mode (with a reason), then run the upgrade."));
        else
            checks.Add(status.Enabled
                ? Make("UPF-040", "Maintenance mode", CheckOutcome.Passed, CheckSeverity.Info, "The installation is in maintenance mode.", status.Reason, "")
                : Make("UPF-040", "Maintenance mode", CheckOutcome.Warning, CheckSeverity.Low, "The installation is not in maintenance mode. That is fine for looking, but the upgrade itself will refuse to run until it is.", "", "Enter maintenance mode when you are ready to take the pre-upgrade backup."));
        var active = (await leases.ListAsync(ct)).Where(l => l.IsActive).ToList();
        var exclusive = active.Where(l => l.Name is "recovery.exclusive" or "upgrade.exclusive").ToList();
        if (exclusive.Count > 0)
            checks.Add(Make("UPF-041", "Operations in progress", CheckOutcome.Failed, CheckSeverity.High, "A backup, restore, validation or upgrade is running. Only one of these may run at a time.", string.Join("; ", exclusive.Select(l => $"{l.Name}: {l.OwnerDescription} since {l.AcquiredUtc:HH:mm}Z")), "Wait for it to finish, or take the lease over deliberately if its owner has stopped."));
        var jobs = active.Where(l => l.Name == "background.jobs").ToList();
        if (jobs.Count > 0)
            checks.Add(Make("UPF-042", "Background work", CheckOutcome.Warning, CheckSeverity.Low, "Background jobs are running. Maintenance mode lets them finish and starts nothing new; the upgrade waits for them to clear.", $"{jobs.Count} job lease(s) held", ""));
        var last = (await history.ListAsync(OperationKinds.Upgrade, 1, ct)).FirstOrDefault();
        if (last is not null && last.Status is OperationStatuses.Failed or OperationStatuses.Running)
            checks.Add(Make("UPF-043", "Previous upgrade", CheckOutcome.Warning, CheckSeverity.Medium, $"The last upgrade attempt ended as {last.Status}: {last.Summary}", $"started {last.StartedUtc:yyyy-MM-dd HH:mm}Z", "Understand why before trying again; a failed attempt may be why the installation is in maintenance mode."));
    }

    private async Task ProbesAsync(UpgradeTarget target, List<ValidationCheck> checks, CancellationToken ct)
    {
        var merged = new Dictionary<(string Area, string Code), (UpgradeFinding First, int Count, SortedSet<string> Details)>();
        foreach (var tenant in await UpgradeScopes.TenantsAsync(target.Services, ct))
        {
            await using var scope = target.Services.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenant);
            foreach (var probe in scope.ServiceProvider.GetServices<IUpgradeProbe>())
            {
                IReadOnlyList<UpgradeFinding> found;
                try { found = await probe.ProbeAsync(ct); }
                catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                {
                    // A probe reads this build's tables; on a database one release behind it may meet a table that does not exist yet. That is "not verified", never "fine".
                    found = [new(probe.Area, "UPG-" + probe.Area.ToUpperInvariant()[..Math.Min(3, probe.Area.Length)] + "-000", UpgradeFindingSeverity.Warning, "Could not be checked", "This area could not be examined.", ex.GetType().Name, "Run the pre-flight again after resolving the cause.", 1)];
                }
                foreach (var f in found)
                {
                    var key = (f.Area, f.Code);
                    merged.TryGetValue(key, out var existing);
                    var details = existing.Details ?? new SortedSet<string>(StringComparer.Ordinal);
                    if (f.Detail.Length > 0) details.Add(f.Detail);
                    merged[key] = (existing.First ?? f, existing.Count + f.Count, details);
                }
            }
        }
        foreach (var ((area, code), (first, count, details)) in merged.OrderBy(m => m.Key.Area, StringComparer.Ordinal).ThenBy(m => m.Key.Code, StringComparer.Ordinal))
        {
            var outcome = first.Severity switch { UpgradeFindingSeverity.Blocker => CheckOutcome.Failed, UpgradeFindingSeverity.Warning => CheckOutcome.Warning, _ => CheckOutcome.Passed };
            var severity = first.Severity switch { UpgradeFindingSeverity.Blocker => CheckSeverity.Critical, UpgradeFindingSeverity.Warning => CheckSeverity.Medium, _ => CheckSeverity.Info };
            checks.Add(Make(code, $"{area}: {first.Title}", outcome, severity, first.Description, string.Join(" | ", details.Take(8)) + $" (total {count})", first.Remediation));
        }
        if (merged.Count == 0) checks.Add(Make("UPF-060", "Compatibility and in-flight work", CheckOutcome.Passed, CheckSeverity.Info, "No module reported anything that would stop or complicate the upgrade.", "", ""));
    }
}
