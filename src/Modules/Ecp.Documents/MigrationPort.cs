using Ecp.Audit;
using Ecp.Documents.Domain;
using Ecp.Documents.Persistence;
using Ecp.Documents.Storage;
using Ecp.Persistence;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.Documents;

public enum ScanVerdict { Clean, Infected, Skipped, Error }

/// <summary>
/// Malware scanning provider hook (ATTACHMENT_INGESTION.md). No real provider is integrated: the default reports <see cref="ScanVerdict.Skipped"/>, and a version whose
/// scan was skipped is downloadable only where the tenant has explicitly allowed it. A provider adapter replaces this registration.
/// </summary>
public interface IMalwareScanner
{
    string Name { get; }
    Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default);
}

internal sealed class DisabledMalwareScanner : IMalwareScanner
{
    public string Name => "none";
    public Task<ScanVerdict> ScanAsync(Stream content, CancellationToken ct = default) => Task.FromResult(ScanVerdict.Skipped);
}

public sealed record ExistingDocumentRef(
    Guid Id, Guid? ContractId, string DocumentTypeCode, string Title, string? DocumentSourceId, Guid? MigrationBatchId, int VersionCount, Guid? CurrentVersionId,
    string? CurrentSha256, DocumentStatus Status);

public sealed record MigratedDocument(
    Guid OrganisationId, Guid BatchId, Guid ContractId, string DocumentTypeCode, string Title, string FileName, string MimeType, string StorageKey, string Sha256, long SizeBytes,
    string Classification, bool IsExecutedCopy, string? VersionLabel, DateOnly? DocumentDate, string SourceSystem, string DocumentSourceId, Guid? ParentDocumentId, ScanStatus Scan);

public sealed record ImportedDocument(Guid DocumentId, Guid VersionId, int VersionNumber);

public sealed record DocumentRollbackState(Guid DocumentId, Guid? ContractId, DocumentStatus Status, int VersionCount, int VersionsOfBatch, bool CreatedByBatch);

public sealed record BatchDocumentSummary(int Documents, int Versions, long Bytes, IReadOnlyDictionary<string, int> ByType);

/// <summary>Trusted operations a migration needs from the Documents module (see the Contracts equivalent: the Migration module authorises before calling).</summary>
public interface IDocumentMigrationPort
{
    Task<IReadOnlyList<ExistingDocumentRef>> FindBySourceAsync(Guid organisationId, string sourceSystem, IReadOnlyCollection<string> documentSourceIds, CancellationToken ct = default);
    /// <summary>Documents in this tenant whose current version has one of the given content hashes (content de-duplication is within one tenant only).</summary>
    Task<IReadOnlyList<ExistingDocumentRef>> FindByHashAsync(IReadOnlyCollection<string> sha256, CancellationToken ct = default);
    Task<ImportedDocument> ImportAsync(MigratedDocument document, CancellationToken ct = default);
    Task<ImportedDocument> AddVersionAsync(Guid documentId, MigratedDocument document, CancellationToken ct = default);
    Task<DocumentRollbackState?> GetRollbackStateAsync(Guid documentId, Guid batchId, CancellationToken ct = default);
    /// <summary>Removes a document created by the batch, with its versions. Returns the storage keys that were released.</summary>
    Task<IReadOnlyList<string>> RemoveDocumentAsync(Guid documentId, Guid batchId, string reason, CancellationToken ct = default);
    /// <summary>Removes the versions a batch added to a document it did not create, returning the document to its earlier current version.</summary>
    Task<IReadOnlyList<string>> RemoveBatchVersionsAsync(Guid documentId, Guid batchId, string reason, CancellationToken ct = default);
    Task ArchiveDocumentAsync(Guid documentId, Guid batchId, string reason, CancellationToken ct = default);
    Task<int> CountDocumentsOfContractAsync(Guid contractId, CancellationToken ct = default);
    Task<bool> IsBlobReferencedAsync(string storageKey, CancellationToken ct = default);
    Task<BatchDocumentSummary> SummariseBatchAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>Forgets entities the context is tracking (see the Contracts equivalent). Call only between committed chunks.</summary>
    void ReleaseTracked();
}

