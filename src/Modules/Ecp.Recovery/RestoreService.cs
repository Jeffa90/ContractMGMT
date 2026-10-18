using System.Text.Json;
using Ecp.Audit;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.Operations;
using Ecp.Operations.Domain;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

/// <summary>Where a restore puts things. The connection string is a secret and is never shown or logged.</summary>
public sealed record RestoreTarget(string DatabaseName, string ConnectionString, string DocumentsDirectory, string PacksDirectory, bool InPlace);

/// <summary>An installation opened for reading beside the running one (a restored copy), with its own services, database connection, document store and package store. Disposing it closes everything.</summary>
public interface IRestoreTargetHost : IAsyncDisposable
{
    IServiceProvider Services { get; }
}

/// <summary>Implemented by the host, which knows how the modules are composed. Recovery asks for the same composition pointed at the restored copy, so validation exercises the real services.</summary>
public interface IRestoreTargetFactory
{
    IRestoreTargetHost Open(RestoreTarget target);
}

/// <summary>Where a side-by-side restore puts its files: one directory per target database under the restore area.</summary>
internal static class RestoreLayout
{
    public static (string Documents, string Packs) For(RecoveryOptions options, string targetDatabase)
    {
        var root = Path.Combine(Path.GetFullPath(options.RestoreTargetDirectory), targetDatabase);
        return (Path.Combine(root, "documents"), Path.Combine(root, "packs"));
    }
}

public static class RestorePhrases
{
    /// <summary>The words an operator types to confirm a restore. They name the backup, so a restore cannot be confirmed by habit.</summary>
    public static string For(string backupId, bool inPlace) => inPlace ? $"RESTORE {backupId} OVER THE ACTIVE DATABASE" : $"RESTORE {backupId}";
}

public sealed record RestoreRequest(string BackupId, string TargetDatabase, string Reason, string ConfirmationText, OperatorIdentity Operator, bool InPlaceDisasterRecovery = false,
    bool ReplaceExistingTarget = false, bool AllowDifferentEnvironment = false, string? ExpectedManifestSha256 = null, bool AcceptMissingDocumentContent = false);

public sealed record RestoreSummary(DateTime CreatedUtc, string PlatformVersion, string EnvironmentMode, string DatabaseEngine, string DatabaseName, bool Encrypted, bool Quiescent, long Tenants, long Contracts, long Counterparties,
    long AuditEvents, long DocumentObjects, long DocumentBytes, long Packages, int Warnings, string ManifestSha256);

public sealed record RestorePreview(string BackupId, bool CanRestore, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings, BackupVerification Verification, RestoreSummary? Summary, string TargetDatabase,
    string ConfirmationPhrase, string TargetDocumentsDirectory, string TargetPacksDirectory, bool TargetIsActive);

public sealed record RestoreResult(string BackupId, string TargetDatabase, string Outcome, ValidationReport? Validation, bool Released, string? Failure, string? EvidenceFile)
{
    public bool Succeeded => Outcome is "Succeeded" or "SucceededWithWarnings";
}

/// <summary>
/// Restore (docs/BACKUP_AND_RESTORE.md, "Restore"). A restore is performed into a separate target database, document directory and package directory, validated there, and only then released; the active database is
/// never the target unless the disaster-recovery path is chosen on purpose. It applies no migrations, regenerates no history and republishes no configuration. A failure leaves the installation in maintenance mode.
/// </summary>
public interface IRestoreService
{
    Task<RestorePreview> PreviewAsync(string backupId, string targetDatabase, bool inPlace = false, CancellationToken ct = default);
    Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken ct = default);
}

