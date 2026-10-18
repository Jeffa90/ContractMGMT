using System.Text.Json;
using Ecp.Operations;
using Ecp.SharedKernel;
using Microsoft.Extensions.Options;

namespace Ecp.Recovery;

public enum VerificationOutcome { Passed, PassedWithWarnings, Failed, NotVerified }
public enum FindingSeverity { Info, Warning, Failed }

/// <summary>One thing the integrity check found. The code is stable; the message is plain language and never contains a secret.</summary>
public sealed record VerificationFinding(string Code, FindingSeverity Severity, string Message);

public sealed record BackupVerification(string BackupId, VerificationOutcome Outcome, DateTime VerifiedUtc, string? ManifestSha256, IReadOnlyList<VerificationFinding> Findings, long FilesChecked, long BytesChecked);

/// <summary>
/// Reads a backup back and proves it intact (docs/BACKUP_AND_RESTORE.md, "Integrity"): the manifest's own hash, every declared file hash and size, every document object in the index, the database server's own
/// verification of the database backup, and files that the manifest does not declare. The same check runs when a backup is created and before every restore.
/// </summary>
public interface IBackupVerifier
{
    /// <param name="persist">Write the result into the backup folder (<c>verification-*.json</c>). Restore verifies without writing, so a read-only backup store works.</param>
    Task<BackupVerification> VerifyAsync(string backupId, bool persist = false, CancellationToken ct = default);
    /// <summary>The verified manifest. Throws a domain error if the backup is not intact; used by restore.</summary>
    Task<BackupManifest> LoadManifestAsync(string backupId, CancellationToken ct = default);
}

internal sealed class BackupVerifier(IOptions<RecoveryOptions> recovery, IDatabaseBackupProvider database, IClock clock) : IBackupVerifier
{
    private RecoveryOptions Options => recovery.Value;

    public async Task<BackupManifest> LoadManifestAsync(string backupId, CancellationToken ct = default)
    {
        var (manifest, _, findings) = await ReadManifestAsync(backupId, ct);
        if (manifest is null) throw new DomainException("manifest_invalid", findings.FirstOrDefault(f => f.Severity == FindingSeverity.Failed)?.Message ?? "The manifest could not be read.");
        return manifest;
    }

