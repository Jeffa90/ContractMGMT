using System.Reflection;
using System.Text.Json;
using Ecp.Audit;
using Ecp.Configuration;
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

public sealed record BackupRequest(string Reason, OperatorIdentity Operator, bool HoldMaintenance = false);
public sealed record BackupResult(string BackupId, string Outcome, string? ManifestSha256, IReadOnlyList<string> Warnings, string? Failure)
{
    public bool Succeeded => Outcome is "Completed" or "CompletedWithWarnings";
}

/// <summary>Creates a complete installation backup (docs/BACKUP_AND_RESTORE.md). A backup is not reported as successful until its integrity has been checked.</summary>
public interface IBackupService
{
    Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken ct = default);
}

/// <summary>Records an audit event for an installation-level operation, in its own unit of work, in the system tenant's chain.</summary>
internal sealed class OperationAudit(IAuditWriter audit, IUnitOfWork uow, TenantContext tenant)
{
    public async Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        using var _ = tenant.UseInstallationScope();
        await uow.ExecuteAsync(_ => { audit.Record(entry); return Task.CompletedTask; }, ct);
    }
}

internal sealed class BackupService(
    IServiceProvider root, IServiceScopeFactory scopes, IOptions<RecoveryOptions> recovery, IDatabaseBackupProvider database, IInstallationInventory inventory, IBackupVerifier verifier,
    IDocumentStorageProvider documentStorage, IOptions<PackOptions> packOptions, IOperationHistory history, IMaintenanceService maintenance, OperationAudit audit, IClock clock,
    StartupFacts facts, ActorContext actor, ILogger<BackupService> log, IEnumerable<IDocumentStoreInventory> stores) : IBackupService
{
    private RecoveryOptions Options => recovery.Value;

    public async Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new DomainException("reason_required", "A reason is required to create a backup.");
        request = request with { Reason = Redaction.Clean(request.Reason.Trim()) };      // the reason is written into the manifest and the audit trail: a secret typed into it is masked first
        Preflight();
        var started = clock.UtcNow;
        var id = SafeFiles.NewBackupId(started);
        var directory = SafeFiles.ChildDirectory(Options.BackupDirectory, id);
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        await using var lease = await LeaseHolder.TryAcquireAsync(scopes, RecoveryLeases.Exclusive, IOperationLeases.InstallationScope, owner, $"backup {id}", TimeSpan.FromMinutes(Math.Max(2, Options.LeaseMinutes)), ct)
            ?? throw new DomainException("operation_in_progress", await RecoveryLeases.DescribeBusyAsync(scopes, ct));

        var runId = await history.StartAsync(OperationKinds.Backup, id, request.Reason, request.Operator, ct);
        await audit.RecordAsync(new AuditEntry("operations.backup.started", "Backup", id, Reason: request.Reason.Trim(), NewValuesJson: JsonSerializer.Serialize(new { backupId = id, @operator = request.Operator.Id })), ct);
        var heldMaintenance = false;
        try
        {
            Directory.CreateDirectory(directory);
            SafeFiles.Private(directory);
            WriteStatus(directory, id, "Running", "Started", null);

            if (request.HoldMaintenance && !(await maintenance.GetAsync(ct)).Enabled)
            {
                await maintenance.EnableAsync($"Backup {id} in progress", request.Operator, MaintenanceCauses.Manual, ct);
                heldMaintenance = true;
            }
            var quiet = (await maintenance.GetAsync(ct)).Enabled;

            WriteStatus(directory, id, "Running", "Inventory (before)", null);
            var before = await inventory.CollectAsync(root, Options.TableChecksums, ct);
            lease.ThrowIfLost();

            WriteStatus(directory, id, "Running", "Database backup", null);
            var warnings = new List<string>();
            var databaseSection = await BackupDatabaseAsync(directory, id, started, ct);
            lease.ThrowIfLost();

            WriteStatus(directory, id, "Running", "Document store", null);
            var (documentSection, indexFiles, documentWarnings) = await BackupDocumentsAsync(directory, ct);
            warnings.AddRange(documentWarnings);
            lease.ThrowIfLost();

            WriteStatus(directory, id, "Running", "Packages and trust store", null);
            var (packSection, packFiles, packWarnings) = await BackupPacksAsync(directory, ct);
            warnings.AddRange(packWarnings);

            WriteStatus(directory, id, "Running", "Inventory (after)", null);
            var after = await inventory.CollectAsync(root, Options.TableChecksums, ct);
            var schema = await SchemaAsync(ct);
            var quiescent = quiet && InventoryComparer.Equivalent(before, after);
            if (!InventoryComparer.Equivalent(before, after))
                warnings.Add("The installation changed while the backup was running, so counts in the manifest are ranges (before and after), not single values. Use maintenance mode for an exact backup.");
            foreach (var t in after.Tenants.Where(t => !t.AuditChainValid))
                warnings.Add($"The audit chain of tenant {t.Name} did not verify at backup time ({Redaction.Clean(t.AuditChainProblem)}). The backup holds it as it was.");
            if (!databaseSection.Encrypted && string.IsNullOrWhiteSpace(Options.StorageProtection))
                warnings.Add("The database backup is not encrypted and no storage protection statement is configured. A backup holds confidential data: protect it with your approved storage process.");

            var inventoryBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { before, after }, ManifestJson.Options) + "\n");
            var inventoryPath = SafeFiles.Resolve(directory, ManifestFiles.Inventory);
            await File.WriteAllBytesAsync(inventoryPath, inventoryBytes, ct);

            var files = new List<BackupFile> { new(databaseSection.File, "database", databaseSection.Bytes, databaseSection.Sha256), new(ManifestFiles.Inventory, "inventory", inventoryBytes.Length, SafeFiles.Sha256Of(inventoryBytes)) };
            files.AddRange(indexFiles);
            files.AddRange(packFiles);
            var finished = clock.UtcNow;
            var manifest = new BackupManifest(1, id, "installation", started, finished, PlatformVersionText(), facts.Mode.ToString(), new OperatorEntry(request.Operator.Id, request.Operator.Name), request.Reason.Trim(),
                actor.CorrelationId, databaseSection, schema, documentSection, packSection, before, after, quiescent,
                new ProtectionSection(databaseSection.Encrypted, databaseSection.EncryptionCertificate, Redaction.Clean(Options.StorageProtection),
                    "Backup files are not encrypted by the platform. Encrypt or protect the backup storage as your approved process requires; the platform records the statement above but cannot verify it."),
                files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(), warnings.Count == 0 ? "Completed" : "CompletedWithWarnings", warnings);
            var manifestBytes = ManifestJson.Serialise(manifest);
            var text = System.Text.Encoding.UTF8.GetString(manifestBytes);
            if (Redaction.ContainsSecret(text)) throw new DomainException("secret_in_manifest", "The manifest was not written because it appears to contain a secret. Nothing was stored.");
            var manifestHash = ManifestJson.Sha256(manifestBytes);
            await File.WriteAllBytesAsync(SafeFiles.Resolve(directory, ManifestFiles.Manifest), manifestBytes, ct);
            await File.WriteAllTextAsync(SafeFiles.Resolve(directory, ManifestFiles.ManifestHash), manifestHash + "  " + ManifestFiles.Manifest + "\n", ct);
            WriteStatus(directory, id, "Written", "Manifest written; verifying", manifestHash);

            // Success is declared only after the backup has been read back and checked.
            var verification = await verifier.VerifyAsync(id, persist: true, ct);
            if (verification.Outcome == VerificationOutcome.Failed)
            {
                WriteStatus(directory, id, "VerificationFailed", "Integrity check failed", manifestHash);
                var why = string.Join(" ", verification.Findings.Where(f => f.Severity == FindingSeverity.Failed).Take(3).Select(f => f.Message));
                await FinishAsync(runId, id, OperationStatuses.Failed, "The backup was written but its integrity check failed. Do not use it. " + why, new { manifestSha256 = manifestHash, verification = verification.Outcome.ToString() }, "operations.backup.failed", request.Reason, ct);
                return new BackupResult(id, "Failed", manifestHash, warnings, "The integrity check of the new backup failed: " + why);
            }
            if (verification.Outcome == VerificationOutcome.PassedWithWarnings) warnings.AddRange(verification.Findings.Where(f => f.Severity == FindingSeverity.Warning).Select(f => f.Message));
            var status = warnings.Count == 0 ? OperationStatuses.Completed : OperationStatuses.CompletedWithWarnings;
            await FinishAsync(runId, id, status, $"Backup {id} created and verified ({files.Count} files, {documentSection.ObjectCount} documents).",
                new { manifestSha256 = manifestHash, files = files.Count, documents = documentSection.ObjectCount, warnings = warnings.Count, quiescent }, "operations.backup.completed", request.Reason, ct);
            return new BackupResult(id, warnings.Count == 0 ? "Completed" : "CompletedWithWarnings", manifestHash, warnings, null);
        }
        catch (Exception ex)
        {
            var message = Redaction.Clean(ex is DomainException or IOException or UnauthorizedAccessException ? ex.Message : "The backup failed unexpectedly (" + ex.GetType().Name + "). See the application log with the correlation id.");
            log.LogError(ex, "Backup {BackupId} failed.", id);
            DiscardPartial(directory, id, message);
            await FinishAsync(runId, id, OperationStatuses.Incomplete, "The backup did not finish and is not a valid recovery point. " + message, new { failure = message }, "operations.backup.failed", request.Reason, CancellationToken.None);
            return new BackupResult(id, "Failed", null, [], message);
        }
        finally
        {
            if (heldMaintenance) { try { await maintenance.DisableAsync($"Backup {id} finished", request.Operator, false, CancellationToken.None); } catch (DomainException) { } }
        }
    }

    private async Task FinishAsync(Guid runId, string id, string status, string summary, object detail, string action, string reason, CancellationToken ct)
    {
        await history.FinishAsync(runId, status, summary, JsonSerializer.Serialize(detail), ct);
        await audit.RecordAsync(new AuditEntry(action, "Backup", id, status == OperationStatuses.Completed || status == OperationStatuses.CompletedWithWarnings ? AuditOutcome.Success : AuditOutcome.Failed, Reason: reason.Trim(),
            NewValuesJson: JsonSerializer.Serialize(new { backupId = id, status, detail })), ct);
    }

    private void Preflight()
    {
        if (!database.Supported) throw new DomainException("backup_unsupported", database.UnsupportedReason);
        if (!Options.BackupConfigured) throw new DomainException("backup_not_configured", "No backup directory is configured. Set Ecp:Recovery:BackupDirectory to a protected directory outside the web content, shared with the database server.");
        if (!Path.IsPathRooted(Options.BackupDirectory)) throw new DomainException("backup_not_configured", "The backup directory must be an absolute path.");
        if (!SafeFiles.IsOutsideWebRoot(Options.BackupDirectory, Options.ServedRoots)) throw new DomainException("backup_not_configured", "The backup directory is inside a path the web server serves. Choose a directory outside the web content.");
        Directory.CreateDirectory(Options.BackupDirectory);
        var probe = Path.Combine(Options.BackupDirectory, ".write-test-" + Guid.NewGuid().ToString("N"));
        try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new DomainException("backup_not_writable", "The backup directory is not writable by the application."); }
    }

    private async Task<DatabaseSection> BackupDatabaseAsync(string directory, string id, DateTime startedUtc, CancellationToken ct)
    {
        var databaseDirectory = SafeFiles.ChildDirectory(directory, ManifestFiles.DatabaseDirectory);
        Directory.CreateDirectory(databaseDirectory);
        OpenForDatabaseServer(databaseDirectory);
        var name = database.ActiveDatabaseName;
        var relative = $"{ManifestFiles.DatabaseDirectory}/{SafeName(name)}.bak";
        var path = SafeFiles.Resolve(directory, relative);
        var sqlPath = RecoveryPaths.ToSqlPath(Options, id, relative);
        var identity = await database.IdentifyActiveAsync(ct);
        await database.BackupAsync(new DatabaseBackupRequest(name, sqlPath, $"ecp {id}", Options.CopyOnly, string.IsNullOrWhiteSpace(Options.EncryptionCertificate) ? null : Options.EncryptionCertificate), ct);
        if (!File.Exists(path)) throw new DomainException("backup_not_visible", "The database server reported success but the backup file is not visible to the application. The backup directory must be shared between the database server and the application (see docs/BACKUP_AND_RESTORE.md).");
        var header = await database.ReadHeaderAsync(sqlPath, ct);
        var problem = await database.VerifyAsync(sqlPath, ct);
        if (problem is not null) throw new DomainException("backup_verify_failed", "The database server's verification of the new backup failed: " + problem);
        var (hash, bytes) = await SafeFiles.HashAsync(path, ct);
        SafeFiles.Private(path);
        SafeFiles.Private(databaseDirectory);
        return new DatabaseSection(database.Engine, identity.Version, name, header.BackupType, header.CopyOnly, relative, bytes, hash, header.HasChecksums, VerifyOnlyPassed: true, header.Encrypted,
            header.Encrypted ? Options.EncryptionCertificate : null);
    }

    private static string SafeName(string name) => new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());

    private static void OpenForDatabaseServer(string path)
    {
        // The database server writes this directory as its own service account, which is usually a different user from the application.
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the operator's directory permissions decide */ }
    }

    private async Task<(DocumentSection Section, List<BackupFile> Files, List<string> Warnings)> BackupDocumentsAsync(string directory, CancellationToken ct)
    {
        var warnings = new List<string>();
        var store = stores.FirstOrDefault();
        var provider = documentStorage.ProviderKey;
        if (store is null)
        {
            warnings.Add($"The document store provider '{provider}' cannot list its objects, so document content was not backed up by the platform. Back it up with the provider's own tooling and record the evidence.");
            return (new DocumentSection(provider, false, null, null, null, 0, 0, 0, 0, 0, 0, 0, "Document content is not part of this backup."), [], warnings);
        }
        var copy = await DocumentStoreCopier.CopyAsync(store, documentStorage, Path.Combine(directory, ManifestFiles.DocumentsDirectory), ct);
        var indexBytes = DocumentStoreCopier.Serialise(copy.Lines);
        var indexPath = SafeFiles.Resolve(directory, ManifestFiles.DocumentIndex);
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
        await File.WriteAllBytesAsync(indexPath, indexBytes, ct);
        var indexHash = SafeFiles.Sha256Of(indexBytes);

        // Cross-check the database's record of every document version against what the store holds.
        var keys = copy.Lines.ToDictionary(l => l.Key, l => l, StringComparer.Ordinal);
        long metadata = 0, missing = 0;
        var damaged = new HashSet<string>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tenantId in await TenantIdsAsync(ct))
        {
            await using var scope = scopes.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            await foreach (var v in scope.ServiceProvider.GetRequiredService<IDocumentIntegrity>().VersionsAsync(ct))
            {
                if (!string.Equals(v.StorageProvider, provider, StringComparison.Ordinal)) continue;
                metadata++; referenced.Add(v.StorageKey);
                if (!keys.TryGetValue(v.StorageKey, out var line)) missing++;
                else if (!line.HashMatchesKey || !string.Equals(line.Sha256, v.Sha256, StringComparison.Ordinal) || line.Bytes != v.SizeBytes) damaged.Add(v.StorageKey);
            }
        }
        var orphan = keys.Keys.Count(k => !referenced.Contains(k));
        foreach (var l in copy.Lines.Where(l => !l.HashMatchesKey)) damaged.Add(l.Key);
        var mismatch = damaged.Count;
        if (missing > 0) warnings.Add($"{missing} document version(s) recorded in the database have no content in the document store. They are missing at the source, not lost by the backup.");
        if (mismatch > 0) warnings.Add($"{mismatch} stored document object(s) do not match the hash or size the database records. They were copied as found.");
        if (orphan > 0) warnings.Add($"{orphan} stored object(s) are not referenced by any document version (orphans). They were copied.");
        if (copy.Skipped > 0) warnings.Add($"{copy.Skipped} entries in the document store were skipped because they are links or unexpected names.");
        var files = new List<BackupFile> { new(ManifestFiles.DocumentIndex, "document-index", indexBytes.Length, indexHash) };
        var section = new DocumentSection(provider, true, ManifestFiles.DocumentsDirectory, ManifestFiles.DocumentIndex, indexHash, copy.Lines.Count, copy.ByteTotal, metadata, missing, orphan, mismatch, copy.Skipped,
            "Document content is copied object by object; every object's SHA-256 is in the index. Restore verifies each one.");
        return (section, files, warnings);
    }

    private async Task<(PackSection Section, List<BackupFile> Files, List<string> Warnings)> BackupPacksAsync(string directory, CancellationToken ct)
    {
        var warnings = new List<string>();
        var copied = await PackStoreCopier.CopyAsync(packOptions.Value.ResolvedStorageDirectory, directory, ct);
        var installed = new List<PackInstalledEntry>(); var keys = new List<TrustKeyEntry>(); var shipped = new Dictionary<(string, string), PackShippedEntry>();
        foreach (var tenantId in await TenantIdsAsync(ct))
        {
            await using var scope = scopes.CreateAsyncScope();
            using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().Use(tenantId);
            var inv = scope.ServiceProvider.GetRequiredService<IConfigurationInventory>();
            foreach (var p in await inv.InstalledPacksAsync(ct))
            {
                installed.Add(new PackInstalledEntry(tenantId, p.PackKey, p.Version, p.PackageSha256, p.SignatureStatus, p.Status));
                if (!copied.Any(c => c.TenantId == tenantId && c.Sha256 == p.PackageSha256)) warnings.Add($"The installed package {p.PackKey} {p.Version} has no file in the package store; it is unavailable at the source.");
            }
            foreach (var k in await inv.TrustKeysAsync(ct)) keys.Add(new TrustKeyEntry(tenantId, k.KeyId, k.Fingerprint, k.Status));
            foreach (var e in await scope.ServiceProvider.GetRequiredService<IPackLoader>().ListAsync(ct))
                if (e.IsShipped) shipped[(e.PackKey, e.Version)] = new PackShippedEntry(e.PackKey, e.Version, e.PackageSha256);
        }
        foreach (var c in copied.Where(c => !c.HashMatchesName)) warnings.Add($"A stored package file does not match the hash in its name ({c.RelativePath}). It was copied as found.");
        var files = copied.Select(c => new BackupFile(c.RelativePath, "package", c.Bytes, c.Sha256)).ToList();
        var section = new PackSection(copied.Count == 0 ? null : ManifestFiles.PacksDirectory, copied.Count, copied.Sum(c => c.Bytes),
            installed.OrderBy(i => i.TenantId).ThenBy(i => i.PackKey, StringComparer.Ordinal).ThenBy(i => i.Version, StringComparer.Ordinal).ToList(),
            shipped.Values.OrderBy(s => s.PackKey, StringComparer.Ordinal).ThenBy(s => s.Version, StringComparer.Ordinal).ToList(),
            keys.OrderBy(k => k.TenantId).ThenBy(k => k.KeyId, StringComparer.Ordinal).ToList(),
            "Installed package bytes are copied and re-verified on restore. Shipped packs come from the application image and are recorded by hash. Only public trust keys are recorded; a private signing key is never in a backup.");
        return (section, files, warnings);
    }

    private async Task<SchemaSection> SchemaAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        return await SchemaInspector.ReadAsync(scope.ServiceProvider, ct);
    }

    private async Task<List<Guid>> TenantIdsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        using var _ = scope.ServiceProvider.GetRequiredService<TenantContext>().UseInstallationScope();
        var list = (await scope.ServiceProvider.GetRequiredService<Ecp.Tenancy.ITenantDirectory>().ListAsync(ct)).Where(t => t.Id != SystemTenant.Id).Select(t => t.Id).OrderBy(i => i).ToList();
        list.Add(SystemTenant.Id);
        return list;
    }

    private static void WriteStatus(string directory, string id, string state, string stage, string? manifestSha256)
        => File.WriteAllText(Path.Combine(directory, ManifestFiles.Status), JsonSerializer.Serialize(new BackupStatusFile(id, state, stage, DateTime.UtcNow, manifestSha256, null), ManifestJson.Options) + "\n");

    private static void DiscardPartial(string directory, string id, string reason)
    {
        try
        {
            foreach (var sub in new[] { ManifestFiles.DatabaseDirectory, ManifestFiles.DocumentsDirectory, ManifestFiles.PacksDirectory })
            {
                var path = Path.Combine(directory, sub);
                if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null) Directory.Delete(path, recursive: true);
            }
            foreach (var file in new[] { ManifestFiles.Manifest, ManifestFiles.ManifestHash, ManifestFiles.Inventory })
            {
                var path = Path.Combine(directory, file);
                if (File.Exists(path)) File.Delete(path);
            }
            File.WriteAllText(Path.Combine(directory, ManifestFiles.Status), JsonSerializer.Serialize(new BackupStatusFile(id, "Failed", "Failed", DateTime.UtcNow, null, reason), ManifestJson.Options) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing more can be done; the folder has no manifest, so it is not a recovery point */ }
    }

    private static string PlatformVersionText() => typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}

