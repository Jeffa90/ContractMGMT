using Ecp.Documents.Domain;
using Ecp.Documents.Persistence;
using Ecp.Documents.Storage;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecp.Documents;

public sealed record DocumentSummary(
    Guid DocumentId, Guid? ContractId, Guid OrganisationId, string Title, string DocumentTypeCode, string Classification, bool IsExecutedCopy, DocumentStatus Status, int VersionCount, string? FileName,
    string? MimeType, long SizeBytes, string? Sha256, ScanStatus ScanStatus, bool Downloadable, string? VersionLabel, DateOnly? DocumentDate, string? SourceSystem, string? DocumentSourceId, Guid? MigrationBatchId);

public sealed record DocumentContent(Stream Content, string FileName, string MimeType);

/// <summary>
/// Reads document metadata and content for modules that decide access themselves (Contracts checks the caller against the parent contract before using it). There is no
/// authorisation here, and it never lists documents of another tenant.
/// </summary>
public interface IDocumentQuery
{
    Task<IReadOnlyList<DocumentSummary>> ListForContractAsync(Guid contractId, CancellationToken ct = default);
    Task<DocumentSummary?> GetAsync(Guid documentId, CancellationToken ct = default);
    /// <summary>Opens the current version if it is downloadable (clean, or unscanned where the tenant allows it) and the document is active.</summary>
    Task<DocumentContent?> OpenAsync(Guid documentId, CancellationToken ct = default);
}

internal sealed class DocumentQuery(DocumentsDbContext db, IDocumentStorageProvider storage, ITenantContext tenant, IOptions<DocumentsOptions> options) : IDocumentQuery
{
    public async Task<IReadOnlyList<DocumentSummary>> ListForContractAsync(Guid contractId, CancellationToken ct = default)
    {
        var rows = await Query().Where(x => x.D.ContractId == contractId).OrderBy(x => x.D.Title).ThenBy(x => x.D.Id).ToListAsync(ct);
        return rows.Select(x => Map(x.D, x.V, options.Value.AllowSkippedScan)).ToList();
    }

    public async Task<DocumentSummary?> GetAsync(Guid documentId, CancellationToken ct = default)
    {
        var row = await Query().Where(x => x.D.Id == documentId).FirstOrDefaultAsync(ct);
        return row is null ? null : Map(row.D, row.V, options.Value.AllowSkippedScan);
    }

    public async Task<DocumentContent?> OpenAsync(Guid documentId, CancellationToken ct = default)
    {
        var row = await Query().Where(x => x.D.Id == documentId).FirstOrDefaultAsync(ct);
        if (row?.V is null || row.D.Status != DocumentStatus.Active || !row.V.IsDownloadable(options.Value.AllowSkippedScan)) return null;
        var stream = await storage.OpenReadAsync(tenant.RequireTenantId(), row.V.StorageKey, ct);
        return new DocumentContent(stream, row.V.FileName, row.V.MimeType);
    }

    private IQueryable<DocWithVersion> Query()
        => from d in db.Documents.AsNoTracking()
           join v in db.Versions.AsNoTracking() on d.CurrentVersionId equals v.Id into versions
           from v in versions.DefaultIfEmpty()
           select new DocWithVersion { D = d, V = v };

    private static DocumentSummary Map(Document d, DocumentVersion? v, bool allowSkipped)
        => new(d.Id, d.ContractId, d.OrganisationId, d.Title, d.DocumentTypeCode, d.Classification, d.IsExecutedCopy, d.Status, d.VersionCount, v?.FileName, v?.MimeType, v?.SizeBytes ?? 0, v?.Sha256,
            v?.ScanStatus ?? ScanStatus.Pending, v is not null && d.Status == DocumentStatus.Active && v.IsDownloadable(allowSkipped), v?.VersionLabel, v?.DocumentDate, d.SourceSystem, d.DocumentSourceId, d.MigrationBatchId);

    private sealed class DocWithVersion
    {
        public Document D { get; init; } = null!;
        public DocumentVersion? V { get; init; }
    }
}

/// <summary>
/// A mocked scanner for Demonstration and tests: clean unless the content is the standard antivirus test string. It stands in for a provider adapter and is never registered in
/// Production. It proves the hook (an infected file is held and never imported), not real protection.
/// </summary>
public sealed class MockMalwareScanner : IMalwareScanner
{
    private static readonly byte[] Signature = System.Text.Encoding.ASCII.GetBytes("EICAR-STANDARD-ANTIVIRUS-TEST-FILE");
    public string Name => "mock (demonstration only)";
    public bool IsDemonstrationOnly => true;

    public async Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default)
    {
        var window = new byte[Signature.Length * 2]; var carry = 0; var buffer = new byte[81920]; int n;
        while ((n = await content.ReadAsync(buffer, ct)) > 0)
        {
            var combined = new byte[carry + n];
            Array.Copy(window, window.Length - carry, combined, 0, carry);
            Array.Copy(buffer, 0, combined, carry, n);
            if (combined.AsSpan().IndexOf(Signature) >= 0) return ScanVerdict.Infected;
            carry = Math.Min(Signature.Length, combined.Length);
            Array.Copy(combined, combined.Length - carry, window, window.Length - carry, carry);
        }
        return ScanVerdict.Clean;
    }
}
