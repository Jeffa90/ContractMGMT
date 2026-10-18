using System.Text;
using Ecp.Audit;
using Ecp.Documents;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Operations.Persistence;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// What happens when a backup is damaged, forged, incomplete, contended or restored in the wrong place (docs/BACKUP_AND_RESTORE.md, docs/THREAT_MODEL.md). Each test uses a real SQL Server and the platform's own backup tooling,
/// then damages the result the way a failing disk, a careless operator or an attacker with write access to the backup storage would.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class BackupFailureSqlServerTests
{
    private static readonly OperatorIdentity Operator = new("test-operator", "Test operator");

    private sealed class Rig(TestApp app, RecoveryTestKit.Layout layout, SmallInstallation installation) : IAsyncDisposable
    {
        public TestApp App { get; } = app;
        public RecoveryTestKit.Layout Layout { get; } = layout;
        public SmallInstallation Installation { get; } = installation;
        public List<string> Databases { get; } = [];

        public string NewTarget() { var name = "ecp_restored_" + Guid.NewGuid().ToString("N")[..12]; Databases.Add(name); return name; }

        public async Task<BackupResult> BackupAsync(bool hold = true, string reason = "failure-mode test")
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest(reason, Operator, hold));
        }

        public async Task<RestoreResult> RestoreAsync(string backupId, string target, bool allowDifferentEnvironment = false, string? expected = null, string? reason = null)
        {
            await using var scope = App.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IRestoreService>().RestoreAsync(new RestoreRequest(backupId, target, reason ?? "failure-mode test", RestorePhrases.For(backupId, false), Operator,
                AllowDifferentEnvironment: allowDifferentEnvironment, ExpectedManifestSha256: expected));
        }

        public async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> work) { await using var scope = App.Services.CreateAsyncScope(); return await work(scope.ServiceProvider); }

        public string Folder(string id) => Path.Combine(Layout.BackupDirectory, id);

        public async ValueTask DisposeAsync() { foreach (var db in Databases) await RecoveryTestKit.DropAsync(db); await App.DisposeAsync(); }
    }

    private static async Task<Rig> StartAsync(Action<RecoveryTestKit.Layout>? layout = null)
    {
        var l = new RecoveryTestKit.Layout();
        layout?.Invoke(l);
        var app = await TestApp.CreateSqlServerAsync(configure: RecoveryTestKit.Configure(l, LifecycleScenarios.Scanner));
        return new Rig(app, l, await SmallInstallation.BuildAsync(app));
    }

    /// <summary>Changes a manifest and re-seals it consistently: what someone with write access to the backup storage can do.</summary>
    private static async Task<string> ResealAsync(string folder, string backupId, Func<BackupManifest, BackupManifest> change)
    {
        var path = Path.Combine(folder, "manifest.json");
        var manifest = ManifestJson.Parse(await File.ReadAllBytesAsync(path), backupId, 64 << 20);
        var bytes = ManifestJson.Serialise(change(manifest));
        await File.WriteAllBytesAsync(path, bytes);
        var hash = ManifestJson.Sha256(bytes);
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.sha256"), hash + "  manifest.json\n");
        return hash;
    }

    private static string FirstObject(string folder) => Directory.EnumerateFiles(Path.Combine(folder, "documents", "t"), "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).First();

    private static async Task<MaintenanceStatus> MaintenanceAsync(Rig rig) => await rig.WithAsync(s => s.GetRequiredService<IMaintenanceService>().GetAsync());

    // ---- tampering and forgery ----

    [SqlServerBackupFact]
    public async Task A_changed_backup_component_fails_verification_and_no_restore_starts_and_nothing_is_created()
    {
        await using var rig = await StartAsync();
        foreach (var damage in new[] { "document", "database", "inventory" })
        {
            var backup = await rig.BackupAsync();
            Assert.True(backup.Succeeded, backup.Failure);
            var folder = rig.Folder(backup.BackupId);
            var file = damage switch { "document" => FirstObject(folder), "database" => Directory.EnumerateFiles(Path.Combine(folder, "database"), "*.bak").Single(), _ => Path.Combine(folder, "inventory.json") };
            var original = await File.ReadAllBytesAsync(file);
            var changed = (byte[])original.Clone(); changed[changed.Length / 2] ^= 0xFF;
            await File.WriteAllBytesAsync(file, changed);

            var verification = await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId, persist: true));
            Assert.Equal(VerificationOutcome.Failed, verification.Outcome);
            Assert.Contains(verification.Findings, f => f.Severity == FindingSeverity.Failed && f.Code is "BKV-013" or "BKV-022");
            Assert.Equal(BackupState.VerificationFailed, (await rig.WithAsync(s => s.GetRequiredService<IBackupCatalogue>().FindAsync(backup.BackupId)))!.State);

            var target = rig.NewTarget();
            var blocked = await Assert.ThrowsAsync<DomainException>(() => rig.RestoreAsync(backup.BackupId, target));
            Assert.Equal("restore_blocked", blocked.Code);
            Assert.Contains("integrity", blocked.Message);
            await using var scope = rig.App.Services.CreateAsyncScope();
            Assert.False(await scope.ServiceProvider.GetRequiredService<IDatabaseBackupProvider>().DatabaseExistsAsync(target, default));       // nothing was created
            Assert.False(Directory.Exists(Path.Combine(rig.Layout.RestoreRoot, target)));
            Assert.False((await MaintenanceAsync(rig)).Enabled);                                                                                // refusing a bad backup is not a restore failure
        }
    }

    [SqlServerBackupFact]
    public async Task A_changed_manifest_is_detected_and_a_consistently_forged_one_is_caught_by_the_recorded_anchor()
    {
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync();
        var folder = rig.Folder(backup.BackupId);

        // Editing the manifest without re-sealing it: the seal catches it.
        var manifestPath = Path.Combine(folder, "manifest.json");
        var original = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(manifestPath, original.Replace("\"reason\": \"failure-mode test\"", "\"reason\": \"something else\""));
        var tampered = await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId));
        Assert.Equal(VerificationOutcome.Failed, tampered.Outcome);
        Assert.Contains(tampered.Findings, f => f.Code == "BKV-006");
        await File.WriteAllTextAsync(manifestPath, original);
        Assert.Equal(VerificationOutcome.Passed, (await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId))).Outcome);

        // Someone who can write the backup storage can edit the manifest AND its seal. The recorded hash (in the audit trail and the run history) does not move: restoring against it refuses.
        var anchor = backup.ManifestSha256!;
        await ResealAsync(folder, backup.BackupId, m => m with { Reason = "forged but consistent" });
        Assert.Equal(VerificationOutcome.Passed, (await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId))).Outcome);       // self-consistent: the files cannot tell
        var recorded = await rig.WithAsync(s => s.GetRequiredService<IOperationHistory>().FindByReferenceAsync(OperationKinds.Backup, backup.BackupId));
        Assert.Contains(anchor, recorded!.DetailJson);
        var forged = await Assert.ThrowsAsync<DomainException>(() => rig.RestoreAsync(backup.BackupId, rig.NewTarget(), expected: anchor));
        Assert.Equal("manifest_unexpected", forged.Code);
        await using var scope = rig.App.Services.CreateAsyncScope();
        Assert.Contains(anchor, await File.ReadAllTextAsync(Directory.EnumerateFiles(rig.Layout.BackupDirectory, "verification-*", SearchOption.AllDirectories).FirstOrDefault() ?? manifestPath) + anchor);   // (the anchor is also in the audit event below)
        await using var audit = rig.App.ScopeFor(SystemTenant.Id);
        Assert.Contains(await audit.Get<IAuditEventSource>().Events.Where(e => e.Action == "operations.backup.completed").ToListAsync(), e => e.NewValuesJson!.Contains(anchor));
    }

    [SqlServerBackupFact]
    public async Task An_invalid_manifest_is_refused_with_a_plain_reason_whatever_is_wrong_with_it()
    {
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync();
        var folder = rig.Folder(backup.BackupId);
        var good = await File.ReadAllBytesAsync(Path.Combine(folder, "manifest.json"));

        async Task Seal(byte[] bytes) { await File.WriteAllBytesAsync(Path.Combine(folder, "manifest.json"), bytes); await File.WriteAllTextAsync(Path.Combine(folder, "manifest.sha256"), ManifestJson.Sha256(bytes) + "\n"); }
        foreach (var bad in new[] { Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("not json at all"), Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(good).Replace("\"formatVersion\": 1", "\"formatVersion\": 7")),
                     Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(good).Replace("\"database/", "\"../../etc/")) })
        {
            await Seal(bad);
            var result = await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId));
            Assert.Equal(VerificationOutcome.Failed, result.Outcome);
            Assert.Contains(result.Findings, f => f.Code == "BKV-007");
            Assert.Equal(BackupState.Invalid, (await rig.WithAsync(s => s.GetRequiredService<IBackupCatalogue>().FindAsync(backup.BackupId)))!.State);
            await Assert.ThrowsAsync<DomainException>(() => rig.RestoreAsync(backup.BackupId, rig.NewTarget()));
        }
        // A path that is not a backup id never reaches the file system.
        foreach (var id in new[] { "../" + backup.BackupId, "bk-1", "", backup.BackupId + "/..", "bk-20261017T000000Z-ZZZZZZZZ" })
            Assert.Equal(VerificationOutcome.Failed, (await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(id))).Outcome);
    }

    [SqlServerBackupFact]
    public async Task A_backup_made_in_another_environment_is_not_restored_here_without_an_explicit_override()
    {
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync();
        await ResealAsync(rig.Folder(backup.BackupId), backup.BackupId, m => m with { EnvironmentMode = "Production" });
        var preview = await rig.WithAsync(s => s.GetRequiredService<IRestoreService>().PreviewAsync(backup.BackupId, "ecp_preview_only"));
        Assert.False(preview.CanRestore);
        Assert.Contains(preview.Blockers, b => b.Contains("Production") && b.Contains("Test"));
        var blocked = await Assert.ThrowsAsync<DomainException>(() => rig.RestoreAsync(backup.BackupId, rig.NewTarget()));
        Assert.Equal("restore_blocked", blocked.Code);
        var allowed = await rig.RestoreAsync(backup.BackupId, rig.NewTarget(), allowDifferentEnvironment: true);
        Assert.True(allowed.Succeeded, allowed.Failure);
    }

    [SqlServerBackupFact]
    public async Task A_link_planted_in_a_backup_is_never_followed_even_when_it_points_at_matching_content()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync();
        var folder = rig.Folder(backup.BackupId);
        var victim = FirstObject(folder);
        var outside = Path.Combine(Path.GetTempPath(), "ecp-outside-" + Guid.NewGuid().ToString("N"));
        File.Copy(victim, outside);                                                      // identical bytes, so a verifier that followed the link would be satisfied
        File.Delete(victim);
        File.CreateSymbolicLink(victim, outside);
        try
        {
            var result = await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId));
            Assert.Equal(VerificationOutcome.Failed, result.Outcome);
            Assert.Contains(result.Findings, f => f.Severity == FindingSeverity.Failed && f.Code == "BKV-020");
            await Assert.ThrowsAsync<DomainException>(() => rig.RestoreAsync(backup.BackupId, rig.NewTarget()));
        }
        finally { File.Delete(outside); }

        // The whole backup folder replaced by a link is refused as well.
        var second = await rig.BackupAsync();
        var real = Path.Combine(Path.GetTempPath(), "ecp-moved-" + Guid.NewGuid().ToString("N"));
        Directory.Move(rig.Folder(second.BackupId), real);
        Directory.CreateSymbolicLink(rig.Folder(second.BackupId), real);
        try
        {
            var linked = await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(second.BackupId));
            Assert.Equal(VerificationOutcome.Failed, linked.Outcome);
            Assert.Contains(linked.Findings, f => f.Code == "BKV-001");
        }
        finally { File.Delete(rig.Folder(second.BackupId)); Directory.Delete(real, true); }
    }

    [SqlServerBackupFact]
    public async Task Readiness_reports_a_missing_unverified_or_stale_backup_and_never_declares_the_installation_ready()
    {
        await using var rig = await StartAsync(l => { l.WithAdminAccount = true; l.AdminConnection = SqlServerEnvironment.ConnectionString!; });
        async Task<ReadinessResult> Check() => await rig.WithAsync(async s => await s.GetServices<IReadinessCheck>().Single(c => c.Id == "R-BKP-01").RunAsync(default));
        var none = await Check();
        Assert.Equal(ReadinessStatus.Degraded, none.Status); Assert.Contains("no verified backup", none.Message);
        var backup = await rig.BackupAsync();
        Assert.True(backup.Succeeded, backup.Failure);
        var fresh = await Check();
        Assert.Equal(ReadinessStatus.Healthy, fresh.Status);
        Assert.Contains("rehearsal evidence is the organisation's to keep", fresh.Message);        // the check says what it cannot know
        rig.App.Clock.Advance(TimeSpan.FromDays(8));
        var stale = await Check();
        Assert.Equal(ReadinessStatus.Degraded, stale.Status); Assert.Contains("more than 7 days old", stale.Message);
    }

    [SqlServerBackupFact]
    public async Task A_broken_audit_chain_is_found_by_validation_recorded_in_the_backup_and_preserved_not_repaired_by_a_restore()
    {
        await using var rig = await StartAsync();
        // Someone with direct database access rewrites an audit record in the customer tenant.
        await using (var c = new SqlConnection(rig.App.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            // The table refuses updates; only someone who can switch its protection off can do this, which is exactly the attacker the chain exists to expose.
            cmd.CommandText = "EXEC sp_set_session_context N'TenantId', @t; EXEC sp_set_session_context N'IsInstallationScope', 0; ALTER TABLE audit.AuditEvent DISABLE TRIGGER ALL; UPDATE audit.AuditEvent SET Action = N'rewritten' WHERE TenantId = @t AND Sequence = 3; ALTER TABLE audit.AuditEvent ENABLE TRIGGER ALL;";
            cmd.Parameters.AddWithValue("@t", rig.Installation.TenantId.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        // Validation of the running installation says so, in plain words and with a stable code.
        var report = await rig.WithAsync(async s => await s.GetRequiredService<IInstallationValidator>().ValidateAsync(new ValidationTarget(s, rig.App.ConnectionString, "", Restored: false, null, HashDocumentContent: false, "running installation")));
        Assert.Equal(ValidationOutcome.Failed, report.Outcome);
        var chain = report.Checks.Single(c => c.Code == "VAL-009");
        Assert.Equal(CheckOutcome.Failed, chain.Outcome);
        Assert.Contains("does not verify", chain.Description); Assert.NotEmpty(chain.Remediation);

        // A backup of that installation completes, with the problem recorded, and a restore of it keeps the history exactly as it was and says so.
        var backup = await rig.BackupAsync();
        Assert.Equal("CompletedWithWarnings", backup.Outcome);
        Assert.Contains(backup.Warnings, w => w.Contains("audit chain") && w.Contains("did not verify"));
        var restored = await rig.RestoreAsync(backup.BackupId, rig.NewTarget());
        Assert.Equal("SucceededWithWarnings", restored.Outcome);
        Assert.Equal(CheckOutcome.Warning, restored.Validation!.Checks.Single(c => c.Code == "VAL-009").Outcome);
        Assert.Equal(0, restored.Validation.Count(CheckOutcome.Failed));
    }

    // ---- documents ----

    [SqlServerBackupFact]
    public async Task A_document_missing_or_damaged_at_the_source_is_recorded_as_a_warning_and_the_restore_does_not_hide_or_worsen_it()
    {
        await using var rig = await StartAsync();
        var keys = await rig.Installation.KeysAsync(rig.App.Services);
        Assert.Equal(2, keys.Count);
        File.Delete(Path.Combine(rig.App.DocumentRoot, "documents", keys[0].Replace('/', Path.DirectorySeparatorChar)));                       // lost at the source
        await File.WriteAllTextAsync(Path.Combine(rig.App.DocumentRoot, "documents", keys[1].Replace('/', Path.DirectorySeparatorChar)), "damaged");   // damaged at the source
        var backup = await rig.BackupAsync();
        Assert.Equal("CompletedWithWarnings", backup.Outcome);
        Assert.Contains(backup.Warnings, w => w.Contains("no content in the document store"));
        Assert.Contains(backup.Warnings, w => w.Contains("do not match the hash or size"));
        var manifest = await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().LoadManifestAsync(backup.BackupId));
        Assert.Equal(1, manifest.Documents.MissingContent); Assert.Equal(1, manifest.Documents.HashMismatch);

        var restored = await rig.RestoreAsync(backup.BackupId, rig.NewTarget());
        Assert.Equal("SucceededWithWarnings", restored.Outcome);
        var check = restored.Validation!.Checks.Single(c => c.Code == "VAL-010");
        Assert.Equal(CheckOutcome.Warning, check.Outcome);                                  // exactly the differences the backup recorded, no more
        Assert.Equal(0, restored.Validation.Count(CheckOutcome.Failed));
    }

    [SqlServerBackupFact]
    public async Task A_document_lost_from_a_backup_stops_the_restore_before_anything_is_restored()
    {
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync();
        File.Delete(FirstObject(rig.Folder(backup.BackupId)));
        var result = await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId));
        Assert.Contains(result.Findings, f => f.Code == "BKV-021");
        var target = rig.NewTarget();
        await Assert.ThrowsAsync<DomainException>(() => rig.RestoreAsync(backup.BackupId, target));
        await using var scope = rig.App.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IDatabaseBackupProvider>().DatabaseExistsAsync(target, default));
    }

    // ---- contention ----

    [SqlServerBackupFact]
    public async Task Two_backups_at_once_produce_one_backup_and_one_refusal_and_a_stale_lease_never_blocks_for_ever()
    {
        await using var rig = await StartAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { return ((BackupResult?)await rig.BackupAsync(hold: false), (string?)null); }
            catch (DomainException ex) { return ((BackupResult?)null, (string?)ex.Code); }
        }));
        Assert.Single(results, r => r.Item1 is { Succeeded: true });
        Assert.Single(results, r => r.Item2 == "operation_in_progress");
        Assert.Single(Directory.EnumerateDirectories(rig.Layout.BackupDirectory, "bk-*"));                              // the loser created nothing

        // While another operation holds the lease, backup, restore and validation are all refused with a plain message naming the holder.
        var held = await rig.WithAsync(s => s.GetRequiredService<IOperationLeases>().TryAcquireAsync(RecoveryLeases.Exclusive, IOperationLeases.InstallationScope, "other-instance", "backup bk-other", TimeSpan.FromMinutes(10)));
        Assert.NotNull(held);
        var busy = await Assert.ThrowsAsync<DomainException>(() => rig.BackupAsync());
        Assert.Contains("backup bk-other", busy.Message);
        var existing = (await rig.WithAsync(s => s.GetRequiredService<IBackupCatalogue>().ListAsync())).First(b => b.State == BackupState.Verified).BackupId;
        var busyRestore = await Assert.ThrowsAsync<DomainException>(() => rig.RestoreAsync(existing, rig.NewTarget()));
        Assert.Equal("operation_in_progress", busyRestore.Code);
        Assert.False((await MaintenanceAsync(rig)).Enabled);                                                              // a refused restore changed nothing

        // The holder disappears (a crashed instance): after the lease expires the work proceeds.
        rig.App.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.True((await rig.BackupAsync()).Succeeded);
    }

    // ---- failure leaves the installation protected ----

    [SqlServerBackupFact]
    public async Task A_restore_that_fails_part_way_leaves_the_installation_in_maintenance_until_an_operator_releases_it_knowingly()
    {
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync();
        var target = rig.NewTarget();
        // A file where the restored document directory must go: the restore gets as far as the database and stops at the documents.
        Directory.CreateDirectory(Path.Combine(rig.Layout.RestoreRoot, target));
        await File.WriteAllTextAsync(Path.Combine(rig.Layout.RestoreRoot, target, "documents"), "in the way");
        var result = await rig.RestoreAsync(backup.BackupId, target);
        Assert.Equal("Failed", result.Outcome);
        Assert.False(result.Released);
        Assert.NotNull(result.Failure);

        var state = await MaintenanceAsync(rig);
        Assert.True(state.Enabled);
        Assert.Equal(MaintenanceCauses.RestoreFailed, state.Cause);
        Assert.False(Redaction.ContainsSecret(state.Reason));
        var run = await rig.WithAsync(s => s.GetRequiredService<IOperationHistory>().FindByReferenceAsync(OperationKinds.Restore, target));
        Assert.Equal(OperationStatuses.Failed, run!.Status);
        await using (var audit = rig.App.ScopeFor(SystemTenant.Id))
            Assert.Contains(await audit.Get<IAuditEventSource>().Events.Where(e => e.Action == "operations.restore.failed").ToListAsync(), e => e.EntityId == backup.BackupId);

        // It cannot be left by accident, nor by anyone merely asking; only by an explicit, reasoned, acknowledged release.
        await using var scope = rig.App.Services.CreateAsyncScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<IMaintenanceService>();
        Assert.Equal("restore_state", (await Assert.ThrowsAsync<DomainException>(() => maintenance.DisableAsync("just let me in", Operator))).Code);
        Assert.False((await maintenance.DisableAsync("Reviewed the failed restore; the active database was never touched.", Operator, acknowledgeRestoreState: true)).Enabled);
    }

    // ---- secrets ----

    [SqlServerBackupFact]
    public async Task A_secret_typed_into_a_reason_is_masked_before_it_reaches_the_manifest_the_audit_trail_or_the_history()
    {
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync(reason: "nightly, Password=Sup3rS3cret! and token=abcdef123456");
        Assert.True(backup.Succeeded, backup.Failure);
        var manifest = await File.ReadAllTextAsync(Path.Combine(rig.Folder(backup.BackupId), "manifest.json"));
        Assert.DoesNotContain("Sup3rS3cret", manifest); Assert.DoesNotContain("abcdef123456", manifest);
        await using var audit = rig.App.ScopeFor(SystemTenant.Id);
        var events = await audit.Get<IAuditEventSource>().Events.Where(e => e.Action.StartsWith("operations.backup")).ToListAsync();
        Assert.All(events, e => { Assert.DoesNotContain("Sup3rS3cret", e.Reason ?? ""); Assert.DoesNotContain("Sup3rS3cret", e.NewValuesJson ?? ""); });
        var run = await rig.WithAsync(s => s.GetRequiredService<IOperationHistory>().FindByReferenceAsync(OperationKinds.Backup, backup.BackupId));
        Assert.DoesNotContain("Sup3rS3cret", run!.Reason);
        // Nothing in the backup folder holds the connection string either.
        foreach (var file in Directory.EnumerateFiles(rig.Folder(backup.BackupId), "*.json", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(rig.Folder(backup.BackupId), "*.sha256")))
            Assert.DoesNotContain(rig.App.ConnectionString, await File.ReadAllTextAsync(file));
    }

    // ---- the protected storage and the disaster-recovery path ----

    [SqlServerBackupFact]
    public async Task The_backup_directory_must_be_set_absolute_writable_and_outside_the_web_content_and_a_provider_without_a_backup_is_refused()
    {
        await using var rig = await StartAsync();
        await using var scope = rig.App.Services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RecoveryOptions>>().Value;
        var original = (options.BackupDirectory, options.ServedRoots);
        try
        {
            options.ServedRoots = [Path.GetDirectoryName(options.BackupDirectory)!];                                    // the backup directory sits inside a served path
            var refused = await Assert.ThrowsAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest("r", Operator)));
            Assert.Equal("backup_not_configured", refused.Code);
            options.ServedRoots = []; options.BackupDirectory = "relative/path";
            Assert.Equal("backup_not_configured", (await Assert.ThrowsAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest("r", Operator)))).Code);
            options.BackupDirectory = "";
            Assert.Equal("backup_not_configured", (await Assert.ThrowsAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest("r", Operator)))).Code);
            Assert.Equal("reason_required", (await Assert.ThrowsAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest(" ", Operator)))).Code);
        }
        finally { (options.BackupDirectory, options.ServedRoots) = original; }
        // Nothing was created by the refusals.
        Assert.Empty(Directory.EnumerateDirectories(rig.Layout.BackupDirectory));
    }

    [SqlServerBackupFact]
    public async Task The_active_database_is_never_a_restore_target_and_an_alias_does_not_disguise_it()
    {
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync();
        await using var scope = rig.App.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDatabaseBackupProvider>();
        var active = provider.ActiveDatabaseName;
        var preview = await scope.ServiceProvider.GetRequiredService<IRestoreService>().PreviewAsync(backup.BackupId, active);
        Assert.False(preview.CanRestore); Assert.True(preview.TargetIsActive);
        Assert.Contains(preview.Blockers, b => b.Contains("never tested against the active database"));
        var refused = await Assert.ThrowsAsync<DomainException>(() => rig.RestoreAsync(backup.BackupId, active));
        Assert.Equal("restore_blocked", refused.Code);
        // The same database under a different spelling of its name, and names that must never be targets.
        Assert.False((await scope.ServiceProvider.GetRequiredService<IRestoreService>().PreviewAsync(backup.BackupId, active.ToUpperInvariant())).CanRestore);
        foreach (var name in new[] { "master", "tempdb", "msdb", "model", "x]; DROP DATABASE y;--", "" })
            Assert.False((await scope.ServiceProvider.GetRequiredService<IRestoreService>().PreviewAsync(backup.BackupId, name)).CanRestore, name);
        // A wrong confirmation phrase is refused before anything else happens.
        var wrong = await Assert.ThrowsAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IRestoreService>().RestoreAsync(new RestoreRequest(backup.BackupId, rig.NewTarget(), "r", "yes please", Operator)));
        Assert.Equal("confirmation_required", wrong.Code);
        await Assert.ThrowsAsync<DomainException>(() => scope.ServiceProvider.GetRequiredService<IRestoreService>().RestoreAsync(new RestoreRequest(backup.BackupId, rig.NewTarget(), " ", RestorePhrases.For(backup.BackupId, false), Operator)));
    }

    [SqlServerBackupFact]
    public async Task The_disaster_recovery_path_restores_over_the_active_database_only_by_name_with_its_own_confirmation_and_validates_the_result()
    {
        await using var rig = await StartAsync();
        var backup = await rig.BackupAsync();
        // A change made after the backup: an organisation that the backup does not have.
        var added = await rig.WithAsync(async s => { using var _ = s.GetRequiredService<TenantContext>().Use(rig.Installation.TenantId); return await s.GetRequiredService<Ecp.Tenancy.ITenantProvisioningService>().AddOrganisationAsync(new Ecp.Tenancy.AddOrganisationRequest("After the backup", "Australia/Sydney", "en-AU", "AUD", "AFT", null)); });
        Assert.NotEqual(Guid.Empty, added);

        // The ordinary confirmation is not enough for the dangerous path.
        await using (var scope = rig.App.Services.CreateAsyncScope())
        {
            var restores = scope.ServiceProvider.GetRequiredService<IRestoreService>();
            var wrong = await Assert.ThrowsAsync<DomainException>(() => restores.RestoreAsync(new RestoreRequest(backup.BackupId, "", "dr", RestorePhrases.For(backup.BackupId, false), Operator, InPlaceDisasterRecovery: true)));
            Assert.Equal("confirmation_required", wrong.Code);
            var notActive = await restores.PreviewAsync(backup.BackupId, "", inPlace: true);
            Assert.True(notActive.TargetIsActive);
        }
        RestoreResult result;
        await using (var scope = rig.App.Services.CreateAsyncScope())
            result = await scope.ServiceProvider.GetRequiredService<IRestoreService>().RestoreAsync(new RestoreRequest(backup.BackupId, "", "Disaster recovery rehearsal", RestorePhrases.For(backup.BackupId, true), Operator, InPlaceDisasterRecovery: true));
        SqlConnection.ClearAllPools();
        Assert.True(result.Succeeded, result.Failure + string.Join("; ", result.Validation?.Checks.Where(c => c.Outcome != CheckOutcome.Passed).Select(c => c.Code + " " + c.Description + " " + c.TechnicalDetail) ?? []));
        Assert.True(result.Released);

        // The active database is the backup's content again: the organisation added afterwards is gone, the maintenance flag is off, the restore is on the record.
        await using var fresh = rig.App.RestartServices().CreateAsyncScope();
        using (fresh.ServiceProvider.GetRequiredService<TenantContext>().Use(rig.Installation.TenantId))
            Assert.False(await fresh.ServiceProvider.GetRequiredService<Ecp.Tenancy.ITenantDirectory>().OrganisationExistsAsync(added));
        Assert.False((await fresh.ServiceProvider.GetRequiredService<IMaintenanceService>().GetAsync()).Enabled);
        Assert.Contains(await fresh.ServiceProvider.GetRequiredService<IOperationHistory>().ListAsync(OperationKinds.Restore, 5), r => r.Status is OperationStatuses.Completed or OperationStatuses.CompletedWithWarnings);
    }

    [SqlServerBackupFact]
    public async Task A_database_backup_can_be_encrypted_with_a_server_certificate_and_the_manifest_says_so()
    {
        var certificate = "ecp_test_cert_" + Guid.NewGuid().ToString("N")[..8];
        var cleanup = new List<string>();
        await using var admin = new SqlConnection(SqlServerEnvironment.ConnectionString);
        await admin.OpenAsync();
        async Task Run(string sql) { await using var c = admin.CreateCommand(); c.CommandText = sql; await c.ExecuteNonQueryAsync(); }
        await Run("USE master; IF NOT EXISTS (SELECT 1 FROM sys.symmetric_keys WHERE name = '##MS_DatabaseMasterKey##') CREATE MASTER KEY ENCRYPTION BY PASSWORD = '" + "Aa1!" + Guid.NewGuid().ToString("N") + "';");
        await Run($"USE master; CREATE CERTIFICATE [{certificate}] WITH SUBJECT = 'ecp test backup certificate';");
        try
        {
            await using var rig = await StartAsync(l => l.Certificate = certificate);
            var backup = await rig.BackupAsync();
            Assert.True(backup.Succeeded, backup.Failure);
            var manifest = await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().LoadManifestAsync(backup.BackupId));
            Assert.True(manifest.Database.Encrypted); Assert.Equal(certificate, manifest.Database.EncryptionCertificate);
            Assert.True(manifest.Protection.DatabaseBackupEncrypted);
            Assert.Equal(VerificationOutcome.Passed, (await rig.WithAsync(s => s.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId))).Outcome);
            var restored = await rig.RestoreAsync(backup.BackupId, rig.NewTarget());
            Assert.True(restored.Succeeded, restored.Failure);
        }
        finally { await Run($"USE master; DROP CERTIFICATE [{certificate}];"); }
    }
}
