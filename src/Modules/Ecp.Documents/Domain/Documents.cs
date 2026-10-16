using System.Text;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Documents.Domain;

public enum DocumentStatus { Active, Quarantined, Archived, Discarded }
public enum ScanStatus { Pending, Clean, Infected, Skipped, Error }

public static partial class FileNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>
    /// Produces a display-only file name: no directories, no control or reserved characters, no device names, bounded length.
    /// File names are never used to build storage paths (storage keys are content hashes).
    /// </summary>
    public static string Sanitise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "file";
        var leaf = name.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder(leaf.Length);
        foreach (var ch in leaf.Normalize(NormalizationForm.FormC))
            sb.Append(char.IsControl(ch) || "<>:\"|?*".Contains(ch) ? '_' : ch);
        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        cleaned = Dots().Replace(cleaned, ".");
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (cleaned.Length == 0 || stem.Length == 0 && cleaned.StartsWith('.') && cleaned.Length == 1) return "file";
        if (Reserved.Contains(stem)) cleaned = "_" + cleaned;
        if (cleaned.Length > 200)
        {
            var ext = Path.GetExtension(cleaned);
            ext = ext.Length > 20 ? ext[..20] : ext;
            cleaned = cleaned[..(200 - ext.Length)] + ext;
        }
        return cleaned;
    }

    [GeneratedRegex(@"\.{2,}", RegexOptions.NonBacktracking)]
    private static partial Regex Dots();
}

/// <summary>Opaque storage key: <c>t/{tenant}/{hash[0..2]}/{hash}</c>. Content-addressed within a tenant only (ADR-0012).</summary>
public static partial class StorageKey
{
    public static string For(Guid tenantId, string sha256Hex)
    {
        if (!HashPattern().IsMatch(sha256Hex)) throw new DomainException("invalid_hash", "A lower-case SHA-256 hash is required.");
        return $"t/{tenantId:N}/{sha256Hex[..2]}/{sha256Hex}";
    }

    public static bool IsValidFor(Guid tenantId, string key)
        => KeyPattern().IsMatch(key) && key.StartsWith($"t/{tenantId:N}/", StringComparison.Ordinal);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.NonBacktracking)]
    private static partial Regex HashPattern();

    [GeneratedRegex("^t/[0-9a-f]{32}/[0-9a-f]{2}/[0-9a-f]{64}$", RegexOptions.NonBacktracking)]
    private static partial Regex KeyPattern();
}

