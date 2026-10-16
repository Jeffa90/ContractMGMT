using Ecp.Audit;
using Ecp.Documents.Domain;
using Ecp.Documents.Persistence;
using Ecp.Documents.Storage;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ecp.Documents;

/// <summary>Lets another module (Contracts) vouch that a document's parent exists in the current tenant, without a reference to it.</summary>
public interface IDocumentParentValidator
{
    Task<bool> ParentExistsAsync(Guid contractId, CancellationToken ct = default);
}

public sealed class DocumentsOptions
{
    public long MaxFileBytes { get; set; } = 100L * 1024 * 1024;
    public bool AllowSkippedScan { get; set; }
}

public sealed record AddDocumentRequest(
    Guid OrganisationId, Guid? ContractId, string DocumentTypeCode, string Title, string FileName, string MimeType, Stream Content,
    string Classification, bool IsExecutedCopy = false, string? VersionLabel = null, DateOnly? DocumentDate = null);

public sealed record DocumentVersionDto(Guid DocumentId, Guid VersionId, int VersionNumber, string FileName, long SizeBytes, string Sha256, ScanStatus ScanStatus, bool Downloadable);

public interface IDocumentService
{
    Task<DocumentVersionDto> AddAsync(AddDocumentRequest request, CancellationToken ct = default);

    /// <summary>Opens content only if the version is downloadable. Record-level access checks are added by the access evaluator (M2).</summary>
    Task<Stream?> OpenDownloadAsync(Guid documentId, CancellationToken ct = default);
}

internal sealed class DocumentService(
    DocumentsDbContext db, IDocumentStorageProvider storage, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IActorContext actor,
    IClock clock, IEnumerable<IDocumentParentValidator> parents, Microsoft.Extensions.Options.IOptions<DocumentsOptions> options) : IDocumentService
{
    public async Task<DocumentVersionDto> AddAsync(AddDocumentRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        if (r.ContractId is { } contractId)
        {
            var found = false;
            foreach (var parent in parents) // sequential: all module contexts share one connection
                if (await parent.ParentExistsAsync(contractId, ct)) { found = true; break; }
            if (!found) throw new DomainException("contract_not_found", "The contract was not found.");
        }

        var stored = await storage.PutAsync(tenantId, r.Content, options.Value.MaxFileBytes, ct);
        var document = Document.Create(tenantId, r.OrganisationId, r.ContractId, r.DocumentTypeCode, r.Title, r.Classification, r.IsExecutedCopy);
        var version = document.AddVersion(r.FileName, r.MimeType, stored.SizeBytes, stored.Sha256, storage.ProviderKey, stored.StorageKey,
            r.VersionLabel, r.DocumentDate, actor.ActorId ?? "system", clock.UtcNow);
        await uow.ExecuteAsync(async c =>
        {
            db.Documents.Add(document);
            db.Versions.Add(version);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("document.added", nameof(Document), document.Id.ToString(), OrganisationId: document.OrganisationId,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { version.FileName, version.Sha256, version.SizeBytes, document.DocumentTypeCode })));
        }, ct);
        return new DocumentVersionDto(document.Id, version.Id, version.VersionNumber, version.FileName, version.SizeBytes, version.Sha256, version.ScanStatus, Downloadable: false);
    }

    public async Task<Stream?> OpenDownloadAsync(Guid documentId, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var version = await (from d in db.Documents.AsNoTracking()
                             join v in db.Versions.AsNoTracking() on d.CurrentVersionId equals v.Id
                             where d.Id == documentId && d.Status == DocumentStatus.Active
                             select v).FirstOrDefaultAsync(ct);
        if (version is null || !version.IsDownloadable(options.Value.AllowSkippedScan)) return null;
        return await storage.OpenReadAsync(tenantId, version.StorageKey, ct);
    }
}

public static class DocumentsModule
{
    public const string Name = "Documents";
    public const string Schema = "documents";
    public const int Tier = 6;

    public static IServiceCollection AddDocumentsModule(this IServiceCollection services, Action<DocumentsOptions> configure, LocalFileSystemOptions storageOptions)
    {
        services.Configure(configure);
        services.AddModuleDbContext<DocumentsDbContext>(Name, Tier, Schema);
        services.AddSingleton(storageOptions);
        services.AddSingleton<IDocumentStorageProvider, LocalFileSystemStorageProvider>();
        services.AddScoped<IDocumentService, DocumentService>();
        services.AddScoped<IDocumentMigrationPort, DocumentMigrationPort>();
        services.TryAddSingleton<IMalwareScanner, DisabledMalwareScanner>();
        return services;
    }
}