internal sealed class RestoreService(
    IServiceScopeFactory scopes, IOptions<RecoveryOptions> recovery, IDatabaseBackupProvider database, IBackupVerifier verifier, IRestoreTargetFactory targets, IInstallationValidator validator,
    IOperationHistory history, IMaintenanceService maintenance, OperationAudit audit, IClock clock, StartupFacts facts, ActorContext actor, LocalFileSystemOptions documentsOptions,
    IOptions<Ecp.Configuration.PackOptions> packOptions, EvidenceWriter evidence, ILogger<RestoreService> log) : IRestoreService
{
    private RecoveryOptions Options => recovery.Value;

    public async Task<RestorePreview> PreviewAsync(string backupId, string targetDatabase, bool inPlace = false, CancellationToken ct = default)
    {
        var blockers = new List<string>(); var warnings = new List<string>();
        var (documentsDirectory, packsDirectory) = TargetDirectories(targetDatabase, inPlace, blockers);
        var verification = await verifier.VerifyAsync(backupId, persist: false, ct);
        RestoreSummary? summary = null; var active = false;
        if (!database.Supported) blockers.Add(database.UnsupportedReason);
        if (verification.Outcome == VerificationOutcome.Failed) blockers.Add("The backup does not pass its integrity check: " + string.Join(" ", verification.Findings.Where(f => f.Severity == FindingSeverity.Failed).Take(3).Select(f => f.Message)));
        if (verification.Outcome == VerificationOutcome.NotVerified) blockers.Add("The backup could not be fully verified on this installation.");
        BackupManifest? manifest = null;
        if (verification.Outcome != VerificationOutcome.Failed)
        {
            try { manifest = await verifier.LoadManifestAsync(backupId, ct); } catch (DomainException ex) { blockers.Add(ex.Message); }
        }
        if (manifest is not null)
        {
            var b = manifest.InventoryAfter;
            summary = new RestoreSummary(manifest.CreatedUtc, manifest.PlatformVersion, manifest.EnvironmentMode, manifest.Database.Engine, manifest.Database.Name, manifest.Database.Encrypted, manifest.Quiescent, b.Tenants.Count(t => t.TenantId != SystemTenant.Id),
                b.Total("contracts.Contract") + b.Total("Contract"), b.Total("contracts.Supplier") + b.Total("Supplier"), b.Tenants.Sum(t => t.AuditEvents), manifest.Documents.ObjectCount, manifest.Documents.ByteTotal, manifest.Packs.FileCount, manifest.Warnings.Count, verification.ManifestSha256 ?? "");
            if (!string.Equals(manifest.EnvironmentMode, facts.Mode.ToString(), StringComparison.Ordinal)) blockers.Add($"This backup was made in {manifest.EnvironmentMode} mode and this installation runs in {facts.Mode} mode. Restoring across environments needs an explicit override: the data may be live and confidential.");
            if (!manifest.Documents.ContentBackedUp) blockers.Add("This backup does not contain document content. Restoring without it would leave documents missing; confirm explicitly that the document store is restored by other means.");
            warnings.AddRange(manifest.Warnings.Take(10));
            if (!string.Equals(manifest.PlatformVersion, typeof(RestoreService).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion, StringComparison.Ordinal))
                warnings.Add($"The backup was made by platform version {manifest.PlatformVersion}. Restore applies no migrations; validation reports any that are pending.");
        }
        if (database.Supported)
        {
            if (!SafeFiles.IsTargetDatabaseName(targetDatabase) && !inPlace) blockers.Add("The target database name must start with a letter and use only letters, digits and underscores, and cannot be a system database.");
            else
            {
                try
                {
                    var activeIdentity = await database.IdentifyActiveAsync(ct);
                    var targetIdentity = await database.IdentifyTargetAsync(inPlace ? database.ActiveDatabaseName : targetDatabase, ct);
                    active = targetIdentity.Same(activeIdentity);
                    if (active && !inPlace) blockers.Add("The target is the database this installation is using. A restore is never tested against the active database; choose a new database name.");
                    if (!active && inPlace) blockers.Add("The disaster-recovery path restores over the active database only.");
                    if (!inPlace && SafeFiles.IsTargetDatabaseName(targetDatabase) && await database.DatabaseExistsAsync(targetDatabase, ct)) blockers.Add("A database with the target name already exists. Choose a new name; nothing is overwritten silently.");
                }
                catch (DomainException ex) { blockers.Add(ex.Message); }
            }
        }
        return new RestorePreview(backupId, blockers.Count == 0, blockers, warnings, verification, summary, inPlace ? database.ActiveDatabaseName : targetDatabase, RestorePhrases.For(backupId, inPlace), documentsDirectory, packsDirectory, active);
    }

    private (string Documents, string Packs) TargetDirectories(string targetDatabase, bool inPlace, List<string> blockers)
    {
        if (inPlace) return (Path.GetFullPath(documentsOptions.RootPath), Path.GetFullPath(packOptions.Value.ResolvedStorageDirectory));
        if (!Options.RestoreConfigured) { blockers.Add("No restore target directory is configured (Ecp:Recovery:RestoreTargetDirectory)."); return ("", ""); }
        if (!SafeFiles.IsTargetDatabaseName(targetDatabase)) return ("", "");
        var root = Path.Combine(Path.GetFullPath(Options.RestoreTargetDirectory), targetDatabase);
        foreach (var directory in new[] { RestoreLayout.For(Options, targetDatabase).Documents, RestoreLayout.For(Options, targetDatabase).Packs })
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) blockers.Add($"The target directory {Path.GetFileName(root)}/{Path.GetFileName(directory)} is not empty. A restore never merges into existing files.");
        return RestoreLayout.For(Options, targetDatabase);
    }

    public async Task<RestoreResult> RestoreAsync(RestoreRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new DomainException("reason_required", "A reason is required to restore.");
        request = request with { Reason = Redaction.Clean(request.Reason.Trim()) };
        if (!string.Equals(request.ConfirmationText?.Trim(), RestorePhrases.For(request.BackupId, request.InPlaceDisasterRecovery), StringComparison.Ordinal))
            throw new DomainException("confirmation_required", $"Type exactly “{RestorePhrases.For(request.BackupId, request.InPlaceDisasterRecovery)}” to confirm the restore.");
        var preview = await PreviewAsync(request.BackupId, request.TargetDatabase, request.InPlaceDisasterRecovery, ct);
        var blockers = preview.Blockers.ToList();
        if (request.AllowDifferentEnvironment) blockers.RemoveAll(b => b.Contains("Restoring across environments", StringComparison.Ordinal));
        if (request.AcceptMissingDocumentContent) blockers.RemoveAll(b => b.Contains("does not contain document content", StringComparison.Ordinal));
        if (blockers.Count > 0) throw new DomainException("restore_blocked", "The restore cannot start: " + string.Join(" ", blockers));
        var manifest = await verifier.LoadManifestAsync(request.BackupId, ct);
        if (request.ExpectedManifestSha256 is { Length: > 0 } expected && !string.Equals(expected, preview.Verification.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("manifest_unexpected", "The manifest is not the one you expected (its SHA-256 differs). Do not restore a backup you cannot account for.");

        var owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        await using var lease = await LeaseHolder.TryAcquireAsync(scopes, RecoveryLeases.Exclusive, IOperationLeases.InstallationScope, owner, $"restore {request.BackupId}", TimeSpan.FromMinutes(Math.Max(2, Options.LeaseMinutes)), ct)
            ?? throw new DomainException("operation_in_progress", await RecoveryLeases.DescribeBusyAsync(scopes, ct));

        var targetName = request.InPlaceDisasterRecovery ? database.ActiveDatabaseName : request.TargetDatabase;
        var started = clock.UtcNow;
        var correlation = actor.CorrelationId;
        Guid? runId = null;
        if (!request.InPlaceDisasterRecovery) runId = await history.StartAsync(OperationKinds.Restore, targetName, request.Reason, request.Operator, ct);
        await audit.RecordAsync(new AuditEntry("operations.restore.started", "Restore", request.BackupId, Reason: request.Reason.Trim(),
            NewValuesJson: JsonSerializer.Serialize(new { backupId = request.BackupId, target = targetName, inPlace = request.InPlaceDisasterRecovery, manifestSha256 = preview.Verification.ManifestSha256, @operator = request.Operator.Id })), ct);

        var before = await maintenance.GetAsync(ct);
        await maintenance.EnableAsync($"Restore of {request.BackupId} in progress", request.Operator, MaintenanceCauses.RestoreInProgress, ct);
        ValidationReport? report = null; string? failure = null; var released = false;
        var connectionString = database.ConnectionStringFor(targetName);
        var target = new RestoreTarget(targetName, connectionString, preview.TargetDocumentsDirectory, preview.TargetPacksDirectory, request.InPlaceDisasterRecovery);
        try
        {
            // 1. Verify again, inside the lease: the backup could have changed since the preview.
            var verification = await verifier.VerifyAsync(request.BackupId, persist: false, ct);
            if (verification.Outcome != VerificationOutcome.Passed && verification.Outcome != VerificationOutcome.PassedWithWarnings)
                throw new DomainException("verification_failed", "The backup failed its integrity check, so nothing was restored.");
            lease.ThrowIfLost();

            // 2. Database.
            var directory = SafeFiles.ChildDirectory(Options.BackupDirectory, request.BackupId);
            await database.RestoreAsync(new DatabaseRestoreRequest(RecoveryPaths.ToSqlPath(Options, request.BackupId, manifest.Database.File), targetName, request.ReplaceExistingTarget, request.InPlaceDisasterRecovery), ct);
            if (!request.InPlaceDisasterRecovery) lease.ThrowIfLost();

            // 3. Documents and packages, through the same safe, hash-checked paths.
            var restoredDocuments = await RestoreDocumentsAsync(directory, manifest, target, ct);
            var restoredPackages = await RestorePackagesAsync(directory, manifest, target, ct);

            // 4. Open the restored copy and hold it in maintenance until validation passes.
            await using var host = targets.Open(target);
            await ReleaseCopiedLeasesAsync(host, request, ct);
            var operationsAvailable = await SetTargetMaintenanceAsync(host, $"Restored from {request.BackupId}; validation pending", MaintenanceCauses.RestoreValidationPending, request.Operator, enable: true, ct);

            // 5. Validate.
            report = await validator.ValidateAsync(new ValidationTarget(host.Services, connectionString, targetName, Restored: true, manifest, HashDocumentContent: true, Subject: $"{request.BackupId} → {targetName}"), ct);
            if (report.AllowsRelease)
            {
                if (operationsAvailable) await SetTargetMaintenanceAsync(host, $"Restore of {request.BackupId} validated ({report.Outcome})", MaintenanceCauses.Manual, request.Operator, enable: false, ct);
                released = true;
            }
            else if (operationsAvailable)
                await SetTargetMaintenanceAsync(host, $"Restore of {request.BackupId} failed validation", MaintenanceCauses.RestoreFailed, request.Operator, enable: true, ct);
            await RecordLineageAsync(host, request, report, restoredDocuments, restoredPackages, ct);
        }
        catch (Exception ex)
        {
            failure = Redaction.Clean(ex is DomainException or IOException or UnauthorizedAccessException ? ex.Message : "The restore failed unexpectedly (" + ex.GetType().Name + "). See the application log with the correlation id.");
            log.LogError(ex, "Restore of {BackupId} into {Target} failed.", request.BackupId, targetName);
        }

        var outcome = failure is not null ? "Failed" : report is { Outcome: ValidationOutcome.Passed } ? "Succeeded" : report is { AllowsRelease: true } ? "SucceededWithWarnings" : "Failed";
        if (outcome == "Failed" && failure is null) failure = $"Post-restore validation did not pass ({report?.Outcome}). The restored copy was not released.";
        var evidenceFile = await evidence.WriteRestoreAsync(request, preview, report, outcome, failure, started, clock.UtcNow, correlation, ct);

        // 6. The active installation's maintenance mode: released only when the restore was validated.
        try
        {
            if (request.InPlaceDisasterRecovery) { /* the active database is the restored one; its state was set through the restored copy above */ }
            else if (outcome != "Failed")
            {
                if (before.Enabled) await maintenance.EnableAsync(before.Reason, request.Operator, before.Cause, ct);
                else await maintenance.DisableAsync($"Restore of {request.BackupId} validated", request.Operator, acknowledgeRestoreState: true, ct);
            }
            else await maintenance.EnableAsync($"Restore of {request.BackupId} failed: {Short(failure)}", request.Operator, MaintenanceCauses.RestoreFailed, ct);
            var status = outcome == "Failed" ? OperationStatuses.Failed : outcome == "Succeeded" ? OperationStatuses.Completed : OperationStatuses.CompletedWithWarnings;
            var summary = outcome == "Failed" ? $"Restore failed: {Short(failure)}" : $"Restore into {targetName} validated ({report?.Outcome}).";
            var detail = JsonSerializer.Serialize(new { backupId = request.BackupId, target = targetName, validation = report?.Outcome.ToString(), checks = report?.Checks.Count, failed = report?.Count(CheckOutcome.Failed), warnings = report?.Count(CheckOutcome.Warning), evidence = evidenceFile });
            if (runId is { } id) await history.FinishAsync(id, status, summary, detail, CancellationToken.None);
            else
            {
                var recorded = await history.StartAsync(OperationKinds.Restore, targetName, request.Reason, request.Operator, CancellationToken.None);
                await history.FinishAsync(recorded, status, summary, detail, CancellationToken.None);
            }
            await audit.RecordAsync(new AuditEntry(outcome == "Failed" ? "operations.restore.failed" : "operations.restore.completed", "Restore", request.BackupId, outcome == "Failed" ? AuditOutcome.Failed : AuditOutcome.Success,
                Reason: request.Reason.Trim(), NewValuesJson: detail), CancellationToken.None);
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException or Microsoft.Data.SqlClient.SqlException)
        {
            log.LogError(ex, "Restore bookkeeping for {BackupId} failed after the restore finished.", request.BackupId);
        }
        return new RestoreResult(request.BackupId, targetName, outcome, report, released && outcome != "Failed", failure, evidenceFile);
    }

    private static string Short(string? s) => s is null ? "" : s.Length <= 200 ? s : s[..200] + "…";

    private async Task<long> RestoreDocumentsAsync(string backupDirectory, BackupManifest manifest, RestoreTarget target, CancellationToken ct)
    {
        if (!manifest.Documents.ContentBackedUp || manifest.Documents.IndexFile is null) return 0;
        var provider = new LocalFileSystemStorageProvider(new LocalFileSystemOptions { RootPath = target.DocumentsDirectory });
        long count = 0;
        foreach (var line in DocumentStoreCopier.ReadIndex(SafeFiles.Resolve(backupDirectory, manifest.Documents.IndexFile)))
        {
            ct.ThrowIfCancellationRequested();
            if (!line.HashMatchesKey) continue;       // already corrupt at the source; recorded as a backup warning, and validation counts it against the database
            var path = SafeFiles.Resolve(backupDirectory, ManifestFiles.DocumentsDirectory + "/" + line.Key);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await provider.PutAsync(DocumentStoreCopier.TenantOf(line.Key), stream, long.MaxValue, ct);
            if (!string.Equals(stored.StorageKey, line.Key, StringComparison.Ordinal) || !string.Equals(stored.Sha256, line.Sha256, StringComparison.Ordinal))
                throw new DomainException("document_mismatch", "A document object did not restore to the address it was backed up under. The restore stopped; nothing was released.");
            count++;
        }
        return count;
    }

    private async Task<long> RestorePackagesAsync(string backupDirectory, BackupManifest manifest, RestoreTarget target, CancellationToken ct)
    {
        long count = 0;
        foreach (var file in manifest.Files.Where(f => f.Kind == "package"))
        {
            ct.ThrowIfCancellationRequested();
            var source = SafeFiles.Resolve(backupDirectory, file.Path);
            var relative = file.Path[(ManifestFiles.PacksDirectory.Length + 1)..];
            var destination = SafeFiles.Resolve(target.PacksDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                if ((await SafeFiles.HashAsync(destination, ct)).Sha256 != file.Sha256) throw new DomainException("package_mismatch", "A different package file already exists at the target.");
            }
            else File.Copy(source, destination, overwrite: false);
            if ((await SafeFiles.HashAsync(destination, ct)).Sha256 != file.Sha256) throw new DomainException("package_mismatch", "A package file did not restore intact.");
            count++;
        }
        return count;
    }

    /// <summary>The backup was made while the backup itself held the recovery lease; that lease row came along in the copy. It belongs to a process that does not exist here, so it is released (and the release is audited in the copy).</summary>
    private static async Task ReleaseCopiedLeasesAsync(IRestoreTargetHost host, RestoreRequest request, CancellationToken ct)
    {
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ActorContext>().ActorId = request.Operator.Id;
            await scope.ServiceProvider.GetRequiredService<IOperationLeases>().ReleaseAllAsync($"Restored from {request.BackupId}: leases recorded in the backup belong to the process that made it", ct);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException) { /* a copy without operational tables has no leases */ }
    }

    /// <summary>Sets the maintenance flag inside the restored copy. Returns false if the copy predates the operational tables (restore never migrates it).</summary>
    private static async Task<bool> SetTargetMaintenanceAsync(IRestoreTargetHost host, string reason, string cause, OperatorIdentity by, bool enable, CancellationToken ct)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IMaintenanceService>();
        try
        {
            if (enable) await service.EnableAsync(reason, by, cause, ct);
            else await service.DisableAsync(reason, by, acknowledgeRestoreState: true, ct);
            return true;
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException) { return false; }
    }

    /// <summary>Adds one event to the system chain of the restored copy saying where it came from. Existing events are untouched.</summary>
    private static async Task RecordLineageAsync(IRestoreTargetHost host, RestoreRequest request, ValidationReport report, long documents, long packages, CancellationToken ct)
    {
        try
        {
            await using var scope = host.Services.CreateAsyncScope();
            var audit = scope.ServiceProvider.GetRequiredService<OperationAudit>();
            scope.ServiceProvider.GetRequiredService<ActorContext>().ActorId = request.Operator.Id;
            await audit.RecordAsync(new AuditEntry("operations.restore.lineage", "Restore", request.BackupId, Reason: request.Reason.Trim(),
                NewValuesJson: JsonSerializer.Serialize(new { backupId = request.BackupId, validation = report.Outcome.ToString(), documentsRestored = documents, packagesRestored = packages })), ct);
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or Microsoft.EntityFrameworkCore.DbUpdateException or InvalidOperationException) { /* a copy without audit tables cannot record lineage; validation says so */ }
    }
}