public sealed class DocumentVersion : ITenantOwned
{
    private DocumentVersion() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int VersionNumber { get; private set; }
    public string? VersionLabel { get; private set; }
    public string FileName { get; private set; } = "";
    public string MimeType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
    public string StorageProvider { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public ScanStatus ScanStatus { get; private set; } = ScanStatus.Pending;
    public DateOnly? DocumentDate { get; private set; }
    public string UploadedBy { get; private set; } = "";
    public DateTime UploadedUtc { get; private set; }
    /// <summary>The migration batch that created this version, if any (INV-41).</summary>
    public Guid? MigrationBatchId { get; private set; }

    internal static DocumentVersion Create(Guid tenantId, Guid documentId, int number, string fileName, string mimeType, long size,
        string sha256, string provider, string storageKey, string? label, DateOnly? documentDate, string uploadedBy, DateTime nowUtc, Guid? migrationBatchId = null, ScanStatus scan = ScanStatus.Pending)
    {
        if (!Domain.StorageKey.IsValidFor(tenantId, storageKey))
            throw new DomainException("invalid_storage_key", "The storage key does not belong to this tenant.");
        if (size < 0) throw new DomainException("invalid_size", "File size must not be negative.");
        return new DocumentVersion
        {
            Id = Ids.New(), TenantId = tenantId, DocumentId = documentId, VersionNumber = number,
            VersionLabel = Guard.OptionalBounded(label, "Version label", 50), FileName = FileNames.Sanitise(fileName),
            MimeType = Guard.NotBlank(mimeType, "MIME type", 150), SizeBytes = size, Sha256 = sha256,
            StorageProvider = Guard.NotBlank(provider, "Storage provider", 50), StorageKey = storageKey, DocumentDate = documentDate,
            UploadedBy = Guard.NotBlank(uploadedBy, "Uploaded by", 200), UploadedUtc = Guard.Utc(nowUtc, nameof(nowUtc)), MigrationBatchId = migrationBatchId, ScanStatus = scan
        };
    }

    /// <summary>Downloads are blocked unless the scan verdict is Clean (or Skipped where the tenant explicitly allows it).</summary>
    public bool IsDownloadable(bool allowSkippedScan) => ScanStatus == ScanStatus.Clean || (allowSkippedScan && ScanStatus == ScanStatus.Skipped);

    public void RecordScan(ScanStatus status) => ScanStatus = status;
}

public sealed class Document : ITenantOwned
{
    private Document() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid? ContractId { get; private set; }
    public string DocumentTypeCode { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Classification { get; private set; } = "";
    public bool IsExecutedCopy { get; private set; }
    public DocumentStatus Status { get; private set; } = DocumentStatus.Active;
    public Guid? CurrentVersionId { get; private set; }
    public int VersionCount { get; private set; }
    public string? DocumentSourceId { get; private set; }
    public string? SourceSystem { get; private set; }
    public Guid? MigrationBatchId { get; private set; }
    /// <summary>The document this one was derived from in the source system (manifest ParentDocumentSourceId, resolved).</summary>
    public Guid? ParentDocumentId { get; private set; }
    public string? ArchiveReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Document Create(Guid tenantId, Guid organisationId, Guid? contractId, string documentTypeCode, string title, string classification,
        bool isExecutedCopy, string? documentSourceId = null, string? sourceSystem = null, Guid? migrationBatchId = null, Guid? parentDocumentId = null) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = Guard.NotEmpty(organisationId, nameof(organisationId)),
        ContractId = contractId, DocumentTypeCode = Guard.NotBlank(documentTypeCode, "Document type", 100), Title = Guard.NotBlank(title, "Title", 300),
        Classification = Guard.NotBlank(classification, "Classification", 30), IsExecutedCopy = isExecutedCopy,
        // A document with no contract waits in restricted staging until resolved (INV-35).
        Status = contractId is null ? DocumentStatus.Quarantined : DocumentStatus.Active,
        DocumentSourceId = Guard.OptionalBounded(documentSourceId, "Document source id", 400), SourceSystem = Guard.OptionalBounded(sourceSystem, "Source system", 100),
        MigrationBatchId = migrationBatchId, ParentDocumentId = parentDocumentId
    };

    public DocumentVersion AddVersion(string fileName, string mimeType, long size, string sha256, string provider, string storageKey,
        string? label, DateOnly? documentDate, string uploadedBy, DateTime nowUtc, Guid? migrationBatchId = null, ScanStatus scan = ScanStatus.Pending)
    {
        if (Status is DocumentStatus.Archived or DocumentStatus.Discarded)
            throw new DomainException("document_closed", "Versions cannot be added to an archived or discarded document.");
        var version = DocumentVersion.Create(TenantId, Id, VersionCount + 1, fileName, mimeType, size, sha256, provider, storageKey, label, documentDate, uploadedBy, nowUtc, migrationBatchId, scan);
        VersionCount++;
        CurrentVersionId = version.Id;
        return version;
    }

    /// <summary>Sets the classification. The caller has authorised the change and checked it against the contract's level.</summary>
    public void SetClassification(string level) => Classification = Guard.NotBlank(level, "Classification", 30);

    /// <summary>Points the document back at an earlier version after a later one was removed by a rollback.</summary>
    public void RewindTo(DocumentVersion previous, int versionCount)
    {
        CurrentVersionId = previous.Id; VersionCount = versionCount;
    }

    /// <summary>Compensating archival: the document and its versions are kept, but it is no longer active or downloadable.</summary>
    public void Archive(string reason)
    {
        if (Status == DocumentStatus.Archived) return;
        Status = DocumentStatus.Archived; ArchiveReason = Guard.NotBlank(reason, "Archive reason", 1000);
    }

    /// <summary>Releases a quarantined document to its contract once a person has resolved the match (audited by the caller).</summary>
    public void AttachToContract(Guid contractId)
    {
        ContractId = Guard.NotEmpty(contractId, nameof(contractId));
        if (Status == DocumentStatus.Quarantined) Status = DocumentStatus.Active;
    }
}
