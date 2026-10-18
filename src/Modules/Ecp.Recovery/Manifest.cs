using System.Text.Json;
using System.Text.Json.Serialization;
using Ecp.Configuration;
using Ecp.SharedKernel;

namespace Ecp.Recovery;

/// <summary>What a backup contains and how to check it (docs/BACKUP_AND_RESTORE.md, "The manifest"). Written once, last, and never changed. It holds identifiers, counts and hashes: no secret, no connection string, no business content.</summary>
public sealed record BackupManifest(
    int FormatVersion,
    string BackupId,
    string Scope,
    DateTime CreatedUtc,
    DateTime CompletedUtc,
    string PlatformVersion,
    string EnvironmentMode,
    OperatorEntry Operator,
    string Reason,
    string CorrelationId,
    DatabaseSection Database,
    SchemaSection Schema,
    DocumentSection Documents,
    PackSection Packs,
    InventorySnapshot InventoryBefore,
    InventorySnapshot InventoryAfter,
    bool Quiescent,
    ProtectionSection Protection,
    IReadOnlyList<BackupFile> Files,
    string Outcome,
    IReadOnlyList<string> Warnings);

public sealed record OperatorEntry(string Id, string Name);
public sealed record BackupFile(string Path, string Kind, long Bytes, string Sha256);

public sealed record DatabaseSection(string Engine, string EngineVersion, string Name, string BackupType, bool CopyOnly, string File, long Bytes, string Sha256, bool Checksummed, bool VerifyOnlyPassed, bool Encrypted, string? EncryptionCertificate);
public sealed record ModuleSchema(string Module, string Schema, IReadOnlyList<string> AppliedMigrations);
public sealed record SchemaSection(IReadOnlyList<ModuleSchema> Modules, int SecurityPolicyCount, IReadOnlyList<string> SecurityPolicies);

public sealed record DocumentSection(string Provider, bool ContentBackedUp, string? Reference, string? IndexFile, string? IndexSha256, long ObjectCount, long ByteTotal, long MetadataVersions, long MissingContent, long OrphanContent, long HashMismatch, long SkippedEntries, string Note);

public sealed record PackInstalledEntry(Guid TenantId, string PackKey, string Version, string PackageSha256, string SignatureStatus, string Status);
public sealed record PackShippedEntry(string PackKey, string Version, string PackageSha256);
public sealed record PackSection(string? Reference, long FileCount, long ByteTotal, IReadOnlyList<PackInstalledEntry> Installed, IReadOnlyList<PackShippedEntry> Shipped, IReadOnlyList<TrustKeyEntry> TrustKeys, string Note);
public sealed record TrustKeyEntry(Guid TenantId, string KeyId, string Fingerprint, string Status);

public sealed record ProtectionSection(bool DatabaseBackupEncrypted, string? Certificate, string StorageProtectionStatement, string Note);

public sealed record TableEntry(string Table, Guid? TenantId, long Rows, long? Checksum);
public sealed record OrganisationEntry(Guid Id, string Name, string RegionalFingerprint, bool RegionalComplete, string? ActivePack);
public sealed record ReleaseEntry(Guid ReleaseId, Guid OrganisationId, string Kind, int Number, string ContentHash);
public sealed record TenantEntry(
    Guid TenantId, string Name, long AuditEvents, long AuditHeadSequence, string AuditHeadHash, bool AuditChainValid, string? AuditChainProblem,
    IReadOnlyList<OrganisationEntry> Organisations, IReadOnlyList<ReleaseEntry> Releases, int OpenDrafts, long Documents, long DocumentVersions, long IdentifierProjectionGaps);