internal sealed class DocumentMigrationPort(
    DocumentsDbContext db, IUnitOfWork uow, IAuditWriter audit, IActorContext actor, IClock clock, ITenantContext tenant, IDocumentStorageProvider storage,
    IEnumerable<IDocumentParentValidator> parents) : IDocumentMigrationPort
{
    public async Task<IReadOnlyList<ExistingDocumentRef>> FindBySourceAsync(Guid organisationId, string sourceSystem, IReadOnlyCollection<string> documentSourceIds, CancellationToken ct = default)
    {
        var result = new List<ExistingDocumentRef>();
        foreach (var chunk in documentSourceIds.Distinct().Chunk(400))
        {
            var ids = chunk.ToList();
            var rows = await (from d in db.Documents.AsNoTracking()
                              where d.OrganisationId == organisationId && d.SourceSystem == sourceSystem && ids.Contains(d.DocumentSourceId!)
                              join v in db.Versions.AsNoTracking() on d.CurrentVersionId equals v.Id into vs
                              from v in vs.DefaultIfEmpty()
                              select new ExistingDocumentRef(d.Id, d.ContractId, d.DocumentTypeCode, d.Title, d.DocumentSourceId, d.MigrationBatchId, d.VersionCount, d.CurrentVersionId, v.Sha256, d.Status)).ToListAsync(ct);
            result.AddRange(rows);
        }
        return result;
    }

    public async Task<IReadOnlyList<ExistingDocumentRef>> FindByHashAsync(IReadOnlyCollection<string> sha256, CancellationToken ct = default)
    {
        var result = new List<ExistingDocumentRef>();
        foreach (var chunk in sha256.Distinct().Chunk(400))
        {
            var hashes = chunk.ToList();
            var rows = await (from d in db.Documents.AsNoTracking()
                              join v in db.Versions.AsNoTracking() on d.CurrentVersionId equals v.Id
                              where hashes.Contains(v.Sha256) && d.Status != DocumentStatus.Discarded
                              orderby d.Id
                              select new ExistingDocumentRef(d.Id, d.ContractId, d.DocumentTypeCode, d.Title, d.DocumentSourceId, d.MigrationBatchId, d.VersionCount, d.CurrentVersionId, v.Sha256, d.Status)).ToListAsync(ct);
            result.AddRange(rows);
        }
        return result;
    }

    public async Task<ImportedDocument> ImportAsync(MigratedDocument m, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await RequireContractAsync(m.ContractId, ct);
        await RequireBlobAsync(tenantId, m.StorageKey, ct);
        var document = Document.Create(tenantId, m.OrganisationId, m.ContractId, m.DocumentTypeCode, m.Title, m.Classification, m.IsExecutedCopy, m.DocumentSourceId, m.SourceSystem, m.BatchId, m.ParentDocumentId);
        var by = actor.ActorId ?? "system";
        var version = document.AddVersion(m.FileName, m.MimeType, m.SizeBytes, m.Sha256, storage.ProviderKey, m.StorageKey, m.VersionLabel, m.DocumentDate, by, clock.UtcNow, m.BatchId, m.Scan);
        await uow.ExecuteAsync(async c =>
        {
            db.Documents.Add(document);
            db.Versions.Add(version);
            try { await db.SaveChangesAsync(c); }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex)) { throw new DomainException("document_source_in_use", "A document with that source identifier already exists."); }
            audit.Record(new AuditEntry("migration.document.imported", nameof(Document), document.Id.ToString(), OrganisationId: m.OrganisationId, MigrationBatchId: m.BatchId,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { version.FileName, version.Sha256, version.SizeBytes, document.DocumentTypeCode, m.ContractId, m.DocumentSourceId, ScanStatus = version.ScanStatus.ToString() })));
        }, ct);
        return new ImportedDocument(document.Id, version.Id, version.VersionNumber);
    }

    public async Task<ImportedDocument> AddVersionAsync(Guid documentId, MigratedDocument m, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        await RequireBlobAsync(tenantId, m.StorageKey, ct);
        return await uow.ExecuteAsync(async c =>
        {
            var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == documentId, c) ?? throw new DomainException("document_not_found", "The document was not found.");
            var version = document.AddVersion(m.FileName, m.MimeType, m.SizeBytes, m.Sha256, storage.ProviderKey, m.StorageKey, m.VersionLabel, m.DocumentDate, actor.ActorId ?? "system", clock.UtcNow, m.BatchId, m.Scan);
            db.Versions.Add(version);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("migration.document.version_added", nameof(Document), document.Id.ToString(), OrganisationId: document.OrganisationId, MigrationBatchId: m.BatchId,
                NewValuesJson: System.Text.Json.JsonSerializer.Serialize(new { version.FileName, version.Sha256, version.VersionNumber })));
            return new ImportedDocument(document.Id, version.Id, version.VersionNumber);
        }, ct);
    }

    public async Task<DocumentRollbackState?> GetRollbackStateAsync(Guid documentId, Guid batchId, CancellationToken ct = default)
    {
        var d = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == documentId, ct);
        if (d is null) return null;
        var ofBatch = await db.Versions.AsNoTracking().CountAsync(v => v.DocumentId == documentId && v.MigrationBatchId == batchId, ct);
        return new DocumentRollbackState(d.Id, d.ContractId, d.Status, d.VersionCount, ofBatch, d.MigrationBatchId == batchId);
    }

    public async Task<IReadOnlyList<string>> RemoveDocumentAsync(Guid documentId, Guid batchId, string reason, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == documentId && x.MigrationBatchId == batchId, c);
            if (d is null) return (IReadOnlyList<string>)[];
            var versions = await db.Versions.Where(v => v.DocumentId == documentId).ToListAsync(c);
            if (versions.Any(v => v.MigrationBatchId != batchId)) throw new DomainException("document_changed", "A version was added after the batch, so the document cannot be removed.");
            db.Versions.RemoveRange(versions);
            await db.SaveChangesAsync(c); // versions first: the document row is the foreign-key parent
            db.Documents.Remove(d);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("migration.document.removed", nameof(Document), documentId.ToString(), OrganisationId: d.OrganisationId, Reason: reason, MigrationBatchId: batchId));
            return versions.Select(v => v.StorageKey).Distinct().ToList();
        }, ct);

    public async Task<IReadOnlyList<string>> RemoveBatchVersionsAsync(Guid documentId, Guid batchId, string reason, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == documentId, c);
            if (d is null) return (IReadOnlyList<string>)[];
            var all = await db.Versions.Where(v => v.DocumentId == documentId).OrderBy(v => v.VersionNumber).ToListAsync(c);
            var mine = all.Where(v => v.MigrationBatchId == batchId).ToList();
            if (mine.Count == 0) return [];
            var remaining = all.Where(v => v.MigrationBatchId != batchId).ToList();
            // Only a clean tail can be removed: if someone added a version after the batch's, it is not reversible.
            if (all.Skip(remaining.Count).Any(v => v.MigrationBatchId != batchId)) throw new DomainException("document_changed", "A version was added after the batch, so it cannot be removed.");
            db.Versions.RemoveRange(mine);
            d.RewindTo(remaining[^1], remaining.Count);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("migration.document.versions_removed", nameof(Document), documentId.ToString(), OrganisationId: d.OrganisationId, Reason: reason, MigrationBatchId: batchId));
            return mine.Select(v => v.StorageKey).Distinct().ToList();
        }, ct);

    public async Task ArchiveDocumentAsync(Guid documentId, Guid batchId, string reason, CancellationToken ct = default)
        => await uow.ExecuteAsync(async c =>
        {
            var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == documentId, c);
            if (d is null || d.Status == DocumentStatus.Archived) return;
            d.Archive(reason);
            await db.SaveChangesAsync(c);
            audit.Record(new AuditEntry("migration.document.archived", nameof(Document), documentId.ToString(), OrganisationId: d.OrganisationId, Reason: reason, MigrationBatchId: batchId));
        }, ct);

    public Task<int> CountDocumentsOfContractAsync(Guid contractId, CancellationToken ct = default) => db.Documents.AsNoTracking().CountAsync(d => d.ContractId == contractId, ct);

    public Task<bool> IsBlobReferencedAsync(string storageKey, CancellationToken ct = default) => db.Versions.AsNoTracking().AnyAsync(v => v.StorageKey == storageKey, ct);

    public async Task<BatchDocumentSummary> SummariseBatchAsync(Guid batchId, CancellationToken ct = default)
    {
        var docs = await db.Documents.AsNoTracking().Where(d => d.MigrationBatchId == batchId).Select(d => d.DocumentTypeCode).ToListAsync(ct);
        var versions = await db.Versions.AsNoTracking().Where(v => v.MigrationBatchId == batchId).Select(v => v.SizeBytes).ToListAsync(ct);
        return new BatchDocumentSummary(docs.Count, versions.Count, versions.Sum(), docs.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()));
    }

    public void ReleaseTracked() => db.ChangeTracker.Clear();

    private async Task RequireContractAsync(Guid contractId, CancellationToken ct)
    {
        foreach (var parent in parents) // sequential: all module contexts share one connection
            if (await parent.ParentExistsAsync(contractId, ct)) return;
        throw new DomainException("contract_not_found", "The contract was not found.");
    }

    private async Task RequireBlobAsync(Guid tenantId, string key, CancellationToken ct)
    {
        if (!await storage.ExistsAsync(tenantId, key, ct)) throw new DomainException("content_not_found", "The document content was not found in this tenant's store.");
    }
}
