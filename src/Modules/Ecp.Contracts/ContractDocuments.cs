using Ecp.Audit;
using Ecp.Contracts.Persistence;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.Identity.Access;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Contracts;

public sealed record ContractDocumentItem(
    Guid DocumentId, string Title, string DocumentType, string? FileName, int Versions, long SizeBytes, string? Sha256, string ScanStatus, bool CanDownload, bool IsExecutedCopy,
    DateOnly? DocumentDate, string? VersionLabel, string Status, string? SourceSystem, string? DocumentSourceId, Guid? MigrationBatchId);

public sealed record ContractDocumentDownload(Stream Content, string FileName, string MimeType);

/// <summary>Documents of a contract, for the signed-in user. Visibility is the parent record's; a download also needs documents.download within the document's classification and a scan verdict that allows it.</summary>
public interface ISecuredContractDocuments
{
    Task<IReadOnlyList<ContractDocumentItem>> ListAsync(Guid contractId, CancellationToken ct = default);
    /// <summary>Opens a document. Returns null when it exists but cannot be downloaded (not scanned, infected, archived). Every successful download is audited.</summary>
    Task<ContractDocumentDownload?> OpenAsync(Guid documentId, CancellationToken ct = default);
}

internal sealed class SecuredContractDocuments(ContractsDbContext db, IAccessService access, ContractAccessContext context, IDocumentQuery documents, IUnitOfWork uow, IAuditWriter audit) : ISecuredContractDocuments
{
    public async Task<IReadOnlyList<ContractDocumentItem>> ListAsync(Guid contractId, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(contract, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        var scale = await access.ClassificationScaleAsync(ct);
        var items = new List<ContractDocumentItem>();
        foreach (var d in await documents.ListForContractAsync(contractId, ct))
        {
            // The stricter of the record's and the document's classification is what a download is checked against.
            var strictest = scale.Rank(d.Classification) > scale.Rank(contract.Classification) ? d.Classification : contract.Classification;
            var mayDownload = d.Downloadable && (await access.CheckSilentAsync("documents.download", descriptor with { Classification = strictest }, ct)).Allowed;
            items.Add(new ContractDocumentItem(d.DocumentId, d.Title, d.DocumentTypeCode, d.FileName, d.VersionCount, d.SizeBytes, d.Sha256, d.ScanStatus.ToString(), mayDownload, d.IsExecutedCopy,
                d.DocumentDate, d.VersionLabel, d.Status.ToString(), d.SourceSystem, d.DocumentSourceId, d.MigrationBatchId));
        }
        return items;
    }

    public async Task<ContractDocumentDownload?> OpenAsync(Guid documentId, CancellationToken ct = default)
    {
        var summary = await documents.GetAsync(documentId, ct);
        // A document with no contract waits in restricted staging and is not visible to ordinary users; absent, other tenant's and out-of-scope all answer the same.
        if (summary?.ContractId is not { } contractId) throw new ResourceNotFoundException();
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(contract, ct);
        await access.RequireAsync("contracts.read", descriptor, ct); // 404 outside scope
        var scale = await access.ClassificationScaleAsync(ct);
        // The document's own classification applies as well as the record's: the stricter of the two is what the caller's ceiling is checked against.
        var strictest = scale.Rank(summary.Classification) > scale.Rank(contract.Classification) ? summary.Classification : contract.Classification;
        await access.RequireAsync("documents.download", descriptor with { Classification = strictest }, ct); // 403 when in scope but not permitted
        var content = await documents.OpenAsync(documentId, ct);
        if (content is null) return null;
        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry("document.downloaded", "Document", documentId.ToString(), OrganisationId: contract.OrganisationId,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { contract.AgreementReference, content.FileName, summary.Sha256 })));
            return Task.CompletedTask;
        }, ct);
        return new ContractDocumentDownload(content.Content, content.FileName, content.MimeType);
    }
}