/// <summary>Counts and fingerprints taken from the installation at one moment. A backup takes one just before and one just after the database backup; a quiet installation gives two equal snapshots.</summary>
public sealed record InventorySnapshot(DateTime TakenUtc, IReadOnlyList<TenantEntry> Tenants, IReadOnlyList<TableEntry> Tables)
{
    public long Total(string table) => Tables.Where(t => t.Table.Equals(table, StringComparison.OrdinalIgnoreCase)).Sum(t => t.Rows);
    public long Tenants_ => Tables.Where(t => t.Table.Equals("tenancy.Tenant", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Rows);
}

public static class ManifestFiles
{
    public const string Manifest = "manifest.json";
    public const string ManifestHash = "manifest.sha256";
    public const string Status = "status.json";
    public const string Inventory = "inventory.json";
    public const string DocumentIndex = "documents/index.ndjson";
    public const string DocumentsDirectory = "documents";
    public const string PacksDirectory = "packs";
    public const string DatabaseDirectory = "database";
    public static readonly string[] Kinds = ["database", "inventory", "document-index", "document", "package"];
}

/// <summary>Serialisation of manifests. Property order follows the record declarations and collections are sorted by their builders, so the same backup always serialises to the same bytes.</summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static byte[] Serialise(BackupManifest manifest) => System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Options) + "\n");
    public static string Sha256(byte[] bytes) => SafeFiles.Sha256Of(bytes);

    public static BackupManifest Parse(byte[] bytes, string expectedBackupId, int maxBytes)
    {
        if (bytes.Length == 0 || bytes.Length > maxBytes) throw new DomainException("manifest_invalid", "The manifest is empty or larger than the limit.");
        BackupManifest? m;
        try { m = JsonSerializer.Deserialize<BackupManifest>(bytes, Options); }
        catch (JsonException) { throw new DomainException("manifest_invalid", "The manifest is not valid for this version of the platform."); }
        if (m is null) throw new DomainException("manifest_invalid", "The manifest is empty.");
        Validate(m, expectedBackupId);
        return m;
    }

    /// <summary>Structure checks that do not need the files: version, identity, safe unique paths, well-formed hashes, no duplicate or unknown file kinds.</summary>
    public static void Validate(BackupManifest m, string expectedBackupId)
    {
        [System.Diagnostics.CodeAnalysis.DoesNotReturn] static void Bad(string why) => throw new DomainException("manifest_invalid", "The manifest is not valid: " + why);
        if (m.FormatVersion != 1) Bad($"format version {m.FormatVersion} is not supported by this platform version.");
        if (!SafeFiles.IsBackupId(m.BackupId) || m.BackupId != expectedBackupId) Bad("the backup id does not match the folder it was found in.");
        if (m.Scope != "installation") Bad("only installation-level backups are supported.");
        if (m.Database is null || m.Schema is null || m.Documents is null || m.Packs is null || m.InventoryBefore is null || m.InventoryAfter is null || m.Files is null || m.Protection is null) Bad("a required section is missing.");
        if (m.Files.Count == 0 || m.Files.Count > 100_000) Bad("the file list is empty or too long.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in m.Files)
        {
            if (!SafeFiles.IsRelativeName(f.Path)) Bad("a file path is not a plain relative path.");
            if (!seen.Add(f.Path)) Bad("a file is listed twice.");
            if (!SafeFiles.IsSha256(f.Sha256) || f.Bytes < 0) Bad("a file entry has a malformed hash or size.");
            if (!ManifestFiles.Kinds.Contains(f.Kind)) Bad("a file entry has an unknown kind.");
        }
        if (!SafeFiles.IsRelativeName(m.Database.File) || !seen.Contains(m.Database.File)) Bad("the database backup file is not in the file list.");
        if (m.Documents.IndexFile is not null && !seen.Contains(m.Documents.IndexFile)) Bad("the document index is not in the file list.");
        if (m.Documents.IndexFile is not null && !SafeFiles.IsSha256(m.Documents.IndexSha256)) Bad("the document index hash is malformed.");
        if (m.Outcome is not ("Completed" or "CompletedWithWarnings")) Bad("the outcome is not one of a finished backup.");
    }
}
