namespace Ecp.Host;

/// <summary>
/// Request-size and request-time policy. Every endpoint starts from a small default; only the endpoints that really accept files raise it,
/// and only as far as their purpose needs. The same numbers appear in the IIS web.config (src/Ecp.Host/web.config) because IIS applies its own
/// request-filtering limit before the application sees the request; a test keeps the two consistent.
/// </summary>
public static class UploadLimits
{
    private const long MiB = 1024 * 1024;

    /// <summary>Default maximum request body for every endpoint (forms, API calls). Far below the document limit on purpose.</summary>
    public const long DefaultRequestBytes = 10 * MiB;

    /// <summary>Contract, variation, legal-review and executed-copy documents: the 100 MiB document limit plus 1 MiB for the multipart envelope.</summary>
    public const long DocumentRequestBytes = 100 * MiB + 1 * MiB;

    /// <summary>Solution-pack upload: packs are capped at 8 MiB; 1 MiB for the multipart envelope.</summary>
    public const long PackRequestBytes = 9 * MiB;

    /// <summary>Mapping-profile CSV files (field and lookup mappings): the page refuses anything over 5 MiB; 1 MiB for the multipart envelope. Served under the site default.</summary>
    public const long MappingFileRequestBytes = 6 * MiB;

    /// <summary>Bulk-migration archive upload. This is the only endpoint opened this far; it is limited to its own URL.</summary>
    public const long MigrationArchiveRequestBytes = 2048 * MiB;

    public const string UploadTimeoutPolicy = "ecp-upload";
    public const string MigrationTimeoutPolicy = "ecp-migration";

    /// <summary>Default time allowed for a request. Configurable under Ecp:Http (seconds); see docs/DEPLOYMENT.md.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MigrationTimeout = TimeSpan.FromMinutes(60);
}
