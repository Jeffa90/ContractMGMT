using System.Text.Json;
using Ecp.Audit;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

public static class RehearsalPhrases
{
    public static string For(string backupId) => $"REHEARSE UPGRADE FROM {backupId}";
}

public static class RollbackPhrases
{
    public static string For(string backupId) => $"PROVE ROLLBACK TO {backupId}";
}

public sealed record RehearsalRequest(string BackupId, string TargetDatabase, string Reason, string ConfirmationText, OperatorIdentity Operator, IReadOnlyList<string> AcceptedWarningCodes);

public sealed record RehearsalResult(string BackupId, string TargetDatabase, string Outcome, RestoreResult? Restore, UpgradeResult? Upgrade, string? Failure, string? EvidenceFile, Guid RunId)
{
    public bool Succeeded => Outcome is "Succeeded" or "SucceededWithWarnings";
}

/// <summary>
/// Rehearses an upgrade without touching the installation's data (docs/UPGRADES.md, "Rehearsal"): the verified backup is restored into a new database beside the running installation, the copy is brought into maintenance
/// mode, upgraded by exactly the procedure that would run for real, validated, and left in place for inspection. The active database is never the target.
/// </summary>
public interface IUpgradeRehearsal
{
    Task<RehearsalResult> RehearseAsync(RehearsalRequest request, CancellationToken ct = default);
}

