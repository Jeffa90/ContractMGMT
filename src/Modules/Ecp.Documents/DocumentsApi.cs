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

public sealed record AddVersionRequest(Guid DocumentId, string FileName, string MimeType, Stream Content, string? VersionLabel = null, DateOnly? DocumentDate = null);

/// <summary>Copies the current version of a document to another contract (a renewal's successor). The copy is a new document with its own history; it is never marked as an executed copy.</summary>
public sealed record CopyDocumentRequest(Guid SourceDocumentId, Guid TargetContractId, Guid OrganisationId, string Classification);

public sealed record DocumentVersionDto(Guid DocumentId, Guid VersionId, int VersionNumber, string FileName, long SizeBytes, string Sha256, ScanStatus ScanStatus, bool Downloadable);

public interface IDocumentService
{
    Task<DocumentVersionDto> AddAsync(AddDocumentRequest request, CancellationToken ct = default);

    /// <summary>Adds a version to an active document (for example a revised mark-up). Earlier versions are kept unchanged; the new version is scanned like any upload. No authorisation here: the owning module decides.</summary>
    Task<DocumentVersionDto> AddVersionAsync(AddVersionRequest request, CancellationToken ct = default);

    /// <summary>Copies the current version of an active, downloadable document to another contract as a new document. No authorisation here: the owning module decides what may be copied.</summary>
    Task<DocumentVersionDto> CopyAsync(CopyDocumentRequest request, CancellationToken ct = default);

    /// <summary>Opens content only if the version is downloadable. Record-level access checks are added by the access evaluator (M2).</summary>
    Task<Stream?> OpenDownloadAsync(Guid documentId, CancellationToken ct = default);
}

/// <summary>Raises the classification of a contract's documents so none is lower than its contract (a document may be the same or higher, never lower). Trusted: the owning module authorises first.</summary>
public interface IDocumentClassification
{
    /// <summary>Raises every active document of the contract whose classification is one of <paramref name="lowerLevels"/> to <paramref name="level"/>. Returns how many changed.</summary>
    Task<int> RaiseContractDocumentsAsync(Guid contractId, string level, IReadOnlyCollection<string> lowerLevels, string reason, CancellationToken ct = default);
    /// <summary>Sets one document's classification (raising or, with authority, lowering) and audits it. The caller has authorised it and checked the contract's level.</summary>
    Task ChangeDocumentClassificationAsync(Guid documentId, string level, string? reason, CancellationToken ct = default);
}