public sealed record BackupStatusFile(string BackupId, string State, string Stage, DateTime UpdatedUtc, string? ManifestSha256, string? Failure);

public static class RecoveryLeases
{
    /// <summary>One lease for backup, restore and installation validation: they never run at the same time.</summary>
    public const string Exclusive = "recovery.exclusive";

    public static async Task<string> DescribeBusyAsync(IServiceScopeFactory scopes, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var held = (await scope.ServiceProvider.GetRequiredService<IOperationLeases>().ListAsync(ct)).FirstOrDefault(l => l.Name == Exclusive && l.IsActive);
        return held is null ? "Another backup, restore or validation is starting. Try again in a moment."
            : $"Another operation holds the recovery lease ({Redaction.Clean(held.OwnerDescription)}, since {held.AcquiredUtc:yyyy-MM-dd HH:mm} UTC, renewed {held.HeartbeatUtc:HH:mm:ss} UTC). Only one backup, restore or validation runs at a time.";
    }
}

public static class RecoveryPaths
{
    /// <summary>The path of a file inside a backup as the database server sees it.</summary>
    public static string ToSqlPath(RecoveryOptions options, string backupId, string relative)
    {
        var root = options.SqlDirectory.TrimEnd('/', '\\');
        var separator = root.Contains('\\') && !root.Contains('/') ? "\\" : "/";
        return root + separator + backupId + separator + relative.Replace("/", separator);
    }
}
