using System.Text.Json;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Ecp.Integration.Tests;

/// <summary>
/// OPS-02 (docs/UPGRADES.md), proved against a real SQL Server: an N-1 installation (the frozen 1.0.0 schema, populated with the three shipped businesses) is backed up, rehearsed on a restored copy, upgraded, validated, and
/// rolled back by restore, with the refusals an operator must be able to rely on. Skipped as NOT VERIFIED without a shared backup directory.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class UpgradeSqlServerTests(ITestOutputHelper output)
{
    private static async Task<T> InstallationAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private static string EvidenceText(N1Installation n1, string prefix)
    {
        var dir = Path.Combine(n1.Layout.BackupDirectory, "_evidence");
        var file = Directory.EnumerateFiles(dir, prefix + "-*.json").OrderBy(f => f, StringComparer.Ordinal).Last();
        return File.ReadAllText(file);
    }

    /// <summary>Copies a sanitised evidence file out for the CI artefact (ECP_TEST_EVIDENCE_DIR), when one is wanted.</summary>
    private static void Publish(N1Installation n1, string prefix, string name)
    {
        if (Environment.GetEnvironmentVariable("ECP_TEST_EVIDENCE_DIR") is not { Length: > 0 } target) return;
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, name), EvidenceText(n1, prefix));
    }

    [SqlServerBackupFact]
    public async Task A_rehearsal_on_a_restored_copy_upgrades_it_validates_it_and_the_three_businesses_work_as_before_while_the_installation_is_untouched()
    {
        await using var n1 = await N1Installation.CreateAsync();
        var target = N1Installation.NewTargetName();
        try
        {
            var sourceProbe = await n1.Data.ProbeAsync(n1.App.Services);
            var backup = await n1.BackupAsync();
            var appliedBefore = await UpgradeScopes.AppliedAsync(n1.App.Services, default);

            var accepted = new List<string>();
            var result = await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IUpgradeRehearsal>().RehearseAsync(new RehearsalRequest(backup.BackupId, target, "Rehearsal in a test", RehearsalPhrases.For(backup.BackupId), N1Installation.Operator, accepted)));
            output.WriteLine($"Rehearsal: {result.Outcome} {result.Failure}");
            foreach (var c in result.Restore?.Validation?.Checks.Where(c => c.Outcome != CheckOutcome.Passed) ?? []) output.WriteLine($"restore {c.Outcome} {c.Code} {c.Title}: {c.Description} | {c.TechnicalDetail}");
            if (result.Upgrade is { } u) { foreach (var s in u.Steps) output.WriteLine($"step {s.Module} {s.Outcome} {s.Failure}"); foreach (var c in u.Validation?.Checks.Where(c => c.Outcome != CheckOutcome.Passed) ?? []) output.WriteLine($"{c.Outcome} {c.Code} {c.Title}: {c.Description} | {c.TechnicalDetail}"); }
            Assert.True(result.Succeeded, result.Failure);
            var upgrade = result.Upgrade!;
            Assert.Equal(UpgradeVersions.Current, upgrade.ToVersion);
            Assert.Equal("1.0.0", upgrade.FromVersion);
            Assert.Contains(upgrade.Steps, s => s.Module == "Operations" && s.Outcome == "Applied");
            Assert.True(upgrade.Quiescence!.Clear);
            Assert.True(upgrade.Validation!.AllowsRelease);
            Assert.Contains(upgrade.Validation.Checks, c => c.Code == "UPV-001" && c.Outcome == CheckOutcome.Passed);
            Assert.Contains(upgrade.Validation.Checks, c => c.Code == "VAL-005" && c.Outcome == CheckOutcome.Passed);

            // The installation in use was not touched: still the N-1 schema, and everything reads exactly as before.
            Assert.Equal(appliedBefore.Select(a => (a.Key, a.Value.Count)).OrderBy(x => x.Key), (await UpgradeScopes.AppliedAsync(n1.App.Services, default)).Select(a => (a.Key, a.Value.Count)).OrderBy(x => x.Key));
            Assert.Equal(sourceProbe, await n1.Data.ProbeAsync(n1.App.Services));

            // The copy: upgraded, and the three businesses read exactly as they did before the upgrade.
            var targets = n1.App.Services.GetRequiredService<IRestoreTargetFactory>();
            var layout = RestoreLayout.For(n1.App.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<RecoveryOptions>>().Value, target);
            var cs = n1.App.Services.GetRequiredService<IDatabaseBackupProvider>().ConnectionStringFor(target);
            await using var host = targets.Open(new RestoreTarget(target, cs, layout.Documents, layout.Packs, false));
            var copyApplied = await UpgradeScopes.AppliedAsync(host.Services, default);
            Assert.Contains(copyApplied["Operations"], m => m.EndsWith("_AddMigrationRecords", StringComparison.Ordinal));
            var copyProbe = await n1.Data.ProbeAsync(host.Services);
            Assert.Equal(sourceProbe, copyProbe);
            var records = await InstallationAsync(host.Services, s => s.GetRequiredService<IMigrationRecords>().ListAsync());
            Assert.Equal(UpgradePreflight.Compiled(host.Services.GetServices<ModuleDescriptor>()).All.Count(), records!.Count);
            Assert.Contains(records, r => r.Origin == "applied" && r.PlatformVersion == UpgradeVersions.Current);
            Assert.All(records.Where(r => r.Origin == "adopted"), r => Assert.Equal("1.0.0", r.PlatformVersion));

            // Representative journeys on the upgraded copy.
            var attached = TestApp.Attach((ServiceProvider)host.Services, cs, n1.App.Clock, layout.Documents);
            foreach (var line in await UpgradeJourneys.RunAsync(n1.Data, attached)) output.WriteLine(line);

            // The evidence is sanitised and says it is not an approval.
            var text = EvidenceText(n1, "rehearsal");
            Assert.False(Redaction.ContainsSecret(text));
            Assert.DoesNotContain(n1.Data.DocumentText, text);
            Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not an approval for production use", text);
            Publish(n1, "rehearsal", "rehearsal-evidence.json");
        }
        finally { await RecoveryTestKit.DropAsync(target); }
    }

    private static async Task ExecuteAsync(N1Installation n1, string sql)
    {
        await using var c = new SqlConnection(n1.App.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        // The tests change the database as an administrator would: with the installation scope, so row-level security does not hide the rows.
        cmd.CommandText = $"EXEC sp_set_session_context N'TenantId', N'{SystemTenant.Id}'; EXEC sp_set_session_context N'IsInstallationScope', 1; " + sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<PreflightReport> PreflightAsync(N1Installation n1, string? backupId, PreflightPhase phase = PreflightPhase.Advisory)
        => InstallationAsync(n1.App.Services, s => s.GetRequiredService<IUpgradePreflight>().RunAsync(new UpgradeTarget(n1.App.Services, n1.App.ConnectionString, "active", true, "test"), new PreflightOptions(backupId, phase)));

    private static Task<UpgradeResult> ApplyAsync(N1Installation n1, UpgradeRequest request) => InstallationAsync(n1.App.Services, s => s.GetRequiredService<IUpgradeService>().ApplyAsync(request));

    private static async Task<DomainException> RefusedAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAnyAsync<DomainException>(action);
        return ex;
    }

    private static async Task<string> MessageOfAsync(Func<Task> action) => (await RefusedAsync(action)).Message;

    [SqlServerBackupFact]
    public async Task The_real_upgrade_runs_under_maintenance_after_a_verified_backup_keeps_every_business_as_it_was_releases_maintenance_and_cannot_be_run_twice_and_rollback_by_restore_is_proved()
    {
        await using var n1 = await N1Installation.CreateAsync();
        var rollbackTarget = N1Installation.NewTargetName();
        try
        {
            var before = await n1.Data.ProbeAsync(n1.App.Services);
            var heads = (await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IInstallationInventory>().CollectAsync(n1.App.Services, false))).Tenants.Where(t => t.TenantId != SystemTenant.Id).ToDictionary(t => t.TenantId, t => (t.AuditHeadSequence, t.AuditHeadHash));
            var backup = await n1.BackupAsync();
            await n1.EnterMaintenanceAsync();

            // The strict pre-flight agrees that this can proceed.
            var report = await PreflightAsync(n1, backup.BackupId, PreflightPhase.Apply);
            foreach (var c in report.Checks.Where(c => c.Outcome != CheckOutcome.Passed)) output.WriteLine($"{c.Outcome} {c.Code} {c.Title}: {c.Description} | {c.TechnicalDetail}");
            Assert.True(report.CanApply, string.Join("; ", report.Blockers.Select(b => b.Code)));

            // A wrong confirmation phrase changes nothing.
            Assert.Contains("Type exactly", await MessageOfAsync(() => ApplyAsync(n1, n1.Request(backup.BackupId, confirm: "UPGRADE"))));
            
            var result = await ApplyAsync(n1, n1.Request(backup.BackupId, report.Warnings.Select(w => w.Code).ToList()));
            foreach (var c in result.Validation?.Checks.Where(c => c.Outcome != CheckOutcome.Passed) ?? []) output.WriteLine($"validation {c.Outcome} {c.Code} {c.Title}: {c.Description} | {c.TechnicalDetail}");
            Assert.True(result.Succeeded, result.Failure);
            Assert.True(result.MaintenanceReleased);
            Assert.False((await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IMaintenanceService>().GetAsync())).Enabled);
            Assert.Empty(await InstallationAsync(n1.App.Services, async s => (await s.GetRequiredService<IOperationLeases>().ListAsync()).Where(l => l.IsActive && l.Name is "recovery.exclusive" or "upgrade.exclusive").ToList()));

            // Everything reads as before; customer audit chains end exactly where they ended.
            Assert.Equal(before, await n1.Data.ProbeAsync(n1.App.Services));
            var after = (await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IInstallationInventory>().CollectAsync(n1.App.Services, false))).Tenants.Where(t => t.TenantId != SystemTenant.Id).ToDictionary(t => t.TenantId, t => (t.AuditHeadSequence, t.AuditHeadHash));
            Assert.Equal(heads, after);
            var state = UpgradeStateDetector.Detect(await UpgradeScopes.AppliedAsync(n1.App.Services, default), UpgradePreflight.Compiled(n1.App.Services.GetServices<ModuleDescriptor>()), UpgradeCatalogue.Embedded(), ["1.0.0"]);
            Assert.Equal(UpgradeStateKind.AtTarget, state.Kind);

            // The history and the audit trail record it; running it again is refused.
            var history = await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IOperationHistory>().ListAsync(OperationKinds.Upgrade, 5));
            Assert.Contains(history, h => h.Status is OperationStatuses.Completed or OperationStatuses.CompletedWithWarnings);
            Assert.Equal("upgrade_not_needed", (await RefusedAsync(() => ApplyAsync(n1, n1.Request(backup.BackupId)))).Code);
            Assert.Contains((await PreflightAsync(n1, backup.BackupId)).Checks, c => c.Code == "UPF-010" && c.Outcome == CheckOutcome.Passed);

            // Tampering with a recorded checksum afterwards is found by the next pre-flight.
            await ExecuteAsync(n1, "UPDATE TOP (1) ops.MigrationRecord SET Checksum = REPLICATE('0', 64) WHERE Module = 'Contracts';");
            var tampered = await PreflightAsync(n1, backup.BackupId);
            foreach (var c in tampered.Checks.Where(c => c.Code.StartsWith("UPF-01", StringComparison.Ordinal))) output.WriteLine($"{c.Outcome} {c.Code} {c.Title}: {c.Description} | {c.TechnicalDetail}");
            Assert.Contains(tampered.Checks, c => c.Code == "UPF-015" && c.Outcome == CheckOutcome.Failed);

            // Rollback by restore is proved against the pre-upgrade backup, and says what it would discard.
            var proof = await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IRollbackEvidence>().ProveAsync(new RollbackEvidenceRequest(backup.BackupId, rollbackTarget, "Rollback proof in a test", RollbackPhrases.For(backup.BackupId), N1Installation.Operator)));
            foreach (var c in proof.Checks) output.WriteLine($"rollback {c.Outcome} {c.Code} {c.Title}: {c.Description}");
            Assert.True(proof.Succeeded, proof.Failure);
            Assert.Equal("1.0.0", proof.RequiredApplicationVersion);
            Assert.Contains(proof.Checks, c => c.Code == "RBK-004" && c.Outcome == CheckOutcome.Passed);
            Assert.Contains(proof.Checks, c => c.Code == "RBK-003" && c.Outcome == CheckOutcome.Passed);
            Assert.Contains(proof.Checks, c => c.Code == "RBK-009" && c.Outcome == CheckOutcome.Warning);     // the upgrade's own events would be discarded
            Assert.True(proof.EventsDiscarded > 0);
            var text = EvidenceText(n1, "rollback");
            Assert.False(Redaction.ContainsSecret(text));
            Assert.Contains("discards everything created after the backup", text);
            Publish(n1, "upgrade", "upgrade-evidence.json");
            Publish(n1, "rollback", "rollback-evidence.json");
        }
        finally { await RecoveryTestKit.DropAsync(rollbackTarget); }
    }

    [SqlServerBackupFact]
    public async Task An_upgrade_is_refused_without_a_verified_backup_with_the_wrong_backup_without_maintenance_while_another_operation_runs_and_a_stale_backup_needs_explicit_acceptance()
    {
        await using var n1 = await N1Installation.CreateAsync();
        var backup = await n1.BackupAsync();
        await n1.EnterMaintenanceAsync();

        // No backup named; a backup that does not exist.
        Assert.Contains("UPF-020", await MessageOfAsync(() => ApplyAsync(n1, n1.Request("")))); 
        Assert.Contains("UPF-020", await MessageOfAsync(() => ApplyAsync(n1, n1.Request("bk-20200101T000000Z-deadbeef"))));

        // Not in maintenance mode.
        await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IMaintenanceService>().DisableAsync("test", N1Installation.Operator));
        Assert.Contains("UPF-040", await MessageOfAsync(() => ApplyAsync(n1, n1.Request(backup.BackupId))));
        await n1.EnterMaintenanceAsync();

        // Another operation holds the recovery lease; the upgrade is blocked and no new backup can start beside an upgrade's leases.
        var scopes = n1.App.Services.GetRequiredService<IServiceScopeFactory>();
        await using (var held = await LeaseHolder.TryAcquireAsync(scopes, RecoveryLeases.Exclusive, IOperationLeases.InstallationScope, "other-instance", "backup elsewhere", TimeSpan.FromMinutes(5)))
        {
            Assert.NotNull(held);
            Assert.Contains("UPF-041", await MessageOfAsync(() => ApplyAsync(n1, n1.Request(backup.BackupId))));
            Assert.Equal("operation_in_progress", (await RefusedAsync(async () => await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest("while another operation runs", N1Installation.Operator))))).Code);
        }
        await using (var running = await LeaseHolder.TryAcquireAsync(scopes, UpgradeLeases.Exclusive, IOperationLeases.InstallationScope, "other-upgrade", "an upgrade elsewhere", TimeSpan.FromMinutes(5)))
        {
            Assert.NotNull(running);
            Assert.Contains("UPF-041", await MessageOfAsync(() => ApplyAsync(n1, n1.Request(backup.BackupId))));
        }

        // A backup that is a day old is a warning that must be accepted by code; once accepted the upgrade proceeds.
        n1.App.Clock.Advance(TimeSpan.FromHours(26));
        var message = await MessageOfAsync(() => ApplyAsync(n1, n1.Request(backup.BackupId)));
        Assert.Contains("UPF-021", message);
        Assert.Contains("explicit acceptance", message);
        var report = await PreflightAsync(n1, backup.BackupId, PreflightPhase.Apply);
        Assert.Equal(PreflightOutcome.ReadyWithWarnings, report.Outcome);
        var done = await ApplyAsync(n1, n1.Request(backup.BackupId, report.Warnings.Select(w => w.Code).ToList()));
        Assert.True(done.Succeeded, done.Failure);
    }

    [SqlServerBackupFact]
    public async Task A_backup_from_another_installation_is_refused_as_not_belonging_to_this_one()
    {
        await using var n1 = await N1Installation.CreateAsync();
        await using var other = await N1Installation.CreateAsync();
        var foreign = await other.BackupAsync();
        // The operator copies the other installation's backup into this installation's backup folder; it verifies on its own, but it is not this installation's.
        CopyDirectory(Path.Combine(other.Layout.BackupDirectory, foreign.BackupId), Path.Combine(n1.Layout.BackupDirectory, foreign.BackupId));
        await n1.EnterMaintenanceAsync();
        var report = await PreflightAsync(n1, foreign.BackupId, PreflightPhase.Apply);
        foreach (var c in report.Checks) output.WriteLine($"{c.Outcome} {c.Code} {c.Title}: {c.Description} | {c.TechnicalDetail}");
        Assert.Contains(report.Checks, c => c.Code == "UPF-022" && c.Outcome == CheckOutcome.Failed);
        Assert.Equal(PreflightOutcome.Blocked, report.Outcome);
        Assert.Contains("UPF-022", await MessageOfAsync(() => ApplyAsync(n1, n1.Request(foreign.BackupId))));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, (UnixFileMode)0b111_111_111);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        // The database server reads the .bak by its own account: whatever the copy is, it must stay readable to it, as the originals are.
        if (!OperatingSystem.IsWindows())
            foreach (var path in Directory.EnumerateFileSystemEntries(to, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(path, (UnixFileMode)0b111_111_111);
    }

    [SqlServerBackupFact]
    public async Task Schema_drift_unknown_migrations_and_missing_predecessors_block_the_upgrade_before_anything_is_changed()
    {
        await using var n1 = await N1Installation.CreateAsync();
        var backup = await n1.BackupAsync();

        // Drift: a column that no migration created.
        await ExecuteAsync(n1, "ALTER TABLE contracts.Supplier ADD RogueColumn int NULL;");
        var drift = await PreflightAsync(n1, backup.BackupId);
        Assert.Contains(drift.Checks, c => c.Code == "UPF-016" && c.Outcome == CheckOutcome.Failed && c.TechnicalDetail.Contains("RogueColumn", StringComparison.Ordinal));
        Assert.Equal(PreflightOutcome.Blocked, drift.Outcome);
        await ExecuteAsync(n1, "ALTER TABLE contracts.Supplier DROP COLUMN RogueColumn;");
        Assert.DoesNotContain((await PreflightAsync(n1, backup.BackupId)).Checks, c => c is { Code: "UPF-016", Outcome: CheckOutcome.Failed });

        // An unknown migration: the database was written by a newer version.
        await ExecuteAsync(n1, "INSERT INTO contracts.__EFMigrationsHistory (MigrationId, ProductVersion) VALUES (N'29991231000000_FromTheFuture', N'99.0.0');");
        var ahead = await PreflightAsync(n1, backup.BackupId);
        Assert.Equal(UpgradeStateKind.Ahead, ahead.State);
        Assert.Contains(ahead.Checks, c => c.Code == "UPF-011" && c.Outcome == CheckOutcome.Failed);
        await ExecuteAsync(n1, "DELETE FROM contracts.__EFMigrationsHistory WHERE MigrationId = N'29991231000000_FromTheFuture';");

        // A missing predecessor: a migration in the middle of the history is gone.
        await ExecuteAsync(n1, "DELETE FROM contracts.__EFMigrationsHistory WHERE MigrationId = N'20261016010330_AccessScopes';");
        var gap = await PreflightAsync(n1, backup.BackupId);
        Assert.Contains(gap.Checks, c => c.Code is "UPF-013" or "UPF-014" && c.Outcome == CheckOutcome.Failed);
        Assert.NotEqual(UpgradeStateKind.AtSource, gap.State);
        await n1.EnterMaintenanceAsync();
        Assert.Contains("cannot start", await MessageOfAsync(() => ApplyAsync(n1, n1.Request(backup.BackupId))));
        Assert.DoesNotContain((await UpgradeScopes.AppliedAsync(n1.App.Services, default))["Operations"], m => m.EndsWith("_AddMigrationRecords", StringComparison.Ordinal));
    }

    [SqlServerBackupFact]
    public async Task A_migration_that_fails_stops_the_upgrade_keeps_maintenance_mode_on_in_a_failed_state_and_does_not_continue_by_itself()
    {
        await using var n1 = await N1Installation.CreateAsync();
        var backup = await n1.BackupAsync();
        await n1.EnterMaintenanceAsync();
        // A disabled policy that already has the name the migration is about to use makes the migration fail after it has started: the whole migration rolls back.
        await ExecuteAsync(n1, "CREATE SECURITY POLICY security.[TenantPolicy_ops_MigrationRecord] ADD FILTER PREDICATE security.fn_TenantPredicate(TenantId) ON ops.OperationRun WITH (STATE = OFF);");
        var report = await PreflightAsync(n1, backup.BackupId, PreflightPhase.Apply);
        var result = await ApplyAsync(n1, n1.Request(backup.BackupId, report.Warnings.Select(w => w.Code).ToList()));
        Assert.Equal("Failed", result.Outcome);
        Assert.Contains(result.Steps, s => s.Module == "Operations" && s.Outcome == "Failed");
        Assert.False(result.MaintenanceReleased);
        var maintenance = await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IMaintenanceService>().GetAsync());
        Assert.True(maintenance.Enabled);
        Assert.Equal(MaintenanceCauses.UpgradeFailed, maintenance.Cause);
        // It cannot be left by accident.
        Assert.Equal("upgrade_state", (await RefusedAsync(async () => await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IMaintenanceService>().DisableAsync("trying to carry on", N1Installation.Operator)))).Code);
        // The failed migration left nothing behind: the table is not there, the history is unchanged, and no leases are held.
        await using (var c = new SqlConnection(n1.App.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT OBJECT_ID(N'ops.MigrationRecord')";
            Assert.True((await cmd.ExecuteScalarAsync()) is DBNull or null);
        }
        Assert.DoesNotContain(await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IOperationLeases>().ListAsync()), l => l.IsActive && l.Name is "recovery.exclusive" or "upgrade.exclusive");
        var history = await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IOperationHistory>().ListAsync(OperationKinds.Upgrade, 5));
        Assert.Contains(history, h => h.Status == OperationStatuses.Failed);
        // The next pre-flight reports the earlier failure instead of pretending it did not happen.
        Assert.Contains((await PreflightAsync(n1, backup.BackupId, PreflightPhase.Apply)).Checks, c => c.Code == "UPF-043");
        // An operator who knowingly leaves the failed state is allowed to, and it is audited.
        await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IMaintenanceService>().DisableAsync("Rolled back by restore; leaving the failed state knowingly", N1Installation.Operator, acknowledgeRestoreState: true));
    }

    [SqlServerBackupFact]
    public async Task Background_work_that_does_not_finish_stops_the_upgrade_before_any_migration_and_leaves_maintenance_mode_on()
    {
        await using var n1 = await N1Installation.CreateAsync();
        var backup = await n1.BackupAsync();
        await n1.EnterMaintenanceAsync();
        var scopes = n1.App.Services.GetRequiredService<IServiceScopeFactory>();
        await using var job = await LeaseHolder.TryAcquireAsync(scopes, "background.jobs", n1.Data.TenantId.ToString(), "another-instance", "job cycle on another instance", TimeSpan.FromMinutes(5));
        Assert.NotNull(job);
        var report = await PreflightAsync(n1, backup.BackupId, PreflightPhase.Apply);
        Assert.Contains(report.Checks, c => c.Code == "UPF-042" && c.Outcome == CheckOutcome.Warning);
        var result = await ApplyAsync(n1, n1.Request(backup.BackupId, report.Warnings.Select(w => w.Code).ToList(), quiescenceSeconds: 1));
        Assert.Equal("Failed", result.Outcome);
        Assert.False(result.Quiescence!.Clear);
        Assert.Equal(1, result.Quiescence.LeasesAtStart);
        Assert.Empty(result.Steps);
        Assert.Equal(MaintenanceCauses.UpgradeFailed, (await InstallationAsync(n1.App.Services, s => s.GetRequiredService<IMaintenanceService>().GetAsync())).Cause);
        Assert.DoesNotContain((await UpgradeScopes.AppliedAsync(n1.App.Services, default))["Operations"], m => m.EndsWith("_AddMigrationRecords", StringComparison.Ordinal));
    }

    [SqlServerBackupFact]
    public async Task A_manifest_that_was_changed_or_is_missing_means_the_upgrade_path_is_not_called_safe()
    {
        var real = UpgradeCatalogue.Embedded().ManifestTo(UpgradeVersions.Current)!;
        // Changed after it was produced: its hash no longer matches.
        var tampered = new UpgradeCatalogue(UpgradeCatalogue.Embedded().Baselines, [real with { ReleaseId = "ecp-tampered" }], UpgradeCatalogue.Embedded().Reviews, UpgradeCatalogue.Embedded().Notes);
        await using (var n1 = await N1Installation.CreateAsync(more: s => s.AddSingleton(tampered)))
        {
            var backup = await n1.BackupAsync();
            var report = await PreflightAsync(n1, backup.BackupId);
            Assert.Contains(report.Checks, c => c.Code == "UPF-001" && c.Outcome == CheckOutcome.Failed && c.TechnicalDetail.Contains("UPM-002", StringComparison.Ordinal));
            Assert.Equal(PreflightOutcome.Blocked, report.Outcome);
        }
        // No manifest for this version at all: an unknown target.
        var none = new UpgradeCatalogue(UpgradeCatalogue.Embedded().Baselines, [], UpgradeCatalogue.Embedded().Reviews, UpgradeCatalogue.Embedded().Notes);
        await using (var n1 = await N1Installation.CreateAsync(more: s => s.AddSingleton(none)))
        {
            var backup = await n1.BackupAsync();
            await n1.EnterMaintenanceAsync();
            Assert.Equal("upgrade_unknown_target", (await RefusedAsync(() => ApplyAsync(n1, n1.Request(backup.BackupId)))).Code);
            Assert.Contains((await PreflightAsync(n1, backup.BackupId)).Checks, c => c is { Code: "UPF-001", Outcome: CheckOutcome.Failed });
        }
    }
}
