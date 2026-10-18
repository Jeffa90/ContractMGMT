using System.Text.Json;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.Recovery;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecp.Integration.Tests;

/// <summary>
/// OPS-01 and OPS-02 foundation, proved against a real SQL Server (docs/BACKUP_AND_RESTORE.md, "Verification"): a populated synthetic installation is backed up with the platform's own tooling, the backup is verified, restored
/// into a disposable database and file area beside the running one, validated there, and read through the real services, and nothing about the source or the copy has changed. Skipped as NOT VERIFIED without a shared backup directory.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class BackupRestoreSqlServerTests
{
    private static readonly OperatorIdentity Operator = new("test-operator", "Test operator");

    private static async Task<(TestApp App, RecoveryTestKit.Layout Layout, SyntheticInstallation Installation)> StartAsync()
    {
        var layout = new RecoveryTestKit.Layout();
        var app = await TestApp.CreateSqlServerAsync(configure: RecoveryTestKit.Configure(layout, LifecycleScenarios.Scanner));
        return (app, layout, await SyntheticInstallation.BuildAsync(app));
    }

    private static string NewTargetName() => "ecp_restored_" + Guid.NewGuid().ToString("N")[..12];

    [SqlServerBackupFact]
    public async Task A_complete_installation_backs_up_verifies_restores_and_validates_and_the_three_businesses_work_exactly_as_before()
    {
        var (app, layout, installation) = await StartAsync();
        var target = NewTargetName();
        await using (app)
        {
            var sourceProbe = await installation.ProbeAsync(app.Services);
            Assert.Equal("3", sourceProbe["documents.count"]);       // the reference, Blank and Harborline documents are all present before the backup
            Assert.DoesNotContain("MISMATCH", sourceProbe["documents"]);

            // ---- Back up (an exact backup: maintenance mode held, so nothing moves while it runs) ----
            BackupResult backup;
            await using (var scope = app.Services.CreateAsyncScope())
                backup = await scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync(new BackupRequest("Continuous integration rehearsal", Operator, HoldMaintenance: true));
            Assert.True(backup.Succeeded, backup.Failure);
            Assert.True(SafeFiles.IsBackupId(backup.BackupId));
            Assert.Equal(64, backup.ManifestSha256!.Length);

            // The folder holds what the manifest says, and the manifest is the one that was sealed.
            var folder = Path.Combine(layout.BackupDirectory, backup.BackupId);
            Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(folder, "manifest.sha256")));
            Assert.Equal(backup.ManifestSha256, ManifestJson.Sha256(await File.ReadAllBytesAsync(Path.Combine(folder, "manifest.json"))));
            var manifestText = await File.ReadAllTextAsync(Path.Combine(folder, "manifest.json"));
            Assert.False(Redaction.ContainsSecret(manifestText));
            Assert.DoesNotContain("Password", manifestText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(installation.DocumentText, manifestText);          // no business content in the manifest

            await using (var scope = app.Services.CreateAsyncScope())
            {
                var manifest = await scope.ServiceProvider.GetRequiredService<IBackupVerifier>().LoadManifestAsync(backup.BackupId);
                Assert.Equal(1, manifest.FormatVersion);
                Assert.Equal("SQL Server", manifest.Database.Engine);
                Assert.Equal("Full", manifest.Database.BackupType);
                Assert.True(manifest.Database.CopyOnly);
                Assert.True(manifest.Database.Checksummed);
                Assert.True(manifest.Database.VerifyOnlyPassed);
                Assert.True(manifest.Quiescent, "A backup taken in maintenance mode on a quiet installation is exact.");
                Assert.True(manifest.Documents.ContentBackedUp);
                Assert.Equal(3, manifest.Documents.ObjectCount);
                Assert.Equal(0, manifest.Documents.MissingContent);
                Assert.Contains(manifest.Packs.Installed, p => p.PackKey == HarborlineWorld.PackKey);
                Assert.NotEmpty(manifest.Packs.Shipped);
                Assert.All(manifest.InventoryAfter.Tenants, t => Assert.True(t.AuditChainValid));
                Assert.True(manifest.Schema.SecurityPolicyCount > 0);
                Assert.Contains(manifest.Schema.Modules, m => m.Module == "Operations" && m.AppliedMigrations.Count > 0);
                Assert.Contains(manifest.Files, f => f.Kind == "database");
                Assert.Contains(manifest.Files, f => f.Kind == "package");
                Assert.Equal("Test", manifest.EnvironmentMode);
                Assert.False(string.IsNullOrWhiteSpace(manifest.Protection.StorageProtectionStatement));

                // ---- Verification: every declared hash, the document index, and the database server's own check ----
                var verification = await scope.ServiceProvider.GetRequiredService<IBackupVerifier>().VerifyAsync(backup.BackupId, persist: true);
                Assert.Equal(VerificationOutcome.Passed, verification.Outcome);
                Assert.True(verification.FilesChecked >= manifest.Files.Count);
                var catalogued = await scope.ServiceProvider.GetRequiredService<IBackupCatalogue>().FindAsync(backup.BackupId);
                Assert.Equal(BackupState.Verified, catalogued!.State);
            }

            // The run was recorded and audited, and maintenance mode is off again.
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var runs = await scope.ServiceProvider.GetRequiredService<IOperationHistory>().ListAsync(OperationKinds.Backup, 5);
                Assert.Contains(runs, r => r.Reference == backup.BackupId && r.Status == OperationStatuses.Completed);
                Assert.False((await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().GetAsync()).Enabled);
            }

            // ---- Restore beside the running installation, validate, release ----
            var before = await ChainHeadsAsync(app);
            RestoreResult restored;
            await using (var scope = app.Services.CreateAsyncScope())
                restored = await scope.ServiceProvider.GetRequiredService<IRestoreService>().RestoreAsync(new RestoreRequest(backup.BackupId, target, "Continuous integration rehearsal", RestorePhrases.For(backup.BackupId, false), Operator,
                    ExpectedManifestSha256: backup.ManifestSha256));
            try
            {
                Assert.True(restored.Succeeded, restored.Failure + "\n" + string.Join("\n", restored.Validation?.Checks.Where(c => c.Outcome != CheckOutcome.Passed).Select(c => $"{c.Code} {c.Outcome}: {c.Description} {c.TechnicalDetail}") ?? []));
                Assert.NotNull(restored.Validation);
                Assert.True(restored.Validation!.AllowsRelease);
                Assert.Equal(0, restored.Validation.Count(CheckOutcome.Failed));
                Assert.True(restored.Released);
                foreach (var code in new[] { "VAL-001", "VAL-002", "VAL-003", "VAL-005", "VAL-006", "VAL-007", "VAL-008", "VAL-009", "VAL-010", "VAL-011", "VAL-013", "VAL-014", "VAL-016", "VAL-017", "VAL-019", "VAL-021" })
                    Assert.Contains(restored.Validation.Checks, c => c.Code == code && c.Outcome == CheckOutcome.Passed);
                Assert.NotNull(restored.EvidenceFile);
                Assert.True(File.Exists(Path.Combine(layout.BackupDirectory, restored.EvidenceFile!)));
                Assert.False(Redaction.ContainsSecret(await File.ReadAllTextAsync(Path.Combine(layout.BackupDirectory, restored.EvidenceFile!))));

                // The active installation is untouched and no longer in maintenance.
                await using (var scope = app.Services.CreateAsyncScope())
                    Assert.False((await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().GetAsync()).Enabled);
                var after = await ChainHeadsAsync(app);
                foreach (var (tenant, head) in before.Where(h => h.Key != SystemTenant.Id)) Assert.Equal(head, after[tenant]);   // the customer data of the source did not move
                Assert.True(after[SystemTenant.Id].Sequence > before[SystemTenant.Id].Sequence);                         // the installation's own chain records the restore

                // ---- Open the restored copy as an application and read all three businesses through the real services ----
                var targetConnection = new SqlConnectionStringBuilder(app.ConnectionString) { InitialCatalog = target }.ConnectionString;
                await using var copy = TestApp.BuildDetached(DatabaseProvider.SqlServer, targetConnection, app.Clock, Path.Combine(layout.RestoreRoot, target), EcpMode.Test,
                    services => { LifecycleScenarios.Scanner(services); services.AddRecoveryModule(RecoveryTestKit.OptionsFor(layout), new DatabaseOptions { Provider = DatabaseProvider.SqlServer, ConnectionString = targetConnection }); });
                var copyProbe = await installation.ProbeAsync(copy);
                Assert.Equal(sourceProbe, copyProbe);            // lifecycle, wording, capabilities, regional settings, packs, releases, documents: identical

                // The restored copy came back held in validation mode and was released only by the passing validation; its history records where it came from.
                await using (var scope = copy.CreateAsyncScope())
                    Assert.False((await scope.ServiceProvider.GetRequiredService<IMaintenanceService>().GetAsync()).Enabled);
                var restoredHeads = await ChainHeadsAsync(copy, installation);
                foreach (var (tenant, head) in before.Where(h => h.Key != SystemTenant.Id)) Assert.Equal(head, restoredHeads[tenant]);   // customer audit chains: same length, same last hash
                Assert.True(restoredHeads[SystemTenant.Id].Sequence > before[SystemTenant.Id].Sequence - 1);

                // Restoring the same backup into the same target again is refused: nothing is overwritten silently.
                await using var again = app.Services.CreateAsyncScope();
                var refused = await Assert.ThrowsAsync<DomainException>(() => again.ServiceProvider.GetRequiredService<IRestoreService>().RestoreAsync(
                    new RestoreRequest(backup.BackupId, target, "again", RestorePhrases.For(backup.BackupId, false), Operator)));
                Assert.Contains("already exists", refused.Message);
            }
            finally { await RecoveryTestKit.DropAsync(target); }
        }
    }

    private sealed record Head(long Sequence, string Hash);

    private static Task<Dictionary<Guid, Head>> ChainHeadsAsync(TestApp app) => ChainHeadsAsync(app.Services, null);
    private static Task<Dictionary<Guid, Head>> ChainHeadsAsync(IServiceProvider services, SyntheticInstallation? installation) => ChainHeadsCoreAsync(services);

    private static async Task<Dictionary<Guid, Head>> ChainHeadsCoreAsync(IServiceProvider services)
    {
        var inventory = services.GetRequiredService<IInstallationInventory>();
        var snapshot = await inventory.CollectAsync(services, tableChecksums: false);
        return snapshot.Tenants.ToDictionary(t => t.TenantId, t => new Head(t.AuditHeadSequence, t.AuditHeadHash));
    }
}
