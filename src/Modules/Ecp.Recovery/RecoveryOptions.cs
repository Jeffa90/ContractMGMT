namespace Ecp.Recovery;

/// <summary>
/// Settings for backup, restore and validation (docs/BACKUP_AND_RESTORE.md). The host resolves secrets before binding: <see cref="AdminConnectionString"/> holds a secret and is never logged, serialised, shown or
/// placed in a manifest, a report or a support bundle.
/// </summary>
public sealed class RecoveryOptions
{
    /// <summary>Where backups are written, as this application sees the path. Must be absolute and outside any served path. Empty means backups are not configured.</summary>
    public string BackupDirectory { get; set; } = "";
    /// <summary>The same directory as the database server sees it (a shared mount, a different drive letter or path). Empty means the same path.</summary>
    public string SqlServerBackupDirectory { get; set; } = "";
    /// <summary>Where side-by-side restores place document and package files: one sub-directory per target database. Empty means restore is not configured.</summary>
    public string RestoreTargetDirectory { get; set; } = "";
    /// <summary>The privileged account used for BACKUP and RESTORE (never the day-to-day application account in Production). Empty falls back to the application connection, and readiness says so.</summary>
    public string AdminConnectionString { get; set; } = "";
    /// <summary>The server certificate that protects database backups (BACKUP ... WITH ENCRYPTION). Empty means the database backup is not encrypted by SQL Server.</summary>
    public string EncryptionCertificate { get; set; } = "";
    /// <summary>The operator's statement of how the backup storage is protected (for example "volume encrypted at rest; access limited to the backup operators group"). Recorded in each manifest; never verified by the platform.</summary>
    public string StorageProtection { get; set; } = "";
    /// <summary>Use COPY_ONLY so a platform backup never changes the operator's own differential chain. On by default.</summary>
    public bool CopyOnly { get; set; } = true;
    /// <summary>Compute a per-table row count and checksum for the manifest (a full read of every table). Turn off only for very large installations.</summary>
    public bool TableChecksums { get; set; } = true;
    public int ReauthenticationMinutes { get; set; } = 10;
    public int LeaseMinutes { get; set; } = 10;
    /// <summary>A verified backup older than this is reported as stale by readiness.</summary>
    public int StaleBackupDays { get; set; } = 7;
    /// <summary>Directories the web server serves (set by the host). A backup directory inside or around one is refused.</summary>
    public List<string> ServedRoots { get; set; } = [];
    public int MaxManifestBytes { get; set; } = 32 * 1024 * 1024;
    /// <summary>The seconds a database backup, verification or restore may run. Zero means no limit.</summary>
    public int DatabaseCommandTimeoutSeconds { get; set; }

    public string SqlDirectory => string.IsNullOrWhiteSpace(SqlServerBackupDirectory) ? BackupDirectory : SqlServerBackupDirectory;
    public bool BackupConfigured => !string.IsNullOrWhiteSpace(BackupDirectory);
    public bool RestoreConfigured => !string.IsNullOrWhiteSpace(RestoreTargetDirectory);
    public bool EncryptionRequested => !string.IsNullOrWhiteSpace(EncryptionCertificate);
}
