using Ecp.Audit;
using Ecp.Contracts.Domain;
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

public sealed record AddContractDocumentRequest(Guid ContractId, string DocumentTypeCode, string Title, string FileName, string MimeType, Stream Content, string? Classification = null,
    string? VersionLabel = null, DateOnly? DocumentDate = null);

public sealed record AddContractDocumentVersionRequest(Guid DocumentId, string FileName, string MimeType, Stream Content, string? VersionLabel = null, DateOnly? DocumentDate = null);

/// <summary>Documents of a contract, for the signed-in user. Visibility is the parent record's; a download also needs documents.download within the document's classification and a scan verdict that allows it.</summary>
public interface ISecuredContractDocuments
{
    Task<IReadOnlyList<ContractDocumentItem>> ListAsync(Guid contractId, CancellationToken ct = default);
    /// <summary>Opens a document. Returns null when it exists but cannot be downloaded (not scanned, infected, archived). Every successful download is audited.</summary>
    Task<ContractDocumentDownload?> OpenAsync(Guid documentId, CancellationToken ct = default);
    /// <summary>Adds a document to a contract the caller may edit (or, where the state allows only Legal documents, to one Legal may mark up). Never marks an executed copy: that is the execution step's.</summary>
    Task<ContractDocumentItem> AddAsync(AddContractDocumentRequest request, CancellationToken ct = default);
    /// <summary>Adds a version to a document the caller may see and add to; earlier versions are kept.</summary>
    Task<ContractDocumentItem> AddVersionAsync(AddContractDocumentVersionRequest request, CancellationToken ct = default);
}

internal sealed class SecuredContractDocuments(ContractsDbContext db, IAccessService access, ContractAccessContext context, IDocumentQuery documents, IUnitOfWork uow, IAuditWriter audit,
    IDocumentService documentService) : ISecuredContractDocuments
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
            // A document classified above the caller's access is hidden altogether, even though its contract is visible.
            if (!(await access.CheckSilentAsync("contracts.read", descriptor with { Classification = strictest }, ct)).Allowed) continue;
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
            audit.Record(new AuditEntry("document.downloaded", "Document", documentId.ToString(), OrganisationId: contract.OrganisationId, Classification: strictest,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { contract.AgreementReference, content.FileName, summary.Sha256 })));
            return Task.CompletedTask;
        }, ct);
        return new ContractDocumentDownload(content.Content, content.FileName, content.MimeType);
    }

    private async Task<(Contract Contract, ResourceDescriptor Descriptor, ClassificationScale Scale)> LoadForUploadAsync(Guid contractId, CancellationToken ct)
    {
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct) ?? throw new ResourceNotFoundException();
        var descriptor = await context.DescribeAsync(contract, ct);
        await access.RequireAsync("contracts.read", descriptor, ct);
        var scale = await access.ClassificationScaleAsync(ct);
        // Who may add documents depends on the state: the owner while open, only Legal during Legal review, no one directly once the state is closed.
        switch (contract.DocumentPolicy)
        {
            case DocumentPolicies.Open: await access.RequireAsync("documents.upload", descriptor, ct); break;
            case DocumentPolicies.LegalOnly: await access.RequireAsync("legal.markup", descriptor, ct); break;
            default: throw new DomainException("documents_closed", "Documents cannot be added to this contract in its current state. A change to an Effective contract goes through a Variation.");
        }
        return (contract, descriptor, scale);
    }

    public async Task<ContractDocumentItem> AddAsync(AddContractDocumentRequest r, CancellationToken ct = default)
    {
        var (contract, descriptor, scale) = await LoadForUploadAsync(r.ContractId, ct);
        var requested = scale.Canonical(r.Classification) ?? (string.IsNullOrWhiteSpace(r.Classification) ? null : throw new DomainException("invalid_classification", $"Classification '{r.Classification}' is not recognised."));
        // A document is at least as classified as its contract (it may be higher), and the caller may not classify beyond their own access.
        var level = requested is not null && scale.Rank(requested) >= scale.Rank(contract.Classification) ? requested : contract.Classification;
        if (!(await access.CheckSilentAsync("contracts.read", descriptor with { Classification = level }, ct)).Allowed) throw new ForbiddenException("You cannot classify a document above your own access.");
        var dto = await documentService.AddAsync(new AddDocumentRequest(contract.OrganisationId, contract.Id, r.DocumentTypeCode, r.Title, r.FileName, r.MimeType, r.Content, level, false, r.VersionLabel, r.DocumentDate), ct);
        return (await ListAsync(contract.Id, ct)).First(d => d.DocumentId == dto.DocumentId);
    }

    public async Task<ContractDocumentItem> AddVersionAsync(AddContractDocumentVersionRequest r, CancellationToken ct = default)
    {
        var summary = await documents.GetAsync(r.DocumentId, ct);
        if (summary?.ContractId is not { } contractId) throw new ResourceNotFoundException();
        var (contract, descriptor, scale) = await LoadForUploadAsync(contractId, ct);
        var strictest = scale.Rank(summary.Classification) > scale.Rank(contract.Classification) ? summary.Classification : contract.Classification;
        await access.RequireAsync("contracts.read", descriptor with { Classification = strictest }, ct); // a document above the caller's access does not exist for them
        await documentService.AddVersionAsync(new AddVersionRequest(r.DocumentId, r.FileName, r.MimeType, r.Content, r.VersionLabel, r.DocumentDate), ct);
        return (await ListAsync(contract.Id, ct)).First(d => d.DocumentId == r.DocumentId);
    }
}
