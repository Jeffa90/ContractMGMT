using Ecp.SharedKernel;

namespace Ecp.Documents.Domain;

/// <summary>
/// One scan of one version of a document: when, by which scanner, what it found and why it was run. The history is what shows an administrator that a document was held, retried and released (or kept in
/// quarantine); it never holds the content or anything the scanner reported beyond a short, bounded note.
/// </summary>
public sealed class DocumentScanAttempt : ITenantOwned
{
    private DocumentScanAttempt() { }
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid VersionId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public DateTime AttemptedUtc { get; private set; }
    public ScanStatus Outcome { get; private set; }
    public string Provider { get; private set; } = "";
    /// <summary>Why it ran: upload, rescan (the scheduled retry) or manual.</summary>
    public string Trigger { get; private set; } = "";
    public string? Detail { get; private set; }

    public static DocumentScanAttempt Create(Guid tenantId, Guid organisationId, Guid documentId, Guid versionId, ScanStatus outcome, string provider, string trigger, string? detail, DateTime nowUtc) => new()
    {
        Id = Ids.New(), TenantId = Guard.NotEmpty(tenantId, nameof(tenantId)), OrganisationId = organisationId, DocumentId = documentId, VersionId = versionId, AttemptedUtc = Guard.Utc(nowUtc, nameof(nowUtc)),
        Outcome = outcome, Provider = Guard.NotBlank(provider, "Provider", 100), Trigger = Guard.NotBlank(trigger, "Trigger", 20), Detail = Guard.OptionalBounded(detail, "Detail", 300)
    };
}