    private async Task<(BackupManifest? Manifest, string? Hash, List<VerificationFinding> Findings)> ReadManifestAsync(string backupId, CancellationToken ct)
    {
        var findings = new List<VerificationFinding>();
        if (!SafeFiles.IsBackupId(backupId)) { findings.Add(new("BKV-001", FindingSeverity.Failed, "The backup id is not valid.")); return (null, null, findings); }
        string directory, manifestPath;
        try
        {
            directory = SafeFiles.ChildDirectory(Options.BackupDirectory, backupId);
            manifestPath = SafeFiles.Resolve(directory, ManifestFiles.Manifest);
        }
        catch (DomainException ex) { findings.Add(new("BKV-001", FindingSeverity.Failed, ex.Message)); return (null, null, findings); }
        if (!Directory.Exists(directory)) { findings.Add(new("BKV-002", FindingSeverity.Failed, "There is no backup with this id in the backup directory.")); return (null, null, findings); }
        if (!File.Exists(manifestPath)) { findings.Add(new("BKV-003", FindingSeverity.Failed, "The backup has no manifest. It did not finish and is not a recovery point.")); return (null, null, findings); }
        var info = new FileInfo(manifestPath);
        if (info.Length > Options.MaxManifestBytes) { findings.Add(new("BKV-004", FindingSeverity.Failed, "The manifest is larger than the limit.")); return (null, null, findings); }
        var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
        var hash = ManifestJson.Sha256(bytes);
        var sealPath = Path.Combine(directory, ManifestFiles.ManifestHash);
        if (!File.Exists(sealPath)) findings.Add(new("BKV-005", FindingSeverity.Failed, "The manifest's hash file is missing."));
        else
        {
            var recorded = (await File.ReadAllTextAsync(sealPath, ct)).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.Equals(recorded, hash, StringComparison.Ordinal)) findings.Add(new("BKV-006", FindingSeverity.Failed, "The manifest does not match its recorded hash. It has been changed since the backup was made."));
        }
        BackupManifest? manifest = null;
        try { manifest = ManifestJson.Parse(bytes, backupId, Options.MaxManifestBytes); }
        catch (DomainException ex) { findings.Add(new("BKV-007", FindingSeverity.Failed, ex.Message)); }
        return (findings.Any(f => f.Severity == FindingSeverity.Failed) ? null : manifest, hash, findings);
    }

    public async Task<BackupVerification> VerifyAsync(string backupId, bool persist = false, CancellationToken ct = default)
    {
        var (manifest, hash, findings) = await ReadManifestAsync(backupId, ct);
        long files = 0, bytes = 0;
        if (manifest is not null)
        {
            var directory = SafeFiles.ChildDirectory(Options.BackupDirectory, backupId);
            foreach (var f in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                string path;
                try { path = SafeFiles.Resolve(directory, f.Path); } catch (DomainException ex) { findings.Add(new("BKV-010", FindingSeverity.Failed, ex.Message)); continue; }
                if (!File.Exists(path)) { findings.Add(new("BKV-011", FindingSeverity.Failed, $"A file the manifest lists is missing: {f.Path}.")); continue; }
                var (sha, size) = await SafeFiles.HashAsync(path, ct);
                files++; bytes += size;
                if (size != f.Bytes) findings.Add(new("BKV-012", FindingSeverity.Failed, $"A file has a different size from the manifest: {f.Path}."));
                else if (!string.Equals(sha, f.Sha256, StringComparison.Ordinal)) findings.Add(new("BKV-013", FindingSeverity.Failed, $"A file has been changed since the backup was made (hash mismatch): {f.Path}."));
            }
            if (manifest.Documents.ContentBackedUp && manifest.Documents.IndexFile is not null) await VerifyDocumentsAsync(directory, manifest, findings, ct);
            VerifyUnlisted(directory, manifest, findings);
            await VerifyDatabaseAsync(directory, manifest, findings, ct);
            if (!manifest.Protection.DatabaseBackupEncrypted && string.IsNullOrWhiteSpace(manifest.Protection.StorageProtectionStatement))
                findings.Add(new("BKV-030", FindingSeverity.Warning, "This backup is not encrypted and records no storage protection statement."));
            foreach (var w in manifest.Warnings.Take(20)) findings.Add(new("BKV-031", FindingSeverity.Info, "Recorded when the backup was made: " + Redaction.Clean(w)));
        }
        var outcome = findings.Any(f => f.Severity == FindingSeverity.Failed) ? VerificationOutcome.Failed
            : findings.Any(f => f.Code == "BKV-040") ? VerificationOutcome.NotVerified
            : findings.Any(f => f.Severity == FindingSeverity.Warning) ? VerificationOutcome.PassedWithWarnings : VerificationOutcome.Passed;
        var result = new BackupVerification(backupId, outcome, clock.UtcNow, hash, findings, files, bytes);
        if (persist && manifest is not null)
        {
            try
            {
                var directory = SafeFiles.ChildDirectory(Options.BackupDirectory, backupId);
                var name = $"verification-{result.VerifiedUtc:yyyyMMddTHHmmssfff}Z.json";
                await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(result, ManifestJson.Options) + "\n", ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a read-only store: the result is still returned */ }
        }
        return result;
    }

    private static async Task VerifyDocumentsAsync(string directory, BackupManifest manifest, List<VerificationFinding> findings, CancellationToken ct)
    {
        var indexPath = SafeFiles.Resolve(directory, manifest.Documents.IndexFile!);
        if (!File.Exists(indexPath)) return;     // already reported by the file list check
        long count = 0, total = 0;
        try
        {
            foreach (var line in DocumentStoreCopier.ReadIndex(indexPath))
            {
                ct.ThrowIfCancellationRequested();
                count++; total += line.Bytes;
                string path;
                try { path = SafeFiles.Resolve(directory, ManifestFiles.DocumentsDirectory + "/" + line.Key); } catch (DomainException) { findings.Add(new("BKV-020", FindingSeverity.Failed, "A document entry resolves outside the backup.")); continue; }
                if (!File.Exists(path)) { findings.Add(new("BKV-021", FindingSeverity.Failed, $"A document object in the index is missing from the backup ({Path.GetFileName(path)[..8]}…).")); continue; }
                var (sha, size) = await SafeFiles.HashAsync(path, ct);
                if (size != line.Bytes || !string.Equals(sha, line.Sha256, StringComparison.Ordinal)) findings.Add(new("BKV-022", FindingSeverity.Failed, $"A document object has been changed since the backup was made ({Path.GetFileName(path)[..8]}…)."));
                if (!line.HashMatchesKey) findings.Add(new("BKV-023", FindingSeverity.Warning, $"A document object was already corrupt at the source ({Path.GetFileName(path)[..8]}…)."));
            }
        }
        catch (DomainException ex) { findings.Add(new("BKV-024", FindingSeverity.Failed, ex.Message)); return; }
        if (count != manifest.Documents.ObjectCount || total != manifest.Documents.ByteTotal) findings.Add(new("BKV-025", FindingSeverity.Failed, "The document index does not add up to the totals in the manifest."));
    }

    private static void VerifyUnlisted(string directory, BackupManifest manifest, List<VerificationFinding> findings)
    {
        var listed = new HashSet<string>(manifest.Files.Select(f => f.Path), StringComparer.Ordinal) { ManifestFiles.Manifest, ManifestFiles.ManifestHash, ManifestFiles.Status };
        var documentKeys = new HashSet<string>(StringComparer.Ordinal);
        if (manifest.Documents.IndexFile is not null && File.Exists(Path.Combine(directory, manifest.Documents.IndexFile)))
        {
            try { foreach (var l in DocumentStoreCopier.ReadIndex(Path.Combine(directory, manifest.Documents.IndexFile))) documentKeys.Add(ManifestFiles.DocumentsDirectory + "/" + l.Key); } catch (DomainException) { return; }
        }
        var root = Path.GetFullPath(directory);
        var unlisted = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("verification-", StringComparison.Ordinal) || listed.Contains(relative) || documentKeys.Contains(relative)) continue;
            unlisted++;
        }
        if (unlisted > 0) findings.Add(new("BKV-026", FindingSeverity.Warning, $"{unlisted} file(s) in the backup folder are not listed in the manifest. They are ignored by restore."));
    }

    private async Task VerifyDatabaseAsync(string directory, BackupManifest manifest, List<VerificationFinding> findings, CancellationToken ct)
    {
        if (!database.Supported) { findings.Add(new("BKV-040", FindingSeverity.Warning, "The database backup could not be checked by the database server because this installation does not use SQL Server. File hashes were checked.")); return; }
        var sqlPath = RecoveryPaths.ToSqlPath(Options, manifest.BackupId, manifest.Database.File);
        try
        {
            var header = await database.ReadHeaderAsync(sqlPath, ct);
            if (header.Damaged) findings.Add(new("BKV-041", FindingSeverity.Failed, "The database server reports the database backup as damaged."));
            if (!header.HasChecksums) findings.Add(new("BKV-042", FindingSeverity.Failed, "The database backup has no checksums, so it cannot be verified."));
            var problem = await database.VerifyAsync(sqlPath, ct);
            if (problem is not null) findings.Add(new("BKV-043", FindingSeverity.Failed, "The database server's verification of the database backup failed: " + problem));
        }
        catch (DomainException ex) { findings.Add(new("BKV-044", FindingSeverity.Failed, ex.Message)); }
    }
}