internal sealed class UpgradeRehearsal(IRestoreService restores, UpgradeService upgrades, IRestoreTargetFactory targets, IDatabaseBackupProvider database, IOptions<RecoveryOptions> recovery, IOperationHistory history, OperationAudit audit,
    IClock clock, ILogger<UpgradeRehearsal> log) : IUpgradeRehearsal
{
    public async Task<RehearsalResult> RehearseAsync(RehearsalRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new DomainException("reason_required", "A reason is required to rehearse an upgrade.");
        var reason = Redaction.Clean(request.Reason.Trim());
        if (!string.Equals(request.ConfirmationText?.Trim(), RehearsalPhrases.For(request.BackupId), StringComparison.Ordinal))
            throw new DomainException("confirmation_required", $"Type exactly “{RehearsalPhrases.For(request.BackupId)}” to confirm the rehearsal.");
        if (!recovery.Value.RestoreConfigured) throw new DomainException("restore_not_configured", "No restore target directory is configured (Ecp:Recovery:RestoreTargetDirectory), so a rehearsal cannot be restored.");

        var run = await history.StartAsync(OperationKinds.UpgradeRehearsal, request.TargetDatabase, reason, request.Operator, ct);
        await audit.RecordAsync(new AuditEntry("operations.upgrade.rehearsal.requested", "Upgrade", request.BackupId, Reason: reason, NewValuesJson: JsonSerializer.Serialize(new { backupId = request.BackupId, target = request.TargetDatabase, @operator = request.Operator.Id })), ct);
        RestoreResult? restore = null; UpgradeResult? upgrade = null; string? failure = null;
        try
        {
            // 1. Restore the backup into a new database beside the running installation (its own checks, leases, maintenance and validation apply).
            restore = await restores.RestoreAsync(new RestoreRequest(request.BackupId, request.TargetDatabase, reason, RestorePhrases.For(request.BackupId, false), request.Operator), ct);
            if (!restore.Succeeded) throw new DomainException("rehearsal_restore_failed", "The restore for the rehearsal did not succeed: " + (restore.Failure ?? restore.Outcome));

            // 2. Open the copy with the same composition and run the real procedure on it.
            var (documents, packs) = RestoreLayout.For(recovery.Value, request.TargetDatabase);
            var connectionString = database.ConnectionStringFor(request.TargetDatabase);
            await using var host = targets.Open(new RestoreTarget(request.TargetDatabase, connectionString, documents, packs, InPlace: false));
            var target = new UpgradeTarget(host.Services, connectionString, request.TargetDatabase, IsActive: false, Label: $"rehearsal copy {request.TargetDatabase}");
            await using (var scope = host.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().EnableAsync($"Upgrade rehearsal from {request.BackupId}", request.Operator, MaintenanceCauses.Manual, ct);
            upgrade = await upgrades.ExecuteAsync(target, new UpgradeRequest(request.BackupId, reason, "", request.Operator, request.AcceptedWarningCodes), OperationKinds.UpgradeRehearsal, confirmationChecked: true, ct);
            if (!upgrade.Succeeded) failure = "The rehearsal upgrade did not succeed: " + (upgrade.Failure ?? upgrade.Outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = Redaction.Clean(ex is DomainException or IOException ? ex.Message : "The rehearsal failed unexpectedly (" + ex.GetType().Name + "). See the application log with the correlation id.");
            log.LogError(ex, "Upgrade rehearsal from {BackupId} into {Target} failed.", request.BackupId, request.TargetDatabase);
        }

        var outcome = failure is not null ? "Failed" : upgrade is { Outcome: "Succeeded" } ? "Succeeded" : "SucceededWithWarnings";
        var summary = outcome == "Failed" ? $"Rehearsal failed: {failure}" : $"Rehearsal of the upgrade on {request.TargetDatabase} validated ({upgrade?.Validation?.Outcome}).";
        var detail = JsonSerializer.Serialize(new { backupId = request.BackupId, target = request.TargetDatabase, restore = restore?.Outcome, upgrade = upgrade?.Outcome, evidence = upgrade?.EvidenceFile, restoreEvidence = restore?.EvidenceFile });
        await history.FinishAsync(run, outcome == "Failed" ? OperationStatuses.Failed : outcome == "Succeeded" ? OperationStatuses.Completed : OperationStatuses.CompletedWithWarnings, summary, detail, CancellationToken.None);
        await audit.RecordAsync(new AuditEntry(outcome == "Failed" ? "operations.upgrade.rehearsal.failed" : "operations.upgrade.rehearsal.recorded", "Upgrade", request.BackupId, outcome == "Failed" ? AuditOutcome.Failed : AuditOutcome.Success, Reason: reason, NewValuesJson: detail), CancellationToken.None);
        _ = clock;
        return new RehearsalResult(request.BackupId, request.TargetDatabase, outcome, restore, upgrade, failure, upgrade?.EvidenceFile, run);
    }
}

public sealed record RollbackEvidenceRequest(string BackupId, string TargetDatabase, string Reason, string ConfirmationText, OperatorIdentity Operator);

public sealed record RollbackEvidenceResult(string BackupId, string TargetDatabase, string Outcome, RestoreResult? Restore, IReadOnlyList<ValidationCheck> Checks, long EventsDiscarded, string RequiredApplicationVersion, string RunningApplicationVersion,
    string? Failure, string? EvidenceFile, Guid RunId, DateTime StartedUtc, DateTime CompletedUtc, string CorrelationId)
{
    public bool Succeeded => Outcome is "Succeeded" or "SucceededWithWarnings";
}

/// <summary>
/// Proves that rolling back works (docs/ROLLBACK_BY_RESTORE.md): the verified pre-upgrade backup is restored into a clean database beside the installation and shown to be the pre-upgrade state, intact. It states plainly that a
/// rollback discards what was created after the backup, and it never runs an older application against an upgraded database, nor offers to.
/// </summary>
public interface IRollbackEvidence
{
    Task<RollbackEvidenceResult> ProveAsync(RollbackEvidenceRequest request, CancellationToken ct = default);
}

internal sealed class RollbackEvidenceService(IRestoreService restores, IBackupVerifier verifier, IRestoreTargetFactory targets, IDatabaseBackupProvider database, IInstallationInventory inventory, UpgradeCatalogue catalogue,
    IOptions<RecoveryOptions> recovery, DatabaseOptions databaseOptions, IServiceProvider root, IOperationHistory history, OperationAudit audit, EvidenceWriter evidence, IClock clock, ILogger<RollbackEvidenceService> log) : IRollbackEvidence
{
    public async Task<RollbackEvidenceResult> ProveAsync(RollbackEvidenceRequest request, CancellationToken ct = default)
    {
        var started = clock.UtcNow;
        var correlation = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new DomainException("reason_required", "A reason is required to prove a rollback.");
        var reason = Redaction.Clean(request.Reason.Trim());
        if (!string.Equals(request.ConfirmationText?.Trim(), RollbackPhrases.For(request.BackupId), StringComparison.Ordinal))
            throw new DomainException("confirmation_required", $"Type exactly “{RollbackPhrases.For(request.BackupId)}” to confirm.");
        if (!recovery.Value.RestoreConfigured) throw new DomainException("restore_not_configured", "No restore target directory is configured (Ecp:Recovery:RestoreTargetDirectory), so rollback cannot be proved.");

        var run = await history.StartAsync(OperationKinds.RollbackEvidence, request.BackupId, reason, request.Operator, ct);
        await audit.RecordAsync(new AuditEntry("operations.rollback.evidence.requested", "Rollback", request.BackupId, Reason: reason, NewValuesJson: JsonSerializer.Serialize(new { backupId = request.BackupId, target = request.TargetDatabase, @operator = request.Operator.Id })), ct);
        var checks = new List<ValidationCheck>();
        ValidationCheck Make(string code, string title, CheckOutcome o, CheckSeverity sev, string d, string detail = "", string fix = "") => new(code, title, o, sev, d, Redaction.Clean(detail), fix, correlation);
        RestoreResult? restore = null; string? failure = null; long discarded = 0;
        var running = UpgradeVersions.Current; var required = running;
        try
        {
            var manifest = await verifier.LoadManifestAsync(request.BackupId, ct);
            // The version a rollback returns to is the version whose schema the backup holds, not the text of the build that happened to write it.
            required = UpgradeBackups.SchemaVersionOf(manifest, catalogue, UpgradePreflight.Compiled(root.GetServices<ModuleDescriptor>()), running) ?? manifest.PlatformVersion.Split('+')[0];

            // 1. Restore the backup into a clean database: the same, fully checked procedure as any restore.
            restore = await restores.RestoreAsync(new RestoreRequest(request.BackupId, request.TargetDatabase, reason, RestorePhrases.For(request.BackupId, false), request.Operator), ct);
            checks.Add(restore.Succeeded
                ? Make("RBK-001", "Restore of the pre-upgrade backup", CheckOutcome.Passed, CheckSeverity.Info, $"The verified backup restored into {request.TargetDatabase} and passed post-restore validation ({restore.Validation?.Outcome}).", restore.EvidenceFile ?? "")
                : Make("RBK-001", "Restore of the pre-upgrade backup", CheckOutcome.Failed, CheckSeverity.Critical, "The backup did not restore and validate, so it cannot be used for rollback.", restore.Failure ?? restore.Outcome, "Take a new verified backup before relying on rollback."));
            if (restore.Succeeded)
            {
                var (documents, packs) = RestoreLayout.For(recovery.Value, request.TargetDatabase);
                var connectionString = database.ConnectionStringFor(request.TargetDatabase);
                await using var host = targets.Open(new RestoreTarget(request.TargetDatabase, connectionString, documents, packs, InPlace: false));
                var report = restore.Validation;
                checks.Add(report?.Checks.FirstOrDefault(c => c.Code == "VAL-001")?.Outcome == CheckOutcome.Passed
                    ? Make("RBK-002", "The restored installation starts", CheckOutcome.Passed, CheckSeverity.Info, "The restored copy opened and answered queries. (It was opened in restricted validation mode by this build; the matching application version is named below.)")
                    : Make("RBK-002", "The restored installation starts", CheckOutcome.Failed, CheckSeverity.Critical, "The restored copy did not open."));

                // 2. The restored schema is the pre-upgrade state: exactly what the backup recorded, and exactly a released baseline when one is known.
                var applied = await UpgradeScopes.AppliedAsync(host.Services, ct);
                var recorded = manifest.Schema.Modules.ToDictionary(m => m.Module, m => (IReadOnlyList<string>)m.AppliedMigrations.ToList(), StringComparer.Ordinal);
                var sameAsBackup = recorded.All(kv => applied.TryGetValue(kv.Key, out var now) && now.SequenceEqual(kv.Value)) && applied.All(kv => kv.Value.Count == 0 || recorded.ContainsKey(kv.Key));
                var baseline = catalogue.Baseline(required);
                var baselineMatch = baseline is null ? (bool?)null : baseline.Modules.All(m => applied.TryGetValue(m.Module, out var now) && now.SequenceEqual(m.Migrations.Select(x => x.Id)));
                checks.Add(sameAsBackup && baselineMatch != false
                    ? Make("RBK-003", "Schema is the pre-upgrade schema", CheckOutcome.Passed, CheckSeverity.Info, baselineMatch == true ? $"Every module is exactly at the schema of platform {required}, as recorded in the backup." : "Every module is exactly at the schema the backup recorded.", baselineMatch is null ? $"No frozen baseline for {required} is known to this build." : "")
                    : Make("RBK-003", "Schema is the pre-upgrade schema", CheckOutcome.Failed, CheckSeverity.Critical, "The restored schema is not the schema the backup recorded.", "", "Do not use this backup for rollback."));

                // 3. Nothing of the target upgrade is presented as part of the pre-upgrade state.
                var target = catalogue.ManifestTo(running);
                var upgradeMigrations = target?.NewMigrations.Select(n => n.Module + "/" + n.Id).ToHashSet(StringComparer.Ordinal) ?? [];
                var leaked = applied.SelectMany(kv => kv.Value.Select(v => kv.Key + "/" + v)).Where(upgradeMigrations.Contains).ToList();
                await using (var scope = host.Services.CreateAsyncScope())
                {
                    var records = await scope.ServiceProvider.GetRequiredService<IMigrationRecords>().ListAsync(ct);
                    var applying = records?.Count(r => r.Origin == "applied" && r.PlatformVersion == running) ?? 0;
                    var runs = (await scope.ServiceProvider.GetRequiredService<IOperationHistory>().ListAsync(OperationKinds.Upgrade, 20, ct)).Count(r => r.Status is OperationStatuses.Completed or OperationStatuses.CompletedWithWarnings);
                    checks.Add(string.Equals(required, running, StringComparison.Ordinal)
                        ? Make("RBK-004", "No upgrade data in the rolled-back state", CheckOutcome.Passed, CheckSeverity.Info, "The backup was made by the version running now, so no upgrade lies between the backup and this build.")
                        : leaked.Count == 0 && applying == 0 && runs == 0
                        ? Make("RBK-004", "No upgrade data in the rolled-back state", CheckOutcome.Passed, CheckSeverity.Info, "The restored copy holds none of the target version's migrations, migration records or upgrade history: nothing created by the upgrade is presented as pre-upgrade state.")
                        : Make("RBK-004", "No upgrade data in the rolled-back state", CheckOutcome.Failed, CheckSeverity.Critical, "The restored copy contains traces of the upgrade, so the backup was not taken before it.", $"migrations: {string.Join(", ", leaked.Take(5))}; records: {applying}; upgrade runs: {runs}", "Name a backup taken before the upgrade."));
                }

                // 4. The three kinds of evidence a rollback is judged by, taken from the ordinary post-restore validation.
                foreach (var (code, title, valCodes) in new[] { ("RBK-005", "Audit-chain integrity", new[] { "VAL-009" }), ("RBK-006", "Documents", new[] { "VAL-010", "VAL-015" }), ("RBK-007", "Solution packs and configuration", new[] { "VAL-011", "VAL-013", "VAL-014", "VAL-019" }) })
                {
                    var mapped = report?.Checks.Where(c => valCodes.Contains(c.Code)).ToList() ?? [];
                    checks.Add(mapped.Count > 0 && mapped.All(c => c.Outcome is CheckOutcome.Passed or CheckOutcome.Warning)
                        ? Make(code, title, CheckOutcome.Passed, CheckSeverity.Info, $"{title} verified on the restored copy ({string.Join(", ", mapped.Select(c => c.Code))}).")
                        : Make(code, title, CheckOutcome.Failed, CheckSeverity.Critical, $"{title} could not be verified on the restored copy.", string.Join("; ", mapped.Where(c => c.Outcome != CheckOutcome.Passed).Select(c => c.Code + " " + c.Description)), "Do not use this backup for rollback."));
                }

                // 5. The businesses configured in the installation are the ones in the backup: organisations, their assigned packs and regional settings.
                var restored = await inventory.CollectAsync(host.Services, tableChecksums: false, ct);
                var differences = new List<string>();
                foreach (var then in manifest.InventoryAfter.Tenants.Where(t => t.TenantId != SystemTenant.Id))
                {
                    var now = restored.Tenants.FirstOrDefault(t => t.TenantId == then.TenantId);
                    if (now is null) { differences.Add($"tenant {then.Name}: missing"); continue; }
                    if (!then.Organisations.OrderBy(o => o.Id).SequenceEqual(now.Organisations.OrderBy(o => o.Id))) differences.Add($"tenant {then.Name}: organisations, packs or regional settings differ");
                    if (then.AuditHeadSequence != now.AuditHeadSequence || then.AuditHeadHash != now.AuditHeadHash) differences.Add($"tenant {then.Name}: audit chain head differs");
                }
                var orgCount = manifest.InventoryAfter.Tenants.Where(t => t.TenantId != SystemTenant.Id).Sum(t => t.Organisations.Count);
                checks.Add(differences.Count == 0
                    ? Make("RBK-008", "Organisations and their packs", CheckOutcome.Passed, CheckSeverity.Info, $"All {orgCount} organisation(s) are present with the packs and regional settings the backup recorded, and each audit chain ends where it ended at the backup.")
                    : Make("RBK-008", "Organisations and their packs", CheckOutcome.Failed, CheckSeverity.Critical, "The restored organisations or their history differ from the backup.", string.Join("; ", differences.Take(5)), "Do not use this backup for rollback."));

                // 6. The data-loss window: what a rollback to this backup would discard, measured against the installation as it is now.
                var current = await inventory.CollectAsync(root, tableChecksums: false, ct);
                foreach (var then in manifest.InventoryAfter.Tenants)
                    discarded += Math.Max(0, (current.Tenants.FirstOrDefault(t => t.TenantId == then.TenantId)?.AuditHeadSequence ?? then.AuditHeadSequence) - then.AuditHeadSequence);
                foreach (var extra in current.Tenants.Where(t => manifest.InventoryAfter.Tenants.All(b => b.TenantId != t.TenantId))) discarded += extra.AuditEvents;
                checks.Add(Make("RBK-009", "Data-loss window", discarded == 0 ? CheckOutcome.Passed : CheckOutcome.Warning, discarded == 0 ? CheckSeverity.Info : CheckSeverity.Medium,
                    discarded == 0 ? "Nothing has been recorded since the backup, so a rollback to it loses nothing."
                    : $"Rolling back discards everything created after this backup: {discarded} audited event(s) so far, and the data they describe. They are not in the backup and cannot be recovered from it.",
                    "", "Decide with the owner whether that work can be redone, and tell the people affected before rolling back."));
            }
            checks.Add(string.Equals(required, running, StringComparison.Ordinal)
                ? Make("RBK-010", "Application version", CheckOutcome.Passed, CheckSeverity.Info, $"The backup was made by platform {required}, the version running now.")
                : Make("RBK-010", "Application version", CheckOutcome.Warning, CheckSeverity.Medium, $"A rollback needs the application of the backup's own version, platform {required}. This build is {running} and opened the restored copy only in restricted validation mode.",
                    "", $"Deploy platform {required} (the build that made the backup) against the restored database. This tool does not run an older application against an upgraded database, and never downgrades one."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = Redaction.Clean(ex is DomainException or IOException ? ex.Message : "The rollback evidence failed unexpectedly (" + ex.GetType().Name + "). See the application log with the correlation id.");
            log.LogError(ex, "Rollback evidence for {BackupId} into {Target} failed.", request.BackupId, request.TargetDatabase);
        }
        var completed = clock.UtcNow;
        var outcome = failure is not null || checks.Any(c => c.Outcome == CheckOutcome.Failed) ? "Failed" : checks.Any(c => c.Outcome is CheckOutcome.Warning or CheckOutcome.NotVerified) ? "SucceededWithWarnings" : "Succeeded";
        var document = new
        {
            kind = "rollback-evidence", backupId = request.BackupId, target = request.TargetDatabase, @operator = Redaction.Clean(request.Operator.Id), reason, startedUtc = started, completedUtc = completed, outcome, failure = Redaction.Clean(failure), correlationId = correlation,
            requiredApplicationVersion = required, runningApplicationVersion = running, auditedEventsDiscardedByRollback = discarded,
            restore = restore is null ? null : new { restore.Outcome, validation = restore.Validation?.Outcome.ToString(), restore.EvidenceFile },
            checks = checks.Select(c => new { c.Code, c.Title, outcome = c.Outcome.ToString(), severity = c.Severity.ToString(), c.Description, detail = Redaction.Clean(c.TechnicalDetail), c.Remediation }),
            statement = "Rollback is restore of the verified pre-upgrade backup plus the matching application version. It discards everything created after the backup. This tool never runs an older application against an upgraded database.",
            notice = UpgradeEvidence.Notice,
        };
        var file = await evidence.WriteDocumentAsync($"rollback-{started:yyyyMMddTHHmmss}Z-{request.BackupId}.json", document, CancellationToken.None);
        var summary = outcome == "Failed" ? $"Rollback evidence failed: {failure ?? "a check failed"}" : $"Rollback to {request.BackupId} proved on {request.TargetDatabase} ({discarded} audited event(s) would be discarded).";
        await history.FinishAsync(run, outcome == "Failed" ? OperationStatuses.Failed : outcome == "Succeeded" ? OperationStatuses.Completed : OperationStatuses.CompletedWithWarnings, summary,
            JsonSerializer.Serialize(new { backupId = request.BackupId, target = request.TargetDatabase, checks = checks.Select(c => new { c.Code, outcome = c.Outcome.ToString() }), discarded, evidence = file }), CancellationToken.None);
        await audit.RecordAsync(new AuditEntry(outcome == "Failed" ? "operations.rollback.evidence.failed" : "operations.rollback.evidence.recorded", "Rollback", request.BackupId, outcome == "Failed" ? AuditOutcome.Failed : AuditOutcome.Success, Reason: reason,
            NewValuesJson: JsonSerializer.Serialize(new { backupId = request.BackupId, outcome, discarded, evidence = file })), CancellationToken.None);
        _ = databaseOptions;
        return new RollbackEvidenceResult(request.BackupId, request.TargetDatabase, outcome, restore, checks.OrderBy(c => c.Code, StringComparer.Ordinal).ToList(), discarded, required, running, failure, file, run, started, completed, correlation);
    }
}