internal sealed class DocumentService(
    DocumentsDbContext db, IDocumentStorageProvider storage, IUnitOfWork uow, IAuditWriter audit, ITenantContext tenant, IActorContext actor,
    IClock clock, IEnumerable<IDocumentParentValidator> parents, Microsoft.Extensions.Options.IOptions<DocumentsOptions> options, IMalwareScanner scanner) : IDocumentService
{
    /// <summary>Scans the stored content; any failure of the scanner is recorded as an error verdict, never as clean.</summary>
    private async Task<ScanStatus> ScanAsync(string storageKey, CancellationToken ct)
    {
        try
        {
            await using var read = await storage.OpenReadAsync(tenant.RequireTenantId(), storageKey, ct);
            return await scanner.ScanAsync(read, ct) switch { ScanVerdict.Clean => ScanStatus.Clean, ScanVerdict.Infected => ScanStatus.Infected, ScanVerdict.Skipped => ScanStatus.Skipped, _ => ScanStatus.Error };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ScanStatus.Error; }
    }

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
        var scan = await ScanAsync(stored.StorageKey, ct);
        var version = document.AddVersion(r.FileName, r.MimeType, stored.SizeBytes, stored.Sha256, storage.ProviderKey, stored.StorageKey,
            r.VersionLabel, r.DocumentDate, actor.ActorId ?? "system", clock.UtcNow, scan: scan);
        await uow.ExecuteAsync(async c =>
        {
            db.Documents.Add(document);
            db.Versions.Add(version);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("document.added", nameof(Document), document.Id.ToString(), OrganisationId: document.OrganisationId, Classification: document.Classification,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { version.FileName, version.Sha256, version.SizeBytes, document.DocumentTypeCode })));
        }, ct);
        return new DocumentVersionDto(document.Id, version.Id, version.VersionNumber, version.FileName, version.SizeBytes, version.Sha256, version.ScanStatus, version.IsDownloadable(options.Value.AllowSkippedScan));
    }

    public async Task<DocumentVersionDto> AddVersionAsync(AddVersionRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var stored = await storage.PutAsync(tenantId, r.Content, options.Value.MaxFileBytes, ct);
        var scan = await ScanAsync(stored.StorageKey, ct);
        return await uow.ExecuteAsync(async c =>
        {
            var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == r.DocumentId, c) ?? throw new DomainException("document_not_found", "The document was not found.");
            var version = document.AddVersion(r.FileName, r.MimeType, stored.SizeBytes, stored.Sha256, storage.ProviderKey, stored.StorageKey, r.VersionLabel, r.DocumentDate, actor.ActorId ?? "system", clock.UtcNow, scan: scan);
            db.Versions.Add(version);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateConcurrencyException) { throw new DomainException("concurrency_conflict", "The document was changed by someone else at the same time. Reload it and try again."); }
            audit.Record(new AuditEntry("document.version_added", nameof(Document), document.Id.ToString(), OrganisationId: document.OrganisationId, Classification: document.Classification,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { version.VersionNumber, version.FileName, version.Sha256, version.SizeBytes })));
            return new DocumentVersionDto(document.Id, version.Id, version.VersionNumber, version.FileName, version.SizeBytes, version.Sha256, version.ScanStatus, version.IsDownloadable(options.Value.AllowSkippedScan));
        }, ct);
    }

    public async Task<DocumentVersionDto> CopyAsync(CopyDocumentRequest r, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var source = await (from d in db.Documents.AsNoTracking()
                            join v in db.Versions.AsNoTracking() on d.CurrentVersionId equals v.Id
                            where d.Id == r.SourceDocumentId && d.Status == DocumentStatus.Active
                            select new { Document = d, Version = v }).FirstOrDefaultAsync(ct) ?? throw new DomainException("document_not_found", "The document was not found.");
        if (!source.Version.IsDownloadable(options.Value.AllowSkippedScan)) throw new DomainException("document_not_copyable", "Only a document that has passed its scan can be copied.");
        await using var content = await storage.OpenReadAsync(tenantId, source.Version.StorageKey, ct);
        var copy = await AddAsync(new AddDocumentRequest(r.OrganisationId, r.TargetContractId, source.Document.DocumentTypeCode, source.Document.Title, source.Version.FileName, source.Version.MimeType, content,
            r.Classification, false, source.Version.VersionLabel, source.Version.DocumentDate), ct);
        await uow.ExecuteAsync(c =>
        {
            audit.Record(new AuditEntry("document.copied", nameof(Document), copy.DocumentId.ToString(), OrganisationId: r.OrganisationId, Classification: r.Classification,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { sourceDocumentId = r.SourceDocumentId, copy.Sha256 })));
            return Task.CompletedTask;
        }, ct);
        return copy;
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

internal sealed class DocumentClassificationService(DocumentsDbContext db, IUnitOfWork uow, IAuditWriter audit) : IDocumentClassification
{
    public Task<int> RaiseContractDocumentsAsync(Guid contractId, string level, IReadOnlyCollection<string> lowerLevels, string reason, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var lower = lowerLevels.ToArray();
            var docs = await db.Documents.Where(d => d.ContractId == contractId && d.Status != DocumentStatus.Discarded && lower.Contains(d.Classification)).ToListAsync(c);
            foreach (var d in docs)
            {
                var old = d.Classification;
                d.SetClassification(level);
                audit.Record(new AuditEntry("document.classification_changed", nameof(Document), d.Id.ToString(), OrganisationId: d.OrganisationId, Reason: reason, Classification: level,
                    OldValuesJson: System.Text.Json.JsonSerializer.Serialize(new { classification = old }), NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { classification = level })));
            }
            await db.SaveChangesAsync(c);
            return docs.Count;
        }, ct);

    public Task ChangeDocumentClassificationAsync(Guid documentId, string level, string? reason, CancellationToken ct = default)
        => uow.ExecuteAsync(async c =>
        {
            var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == documentId, c) ?? throw new DomainException("document_not_found", "The document was not found.");
            var old = d.Classification;
            if (string.Equals(old, level, StringComparison.Ordinal)) return;
            d.SetClassification(level);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("document.classification_changed", nameof(Document), d.Id.ToString(), OrganisationId: d.OrganisationId, Reason: reason, Classification: level,
                OldValuesJson: System.Text.Json.JsonSerializer.Serialize(new { classification = old }), NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { classification = level })));
        }, ct);
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
        services.AddScoped<IDocumentQuery, DocumentQuery>();
        services.AddScoped<IDocumentClassification, DocumentClassificationService>();
        services.TryAddSingleton<IMalwareScanner, DisabledMalwareScanner>();
        return services;
    }
}
