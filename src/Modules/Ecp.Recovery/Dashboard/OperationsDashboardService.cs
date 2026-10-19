using System.Reflection;
using System.Text.Json;
using Ecp.Identity;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

/// <summary>
/// The operator's view of the whole installation (docs/OPERATIONS_DASHBOARD.md). It only <b>reads</b> and it only reuses what exists: the readiness service, the operation history, the leases, the backup catalogue, the
/// migration inventory and the modules' own operational probes. It judges each fact with one set of rules (below) so a page never has to. Reading needs <c>operations.read</c> in the installation tenant; technical
/// detail needs <c>operations.validate</c>. Nothing here changes data, so nothing here needs a recent sign-in.
/// </summary>
public interface IOperationsDashboard
{
    Task<OperationsDashboard> BuildAsync(CancellationToken ct = default);
}

internal sealed class OperationsDashboardService(
    IAccessService access, ITenantContext tenant, IClock clock, IActorContext actor, IServiceProvider root, ReadinessService readiness, IOperationHistory history, IOperationLeases leases,
    IMaintenanceService maintenance, IBackupCatalogue backups, IDatabaseBackupProvider database, IOptions<RecoveryOptions> recovery, StartupFacts facts, IOperationsAdministration administration,
    IEnumerable<IKeyRingInspector> keyRings, OperationalFactCollector collector, ILogger<OperationsDashboardService> log, GovernanceMode? governance = null) : IOperationsDashboard
{
    private static readonly TimeSpan StaleLeaseGrace = TimeSpan.FromMinutes(5);

    public async Task<OperationsDashboard> BuildAsync(CancellationToken ct = default)
    {
        if (tenant.TenantId != SystemTenant.Id || !await access.HoldsAnywhereAsync(OperationsPermissions.Read)) throw new ForbiddenException("You do not have permission to do this.");
        var detail = await access.HoldsAnywhereAsync(OperationsPermissions.Validate);
        var now = clock.UtcNow;
        var correlation = actor.CorrelationId;
        string D(string text) => detail ? Redaction.Clean(text) : "";

        var report = await Safe(async () => (ReadinessReport?)await readiness.RunAsync(ct));
        var maint = await maintenance.GetAsync(ct);
        var leaseList = await leases.ListAsync(ct);
        var runs = await history.ListAsync(null, 200, ct);
        var backupList = database.Supported || recovery.Value.BackupConfigured ? await Safe(async () => (IReadOnlyList<BackupSummary>?)await backups.ListAsync(ct)) ?? [] : [];
        var facts_ = await CollectFactsAsync(now, ct);
        var lastValidation = await Safe(() => administration.LastValidationAsync(ct));
        var schema = await ReadSchemaAsync(ct);

        var panels = new List<DashboardPanel>
        {
            HealthPanel(report, maint, D),
            ReadinessPanel(report, facts_, maint, now, out var coverage),
            VersionsPanel(schema, D),
            UpgradePanel(schema, runs, facts_, maint, D),
            RecoveryPanel(backupList, runs, lastValidation, now, D),
            JobsPanel(facts_, leaseList, runs, maint, now, D),
            OutboxPanel(facts_, maint, now, D),
            ConfigurationPanel(facts_, D),
            IntegrityPanel(lastValidation, report, D),
            CertificatesPanel(facts_, now, D),
        };
        var failures = Failures(runs, leaseList, now, D);
        var overall = OpsStatuses.Worst(panels.Select(p => p.Status));

        foreach (var s in Enum.GetValues<OpsStatus>()) OperationalTelemetry.SetGauge("health", s.ToString(), panels.Count(p => p.Status == s));
        foreach (var key in new[] { "Pending", "Failed", "DeadLetter" }) OperationalTelemetry.SetGauge("outbox", key, Sum(facts_, "Notifications", "Outbox." + key));
        foreach (var key in new[] { "Running", "StaleRunning", "Failed24h", "Succeeded24h" }) OperationalTelemetry.SetGauge("jobs", key, Sum(facts_, "Notifications", "Jobs." + key));
        OperationalTelemetry.SetGauge("leases", "Active", leaseList.Count(l => l.IsActive));
        OperationalTelemetry.SetGauge("leases", "ExpiredUnreleased", leaseList.Count(l => !l.IsActive && l.ReleasedUtc is null));
        OperationalLog.DashboardBuilt(log, OpsStatuses.Label(overall), panels.Count(p => p.Status == OpsStatus.Failed), panels.Count(p => p.Status == OpsStatus.NotVerified), correlation);

        return new OperationsDashboard(now, correlation, overall, UpgradeVersions.Current, facts.Mode.ToString(), database.Engine, maint, panels, failures, coverage, leaseList, schema.Select(m => m.Version).ToList(), detail,
            OperationalTelemetry.GaugeSnapshot(), governance is null ? "" : $"{governance.Name}: {governance.Label}");
    }

    private static async Task<T?> Safe<T>(Func<Task<T?>> work) where T : class
    {
        try { return await work(); }
        catch (Exception ex) when (ex is not OperationCanceledException and not ForbiddenException) { return null; }
    }

    // ---- facts from the modules' probes: counts and times only, per tenant, in that tenant's scope (OperationalFactCollector) ---------------------------------------------------------------------------------------

    private Task<IReadOnlyList<FactTotal>?> CollectFactsAsync(DateTime now, CancellationToken ct) => collector.CollectAsync(now, ct);

    private static long Sum(IReadOnlyList<FactTotal>? facts, string area, string key) => facts?.FirstOrDefault(f => f.Area == area && f.Key == key)?.Value ?? 0;
    private static DateTime? When(IReadOnlyList<FactTotal>? facts, string area, string key) => facts?.FirstOrDefault(f => f.Area == area && f.Key == key)?.Utc;
    private static bool Has(IReadOnlyList<FactTotal>? facts, string area, string key) => facts?.Any(f => f.Area == area && f.Key == key) == true;

    private static string Ago(DateTime now, DateTime then)
    {
        var span = now - then;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} minute(s) ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} hour(s) ago" : $"{(int)span.TotalDays} day(s) ago";
    }

    // ---- panels ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static OpsStatus Map(ReadinessStatus s) => s switch { ReadinessStatus.Healthy => OpsStatus.Verified, ReadinessStatus.Degraded => OpsStatus.Warning, _ => OpsStatus.Failed };

    private static DashboardPanel Panel(string key, string title, string summary, List<DashboardItem> items) => new(key, title, summary, OpsStatuses.Worst(items.Select(i => i.Status)), items);

    private static OpsStatus MaintenanceStatus(MaintenanceStatus m)
        => !m.Enabled ? OpsStatus.Verified : m.Cause is MaintenanceCauses.RestoreFailed or MaintenanceCauses.UpgradeFailed ? OpsStatus.Failed : OpsStatus.Warning;

    private DashboardPanel HealthPanel(ReadinessReport? report, MaintenanceStatus m, Func<string, string> D)
    {
        var items = new List<DashboardItem>();
        if (report is null) items.Add(new("HLT-001", "Platform readiness", OpsStatus.NotVerified, "The readiness checks could not be run, so the platform's health is not known.", NextStep: "Look at the application log with the correlation id of this page."));
        else
        {
            var counts = report.Results.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
            items.Add(new("HLT-001", "Platform readiness", Map(report.Overall), $"{counts.GetValueOrDefault(ReadinessStatus.Healthy)} healthy, {counts.GetValueOrDefault(ReadinessStatus.Degraded)} degraded, {counts.GetValueOrDefault(ReadinessStatus.Unhealthy)} unhealthy of {report.Results.Count} checks that run on every start and on request."));
            foreach (var r in report.Results.OrderBy(r => r.Id, StringComparer.Ordinal))
                items.Add(new(r.Id, r.Description, Map(r.Status), r.Status == ReadinessStatus.Healthy ? "The check passed." : "The check did not pass. See the message.", D(r.Message)));
        }
        items.Add(new("HLT-002", "Maintenance mode", MaintenanceStatus(m), !m.Enabled ? "The installation accepts changes." : m.Cause switch
        {
            MaintenanceCauses.UpgradeFailed => "An upgrade failed. Changes stay refused until the operator rolls back by restore or acknowledges the state.",
            MaintenanceCauses.RestoreFailed => "A restore failed. Changes stay refused until the operator acknowledges the restore state.",
            _ => "Changes are refused and background work takes nothing new.",
        }, D(m.Enabled ? $"Cause {m.Cause}; since {m.SinceUtc:u}; set by {m.ChangedBy}. Reason: {m.Reason}" : ""), m.SinceUtc, NextStep: m.Enabled ? "Review the maintenance page." : null, Link: "/Admin/Operations/Maintenance"));
        return Panel("health", "Installation health", "Whether the platform is up, reachable and current.", items);
    }

    private static readonly Dictionary<string, string> Alias = new(StringComparer.Ordinal) { ["R-BKP-01"] = "R-DB-04" };

    private DashboardPanel ReadinessPanel(ReadinessReport? report, IReadOnlyList<FactTotal>? facts_, MaintenanceStatus m, DateTime now, out CatalogueCoverage coverage)
    {
        var byId = new Dictionary<string, ReadinessResult>(StringComparer.Ordinal);
        foreach (var r in report?.Results ?? []) byId[Alias.GetValueOrDefault(r.Id, r.Id)] = r;
        var keyRing = keyRings.FirstOrDefault()?.Inspect(now);
        int here = 0, elsewhere = 0, notBuilt = 0;
        var items = new List<DashboardItem>();
        foreach (var e in ReadinessCatalogue.Entries)
        {
            string title = $"{e.Title} (severity {e.Severity}{(e.Startup ? ", checked at start-up" : "")})";
            if (byId.TryGetValue(e.Id, out var r)) { here++; items.Add(new(e.Id, title, Map(r.Status), r.Status == ReadinessStatus.Healthy ? "The check passed." : "The check did not pass.", r.Message)); continue; }
            switch (e.Id)
            {
                case "R-OPS-03":
                    here++;
                    var stale = Sum(facts_, "Notifications", "Jobs.StaleRunning"); var dead = Sum(facts_, "Notifications", "Outbox.DeadLetter"); var ran = Has(facts_, "Notifications", "Jobs.LastStartedUtc") && When(facts_, "Notifications", "Jobs.LastStartedUtc") is not null;
                    items.Add(new(e.Id, title, facts_ is null ? OpsStatus.NotVerified : stale > 0 ? OpsStatus.Failed : !ran ? OpsStatus.NotVerified : dead > 0 ? OpsStatus.Warning : OpsStatus.Verified,
                        facts_ is null ? "Job and outbox facts could not be read." : stale > 0 ? "A job run is recorded as running but has not finished for more than an hour." : !ran ? "No background job has run yet, or jobs are turned off." : dead > 0 ? "Some messages are dead-lettered." : "Jobs are running and the outbox is draining.", Link: "#jobs"));
                    break;
                case "R-OPS-04":
                    here++;
                    items.Add(new(e.Id, title, m.Enabled ? OpsStatus.Warning : OpsStatus.NotVerified, m.Enabled ? "Maintenance mode is on." : "Maintenance mode is off. Clock skew is not measured by the platform, so this check stays Not Verified.", Link: "/Admin/Operations/Maintenance"));
                    break;
                case "R-SEC-04":
                    here++;
                    items.Add(new(e.Id, title, keyRing is null ? OpsStatus.NotVerified : !keyRing.Readable ? OpsStatus.Failed : keyRing.UsableKeys > 0 ? OpsStatus.Verified : OpsStatus.Failed,
                        keyRing is null ? "This host does not expose its key ring." : keyRing.UsableKeys > 0 ? "The key ring holds a usable key. (That it survives a restart is shown by sessions surviving restarts; it is not re-proved here.)" : "No usable key.", keyRing?.Note ?? ""));
                    break;
                default:
                    if (ReadinessCatalogue.RunElsewhere.Contains(e.Id)) { elsewhere++; items.Add(new(e.Id, title, OpsStatus.NotVerified, "This check exists in the setup wizard or as a start-up guard but is not run from the dashboard. Its result is not shown here (SET-03).")); }
                    else { notBuilt++; items.Add(new(e.Id, title, OpsStatus.NotVerified, "This check is in the catalogue but has not been built (SET-03).")); }
                    break;
            }
        }
        coverage = new CatalogueCoverage(ReadinessCatalogue.Entries.Count, here, elsewhere, notBuilt);
        return Panel("readiness", "Readiness catalogue", $"{here} of {ReadinessCatalogue.Entries.Count} catalogue checks run here, {elsewhere} run in the setup wizard or at start-up only, {notBuilt} are not built. Those that do not run are shown as Not Verified, never as passed.", items);
    }

    private sealed record SchemaRow(ModuleVersion Version, bool Known);

    private async Task<List<SchemaRow>> ReadSchemaAsync(CancellationToken ct)
    {
        var rows = new List<SchemaRow>();
        if (facts.Provider != DatabaseProvider.SqlServer) return rows;          // applied migrations are read on SQL Server only; elsewhere the panels say Not Verified
        try
        {
            await using var scope = root.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
            var compiled = UpgradePreflight.Compiled(scope.ServiceProvider.GetServices<ModuleDescriptor>());
            var applied = (await SchemaInspector.ReadAsync(scope.ServiceProvider, ct)).Modules.ToDictionary(m => m.Module, m => m.AppliedMigrations, StringComparer.Ordinal);
            foreach (var module in compiled.Modules)
            {
                var have = applied.GetValueOrDefault(module.Module) ?? [];
                var last = have.LastOrDefault();
                rows.Add(new(new ModuleVersion(module.Module, last ?? "(none)", module.SchemaVersion, module.Migrations.Count(mig => !have.Contains(mig.Id))), have.Count > 0));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* reported as Not Verified by the panel */ }
        return rows;
    }

    private DashboardPanel VersionsPanel(List<SchemaRow> schema, Func<string, string> D)
    {
        var items = new List<DashboardItem>
        {
            new("VER-001", "Platform version", OpsStatus.Verified, $"This build is platform {UpgradeVersions.Current}, running in {facts.Mode} mode on {database.Engine}.", D(string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Ecp.Host").Select(a => a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")))),
        };
        if (schema.Count == 0) items.Add(new("VER-002", "Schema versions", OpsStatus.NotVerified, "The applied migrations could not be read here (they are read on SQL Server only), so the schema versions are not known."));
        else
        {
            var pending = schema.Sum(s => s.Version.Pending);
            items.Add(new("VER-002", "Schema versions", pending == 0 ? OpsStatus.Verified : OpsStatus.Failed, pending == 0 ? $"All {schema.Count} modules are at the schema version this build expects." : $"{pending} migration(s) are pending. The platform will not run correctly until the upgrade procedure has been followed.",
                D(string.Join("; ", schema.Where(s => s.Version.Pending > 0).Select(s => $"{s.Version.Module}: {s.Version.Pending} pending"))), NextStep: pending == 0 ? null : "Run the upgrade pre-flight.", Link: "/Admin/Operations/MigrationReport"));
        }
        return Panel("versions", "Platform and schema versions", "What is installed, and whether the database matches it.", items);
    }

    private DashboardPanel UpgradePanel(List<SchemaRow> schema, IReadOnlyList<OperationRunDto> runs, IReadOnlyList<FactTotal>? facts_, MaintenanceStatus m, Func<string, string> D)
    {
        var items = new List<DashboardItem>();
        if (schema.Count == 0) items.Add(new("UPR-001", "Upgrade readiness", OpsStatus.NotVerified, "Upgrade state is judged on SQL Server only. Run the pre-flight on a SQL Server installation.", Link: "/Admin/Operations/UpgradeReadiness"));
        else
        {
            var pending = schema.Sum(s => s.Version.Pending);
            var ahead = schema.Any(s => !s.Known && s.Version.Applied != "(none)");
            items.Add(new("UPR-001", "Upgrade readiness", pending == 0 ? OpsStatus.Verified : OpsStatus.Warning, pending == 0 ? "Nothing to upgrade: every migration this build knows is applied. (A full pre-flight is run on request, not on every page load.)" : "Migrations are pending. Run the pre-flight before anything else.", NextStep: pending == 0 ? null : "Run the pre-flight, rehearse on a restored copy, then upgrade.", Link: "/Admin/Operations/UpgradeReadiness"));
        }
        foreach (var (code, title, kind) in new[] { ("UPR-002", "Last upgrade", OperationKinds.Upgrade), ("UPR-003", "Last upgrade rehearsal", OperationKinds.UpgradeRehearsal), ("UPR-004", "Last rollback evidence", OperationKinds.RollbackEvidence) })
        {
            var run = runs.FirstOrDefault(r => r.Kind == kind);
            items.Add(run is null ? new(code, title, OpsStatus.NotVerified, "None recorded.", Link: "/Admin/Operations/UpgradeHistory") : RunItem(code, title, run, D, "/Admin/Operations/UpgradeHistory"));
        }
        if (m.Cause == MaintenanceCauses.UpgradeFailed && m.Enabled) items.Add(new("UPR-005", "Failed upgrade", OpsStatus.Failed, "An upgrade failed and maintenance mode is kept on. Migrations are never retried automatically. Roll back by restoring the verified pre-upgrade backup.", NextStep: "Follow the rollback-by-restore procedure.", Link: "/Admin/Operations/RollbackEvidence"));
        var committing = Sum(facts_, "DataMigration", "Batches.Committing");
        items.Add(new("UPR-006", "Data-migration batches in progress", facts_ is null ? OpsStatus.NotVerified : committing > 0 ? OpsStatus.Warning : OpsStatus.Verified,
            facts_ is null ? "Batch facts could not be read." : committing > 0 ? $"{committing} batch(es) are committing. An upgrade is refused until they finish." : "No batch is committing.", D($"Staged: {Sum(facts_, "DataMigration", "Batches.Staged")}")));
        return Panel("upgrade", "Upgrade readiness and migrations", "Pending or failed migrations and the evidence of upgrades.", items);
    }

    private static DashboardItem RunItem(string code, string title, OperationRunDto run, Func<string, string> D, string? link)
    {
        var status = run.Status switch { OperationStatuses.Completed => OpsStatus.Verified, OperationStatuses.CompletedWithWarnings => OpsStatus.Warning, OperationStatuses.NotVerified => OpsStatus.NotVerified, OperationStatuses.Running => OpsStatus.Warning, _ => OpsStatus.Failed };
        return new(code, title, status, $"{run.Status}: {Redaction.Clean(run.Summary)}", D($"Reference {run.Reference}"), run.CompletedUtc ?? run.StartedUtc, run.CorrelationId, status is OpsStatus.Failed or OpsStatus.Warning ? "Open the evidence and read the failing checks." : null, link);
    }

    private DashboardPanel RecoveryPanel(IReadOnlyList<BackupSummary> backupList, IReadOnlyList<OperationRunDto> runs, ValidationReport? lastValidation, DateTime now, Func<string, string> D)
    {
        var items = new List<DashboardItem>();
        if (!database.Supported) items.Add(new("REC-001", "Backup", OpsStatus.NotVerified, "Backup and restore need SQL Server. " + database.UnsupportedReason, Link: "/Admin/Operations/Backups"));
        else if (!recovery.Value.BackupConfigured) items.Add(new("REC-001", "Backup", OpsStatus.Warning, "No backup directory is configured, so no backup can be taken.", NextStep: "Configure Ecp:Recovery:BackupDirectory.", Link: "/Admin/Operations/Backups"));
        else
        {
            var last = backupList.FirstOrDefault(b => b.State is BackupState.Verified or BackupState.VerifiedWithWarnings);
            if (last is null) items.Add(new("REC-001", "Last verified backup", OpsStatus.Warning, "There is no verified backup.", NextStep: "Take a backup and verify it.", Link: "/Admin/Operations/Backups"));
            else
            {
                var age = now - (last.CreatedUtc ?? now);
                items.Add(new("REC-001", "Last verified backup", age.TotalDays > recovery.Value.StaleBackupDays ? OpsStatus.Warning : last.State == BackupState.VerifiedWithWarnings ? OpsStatus.Warning : OpsStatus.Verified,
                    age.TotalDays > recovery.Value.StaleBackupDays ? $"The newest verified backup is {Ago(now, last.CreatedUtc ?? now)}, older than the {recovery.Value.StaleBackupDays}-day limit." : $"The newest verified backup was made {Ago(now, last.CreatedUtc ?? now)}.",
                    D($"{last.BackupId}; manifest {last.ManifestSha256}"), last.CreatedUtc, null, age.TotalDays > recovery.Value.StaleBackupDays ? "Take a fresh backup." : null, $"/Admin/Operations/Backups/{last.BackupId}"));
            }
            var failedBackup = runs.FirstOrDefault(r => r.Kind == OperationKinds.Backup);
            if (failedBackup is not null && failedBackup.Status is OperationStatuses.Failed or OperationStatuses.Incomplete) items.Add(RunItem("REC-002", "Last backup attempt", failedBackup, D, "/Admin/Operations/Backups"));
        }
        var restore = runs.FirstOrDefault(r => r.Kind == OperationKinds.Restore);
        items.Add(restore is null ? new("REC-003", "Restore rehearsal", OpsStatus.NotVerified, "No restore has been recorded. A backup that has never been restored is not proved.", NextStep: "Rehearse a restore into a disposable database.", Link: "/Admin/Operations/Restore") : RunItem("REC-003", "Last restore", restore, D, "/Admin/Operations/Restore"));
        items.Add(lastValidation is null ? new("REC-004", "Installation validation", OpsStatus.NotVerified, "Validation has not been run on this installation.", NextStep: "Run validation.", Link: "/Admin/Operations/Validation")
            : new("REC-004", "Installation validation", lastValidation.Outcome switch { ValidationOutcome.Passed => OpsStatus.Verified, ValidationOutcome.PassedWithWarnings => OpsStatus.Warning, ValidationOutcome.NotVerified => OpsStatus.NotVerified, _ => OpsStatus.Failed },
                $"Last validation: {lastValidation.Outcome} ({lastValidation.Count(CheckOutcome.Failed)} failed, {lastValidation.Count(CheckOutcome.Warning)} warnings, {lastValidation.Count(CheckOutcome.NotVerified)} not verified).", LastUtc: lastValidation.CompletedUtc, CorrelationId: lastValidation.CorrelationId, Link: "/Admin/Operations/Validation"));
        return Panel("recovery", "Backup and restore", "Whether the installation can be recovered, and the evidence.", items);
    }

    private DashboardPanel JobsPanel(IReadOnlyList<FactTotal>? f, IReadOnlyList<LeaseInfo> leaseList, IReadOnlyList<OperationRunDto> runs, MaintenanceStatus m, DateTime now, Func<string, string> D)
    {
        var items = new List<DashboardItem>();
        if (f is null || !Has(f, "Notifications", "Jobs.LastStartedUtc")) items.Add(new("JOB-001", "Background jobs", OpsStatus.NotVerified, "Job facts could not be read."));
        else
        {
            var last = When(f, "Notifications", "Jobs.LastStartedUtc");
            var stale = Sum(f, "Notifications", "Jobs.StaleRunning"); var failed = Sum(f, "Notifications", "Jobs.Failed24h"); var ok = Sum(f, "Notifications", "Jobs.Succeeded24h");
            items.Add(new("JOB-001", "Background jobs", stale > 0 ? OpsStatus.Failed : last is null ? OpsStatus.NotVerified : failed > 0 ? OpsStatus.Warning : m.Enabled ? OpsStatus.Warning : ok == 0 ? OpsStatus.Warning : OpsStatus.Verified,
                stale > 0 ? $"{stale} job run(s) are recorded as running for over an hour. The process that ran them has probably stopped." : last is null ? "No job has ever run (jobs may be turned off)." : failed > 0 ? $"{failed} job run(s) failed in the last 24 hours; the next cycle repeats them (every job is safe to repeat)." : m.Enabled ? "Jobs are paused by maintenance mode." : ok == 0 ? "No job has completed in the last 24 hours." : $"{ok} job run(s) succeeded in the last 24 hours.",
                D($"Running now: {Sum(f, "Notifications", "Jobs.Running")}; last failure: {When(f, "Notifications", "Jobs.LastFailedUtc"):u}"), last, null, failed > 0 || stale > 0 ? "Check the application log with the tenant's job page, then wait for the next cycle." : null, "#jobs"));
        }
        foreach (var l in leaseList.Where(l => l.IsActive).OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.ScopeKey, StringComparer.Ordinal).Take(20))
            items.Add(new("JOB-LEASE", $"Lease {l.Name}", OpsStatus.Verified, $"Held since {Ago(now, l.AcquiredUtc)}; renewed {Ago(now, l.HeartbeatUtc)}.", D($"scope {l.ScopeKey}; owner {l.OwnerDescription}; generation {l.Generation}"), l.HeartbeatUtc, Link: "/Admin/Operations/Leases"));
        foreach (var l in leaseList.Where(l => !l.IsActive && l.ReleasedUtc is null && now - l.ExpiresUtc > StaleLeaseGrace && l.Name != "background.jobs").Take(10))
            items.Add(new("JOB-STALE", $"Lease {l.Name} expired without being released", OpsStatus.Warning, "The holder stopped without releasing it. Nothing is blocked (an expired lease can be taken), but the holder may have been interrupted.", D($"scope {l.ScopeKey}; expired {l.ExpiresUtc:u}"), l.ExpiresUtc, NextStep: "Check the operation history for an interrupted run.", Link: "/Admin/Operations/Leases"));
        var running = runs.Where(r => r.Status == OperationStatuses.Running && now - r.StartedUtc > StaleLeaseGrace).ToList();
        foreach (var r in running.Where(r => !leaseList.Any(l => l.IsActive && l.Name is "recovery.exclusive" or "upgrade.exclusive")))
            items.Add(new("JOB-STUCK", $"{r.Kind} recorded as running with no live lease", OpsStatus.Failed, "An operation is recorded as running but nothing holds its lease: the process that ran it stopped part-way. Its result is unknown and must not be assumed good.", D(Redaction.Clean(r.Reference)), r.StartedUtc, r.CorrelationId, "Read the operation's evidence; do not assume it completed. Restores and upgrades are never retried automatically.", "/Admin/Operations/Leases"));
        return Panel("jobs", "Background jobs and leases", "Whether scheduled work is running, and whether anything looks stuck.", items);
    }

    private DashboardPanel OutboxPanel(IReadOnlyList<FactTotal>? f, MaintenanceStatus m, DateTime now, Func<string, string> D)
    {
        var items = new List<DashboardItem>();
        if (f is null || !Has(f, "Notifications", "Outbox.Pending")) items.Add(new("OUT-001", "Outbox", OpsStatus.NotVerified, "Outbox facts could not be read."));
        else
        {
            long pending = Sum(f, "Notifications", "Outbox.Pending"), failed = Sum(f, "Notifications", "Outbox.Failed"), dead = Sum(f, "Notifications", "Outbox.DeadLetter");
            var oldest = When(f, "Notifications", "Outbox.Pending");
            var stuckPending = pending > 0 && oldest is not null && now - oldest > TimeSpan.FromHours(1) && !m.Enabled;
            items.Add(new("OUT-001", "Outbox", dead > 0 || failed > 0 || stuckPending ? OpsStatus.Warning : OpsStatus.Verified,
                $"{pending} waiting, {failed} failed (will retry), {dead} dead-lettered, {Sum(f, "Notifications", "Outbox.Sent24h")} sent in the last 24 hours across all tenants." + (m.Enabled && pending > 0 ? " Delivery is paused by maintenance mode." : ""),
                D(oldest is null ? "" : "Oldest waiting message: " + Ago(now, oldest.Value)), oldest, null, dead > 0 ? "A tenant administrator retries dead-lettered messages in that tenant's own Notifications page. An operator cannot act on another tenant's messages." : stuckPending ? "Messages have waited over an hour: check the email provider and the job status." : null, "#outbox"));
        }
        return Panel("outbox", "Notification outbox", "Counts only. No recipient, subject or message is shown.", items);
    }

    private DashboardPanel ConfigurationPanel(IReadOnlyList<FactTotal>? f, Func<string, string> D)
    {
        var items = new List<DashboardItem>();
        if (f is null || !Has(f, "Configuration", "Releases.InForce")) items.Add(new("CFG-001", "Configuration and packs", OpsStatus.NotVerified, "Configuration facts could not be read."));
        else
        {
            long inUse = Sum(f, "Packs", "Incompatible.InUse"), untrusted = Sum(f, "Packs", "Untrusted.InUse"), unreadable = Sum(f, "Packs", "Unreadable.InUse"), idle = Sum(f, "Packs", "Incompatible.Idle");
            items.Add(new("CFG-001", "Configuration releases in force", OpsStatus.Verified, $"{Sum(f, "Configuration", "Releases.InForce")} configuration release(s) are in force across all organisations; {Sum(f, "Configuration", "Drafts.Open")} draft(s) are open."));
            items.Add(new("CFG-002", "Pack compatibility", inUse + untrusted + unreadable > 0 ? OpsStatus.Failed : idle > 0 ? OpsStatus.Warning : OpsStatus.Verified,
                inUse + untrusted + unreadable > 0 ? "A pack that an organisation uses would be refused today (incompatible, untrusted or unreadable)." : idle > 0 ? "Some installed packs that nobody uses would be refused today." : "Every installed pack is compatible and acceptable under this environment's trust policy.",
                D($"In use, incompatible {inUse}; untrusted {untrusted}; unreadable {unreadable}; idle incompatible {idle}; installed {Sum(f, "Packs", "Installed")} (summed over tenants)"), NextStep: inUse + untrusted + unreadable > 0 ? "Run the upgrade pre-flight for the exact packs." : null, Link: "/Admin/Packs"));
        }
        return Panel("configuration", "Packs and configuration", "Configuration in force, and whether the installed packs are still acceptable.", items);
    }

    private DashboardPanel IntegrityPanel(ValidationReport? v, ReadinessReport? report, Func<string, string> D)
    {
        DashboardItem FromChecks(string code, string title, params string[] checkCodes)
        {
            if (v is null) return new(code, title, OpsStatus.NotVerified, "Validation has not been run on this installation, so this is not verified.", NextStep: "Run validation.", Link: "/Admin/Operations/Validation");
            var checks = v.Checks.Where(c => checkCodes.Contains(c.Code)).ToList();
            if (checks.Count == 0) return new(code, title, OpsStatus.NotVerified, "The last validation did not include this check.", LastUtc: v.CompletedUtc, CorrelationId: v.CorrelationId, Link: "/Admin/Operations/Validation");
            var status = OpsStatuses.Worst(checks.Select(c => c.Outcome switch { CheckOutcome.Passed => OpsStatus.Verified, CheckOutcome.Warning => OpsStatus.Warning, CheckOutcome.NotVerified => OpsStatus.NotVerified, _ => OpsStatus.Failed }));
            return new(code, title, status, status == OpsStatus.Verified ? "Passed at the last validation." : status == OpsStatus.Failed ? "Failed at the last validation." : status == OpsStatus.Warning ? "Passed with warnings at the last validation." : "Not verified at the last validation.",
                D(string.Join(" | ", checks.Select(c => $"{c.Code}: {c.Description}"))), v.CompletedUtc, v.CorrelationId, status is OpsStatus.Failed or OpsStatus.Warning ? "Read the failing check and its remediation." : null, "/Admin/Operations/Validation");
        }
        var storage = report?.Results.FirstOrDefault(r => r.Id == "R-ST-02");
        var items = new List<DashboardItem>
        {
            storage is null ? new("INT-001", "Document store", OpsStatus.NotVerified, "The storage round trip was not run.") : new("INT-001", "Document store: write, read and delete", Map(storage.Status), storage.Status == ReadinessStatus.Healthy ? "A write, read and delete round trip succeeded just now." : "The storage round trip failed.", D(storage.Message)),
            FromChecks("INT-002", "Document content and metadata", "VAL-010", "VAL-015"),
            FromChecks("INT-003", "Audit chain", "VAL-009"),
        };
        return Panel("integrity", "Document store and audit chain", "The document store is probed now; document content and the audit chain are read from the last validation, with its time.", items);
    }

    private DashboardPanel CertificatesPanel(IReadOnlyList<FactTotal>? f, DateTime now, Func<string, string> D)
    {
        var items = new List<DashboardItem>();
        if (f is null || !Has(f, "Certificates", "PackKeys.Active")) items.Add(new("CRT-001", "Pack-signing keys", OpsStatus.NotVerified, "Key facts could not be read."));
        else
        {
            long active = Sum(f, "Certificates", "PackKeys.Active"), expired = Sum(f, "Certificates", "PackKeys.Expired"), soon = Sum(f, "Certificates", "PackKeys.Expiring30d"); var next = When(f, "Certificates", "PackKeys.NextExpiryUtc");
            items.Add(new("CRT-001", "Pack-signing keys", expired > 0 ? OpsStatus.Failed : soon > 0 ? OpsStatus.Warning : OpsStatus.Verified,
                expired > 0 ? $"{expired} trusted key(s) have expired but are still marked active; packs signed by them are refused." : soon > 0 ? $"{soon} trusted key(s) expire within 30 days." : active == 0 ? "No trusted pack-signing keys are registered." : $"{active} trusted key(s) are active; none expires within 30 days.",
                D(next is null ? "No key has an end date." : "Next expiry " + next.Value.ToString("u")), next, NextStep: expired + soon > 0 ? "Register the publisher's new key and revoke the old one." : null, Link: "/Admin/Packs"));
        }
        var ring = keyRings.FirstOrDefault()?.Inspect(now);
        items.Add(ring is null ? new("CRT-002", "Data-protection key ring", OpsStatus.NotVerified, "This host does not expose its key ring.")
            : !ring.Readable ? new("CRT-002", "Data-protection key ring", OpsStatus.Failed, "The key ring could not be read.", D(ring.Note))
            : ring.UsableKeys == 0 ? new("CRT-002", "Data-protection key ring", OpsStatus.Failed, "No usable key. Sessions and protected values cannot be read.", D(ring.Note))
            : new("CRT-002", "Data-protection key ring", ring.NextExpiryUtc is { } e && e - now < TimeSpan.FromDays(7) ? OpsStatus.Warning : OpsStatus.Verified, ring.NextExpiryUtc is { } e2 && e2 - now < TimeSpan.FromDays(7) ? "The newest key expires within 7 days; a new key is created the next time the ring is used." : $"{ring.UsableKeys} usable key(s).", D(ring.Note), ring.NextExpiryUtc));
        if (recovery.Value.EncryptionRequested) items.Add(new("CRT-003", "Backup-encryption certificate", OpsStatus.NotVerified, "Backup encryption is requested. The certificate lives on the database server; its expiry is not read by the platform.", NextStep: "Check the certificate's expiry on the database server and keep its backup."));
        items.Add(new("CRT-004", "TLS certificate", OpsStatus.NotVerified, "TLS is normally terminated outside the application (reverse proxy, container ingress, IIS or App Service). The platform does not inspect that certificate.", NextStep: "Monitor the certificate where it is installed."));
        items.Add(new("CRT-005", "Secret expiry", OpsStatus.NotVerified, "Secrets are resolved by reference (environment, file, vault) and carry no expiry the platform can read.", NextStep: "Track secret rotation in the secret provider."));
        return Panel("certificates", "Certificates and secret expiry", "Only what the platform can read is judged. The rest is Not Verified, not assumed fine.", items);
    }

    // ---- failures -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private List<FailureEntry> Failures(IReadOnlyList<OperationRunDto> runs, IReadOnlyList<LeaseInfo> leaseList, DateTime now, Func<string, string> D)
    {
        var list = new List<FailureEntry>();
        foreach (var r in runs.Where(r => r.Status is OperationStatuses.Failed or OperationStatuses.Incomplete).Take(15))
        {
            var (next, link, retry, label) = r.Kind switch
            {
                OperationKinds.Backup => ("This backup is not a recovery point. Fix the cause in the summary, then start a new backup (a new attempt, not a resume).", "/Admin/Operations/Backups", true, "Start a new backup"),
                OperationKinds.Validation => ("Read the failing checks and fix the cause; then run validation again.", "/Admin/Operations/Validation", true, "Run validation again"),
                OperationKinds.Restore => ("A failed restore is never retried automatically and never continued. Maintenance mode stays on. Follow the restore failure steps in the runbook.", "/Admin/Operations/Restore", false, null),
                OperationKinds.Upgrade => ("A failed upgrade is never retried. Maintenance mode stays on. Roll back by restoring the verified pre-upgrade backup.", "/Admin/Operations/RollbackEvidence", false, null),
                OperationKinds.UpgradeRehearsal => ("The rehearsal ran on a copy; the installation is untouched. Fix the cause and rehearse again on a new copy.", "/Admin/Operations/UpgradePreview", true, "Rehearse again"),
                OperationKinds.RollbackEvidence => ("Rollback is not yet proved for this backup. Do not rely on it until it is.", "/Admin/Operations/RollbackEvidence", true, "Prove rollback again"),
                _ => ("Read the operation's evidence.", "/Admin/Operations", false, null),
            };
            list.Add(new("FAIL-" + r.Kind.ToUpperInvariant(), r.Kind, r.Status, Redaction.Clean(r.Summary), r.CompletedUtc ?? r.StartedUtc, r.CorrelationId, next, link, retry, label));
        }
        return list;
    }
}