public enum BackupState { Incomplete, Failed, Unverified, Verified, VerifiedWithWarnings, VerificationFailed, Invalid }

public sealed record BackupSummary(string BackupId, BackupState State, DateTime? CreatedUtc, string? Outcome, long Bytes, int Warnings, string? ManifestSha256, DateTime? VerifiedUtc, string Detail);

/// <summary>Lists the recovery points in the backup directory, from the directory itself (not from the database, which a restore may replace). A folder without a manifest is Incomplete or Failed and is never offered as a recovery point.</summary>
public interface IBackupCatalogue
{
    Task<IReadOnlyList<BackupSummary>> ListAsync(CancellationToken ct = default);
    Task<BackupSummary?> FindAsync(string backupId, CancellationToken ct = default);
}

internal sealed class BackupCatalogue(IOptions<RecoveryOptions> recovery) : IBackupCatalogue
{
    private RecoveryOptions Options => recovery.Value;

    public async Task<IReadOnlyList<BackupSummary>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<BackupSummary>();
        if (!Options.BackupConfigured || !Directory.Exists(Options.BackupDirectory)) return list;
        foreach (var directory in new DirectoryInfo(Options.BackupDirectory).EnumerateDirectories().OrderByDescending(d => d.Name, StringComparer.Ordinal))
        {
            if (!SafeFiles.IsBackupId(directory.Name) || directory.LinkTarget is not null) continue;
            list.Add(await SummariseAsync(directory.Name, ct));
        }
        return list;
    }

    public async Task<BackupSummary?> FindAsync(string backupId, CancellationToken ct = default)
        => SafeFiles.IsBackupId(backupId) && Directory.Exists(Path.Combine(Options.BackupDirectory, backupId)) ? await SummariseAsync(backupId, ct) : null;

    private async Task<BackupSummary> SummariseAsync(string id, CancellationToken ct)
    {
        var directory = Path.Combine(Options.BackupDirectory, id);
        var manifestPath = Path.Combine(directory, ManifestFiles.Manifest);
        if (!File.Exists(manifestPath))
        {
            var status = ReadStatus(directory);
            var failed = status?.State == "Failed";
            return new BackupSummary(id, failed ? BackupState.Failed : BackupState.Incomplete, status?.UpdatedUtc, null, 0, 0, null, null,
                failed ? "The backup failed and is not a recovery point." : $"The backup did not finish (last stage: {status?.Stage ?? "unknown"}). It is not a recovery point.");
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
            var manifest = ManifestJson.Parse(bytes, id, Options.MaxManifestBytes);
            var hash = ManifestJson.Sha256(bytes);
            var sealOk = File.Exists(Path.Combine(directory, ManifestFiles.ManifestHash)) && (await File.ReadAllTextAsync(Path.Combine(directory, ManifestFiles.ManifestHash), ct)).StartsWith(hash, StringComparison.Ordinal);
            if (!sealOk) return new BackupSummary(id, BackupState.Invalid, manifest.CreatedUtc, manifest.Outcome, 0, manifest.Warnings.Count, hash, null, "The manifest does not match its recorded hash.");
            var verification = LatestVerification(directory);
            var total = manifest.Files.Sum(f => f.Bytes);
            var state = verification is null ? BackupState.Unverified : verification.Outcome switch
            {
                VerificationOutcome.Passed => BackupState.Verified,
                VerificationOutcome.PassedWithWarnings => BackupState.VerifiedWithWarnings,
                _ => BackupState.VerificationFailed,
            };
            return new BackupSummary(id, state, manifest.CreatedUtc, manifest.Outcome, total, manifest.Warnings.Count, hash, verification?.VerifiedUtc,
                state == BackupState.Unverified ? "Written but not yet verified. Verify it before relying on it." : state == BackupState.VerificationFailed ? "The last integrity check failed. Do not use this backup." : "Integrity checked.");
        }
        catch (DomainException ex) { return new BackupSummary(id, BackupState.Invalid, null, null, 0, 0, null, null, ex.Message); }
    }

    private static BackupStatusFile? ReadStatus(string directory)
    {
        try { return JsonSerializer.Deserialize<BackupStatusFile>(File.ReadAllText(Path.Combine(directory, ManifestFiles.Status)), ManifestJson.Options); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static BackupVerification? LatestVerification(string directory)
    {
        var latest = new DirectoryInfo(directory).EnumerateFiles("verification-*.json").OrderByDescending(f => f.Name, StringComparer.Ordinal).FirstOrDefault();
        if (latest is null || latest.Length > 4 * 1024 * 1024) return null;
        try { return JsonSerializer.Deserialize<BackupVerification>(File.ReadAllText(latest.FullName), ManifestJson.Options); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}
