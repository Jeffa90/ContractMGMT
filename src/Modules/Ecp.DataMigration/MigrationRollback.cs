using System.Text.Json;
using Ecp.Contracts;
using Ecp.DataMigration.Domain;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

public interface IMigrationRollback
{
    /// <summary>What a rollback would do right now, record by record category, without doing it.</summary>
    Task<RollbackPreview> PreviewAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>
    /// Rolls back what the batch did, selecting strictly by the batch's provenance records (so pre-existing records and other batches are never touched): reversible records are removed or restored;
    /// records that have moved on are archived and reported; shared suppliers and categories still used elsewhere are kept. Refused once the batch is accepted. Safe to run again.
    /// </summary>
    Task<RollbackResult> RollBackAsync(Guid batchId, string reason, CancellationToken ct = default);
    /// <summary>The only way back after acceptance: nothing is deleted; the batch's contracts and documents are archived with the reason and batch id.</summary>
    Task<RollbackResult> CompensateAsync(Guid batchId, string reason, CancellationToken ct = default);
    /// <summary>Deletes the restricted staging copies of files that were never imported, once the batch has ended. Returns the number of copies removed.</summary>
    Task<int> PurgeStagingAsync(Guid batchId, CancellationToken ct = default);
}

internal sealed class MigrationRollbackService(MigrationKernel k, IContractMigrationPort contracts, IDocumentMigrationPort documents, IDocumentStorageProvider storage) : IMigrationRollback
{
    private const int ChunkSize = 200;
    private static readonly string[] EntityOrder = ["Document", "Contract", "Supplier", "Category"];

    public async Task<RollbackPreview> PreviewAsync(Guid batchId, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Rollback, track: false, ct);
        try { batch.EnsureCanRollBack(k.Clock.UtcNow); }
        catch (DomainException ex) { return new RollbackPreview(false, ex.Message, []); }
        var tally = new Dictionary<(string, string), (int Count, string Detail)>();
        // Records are considered in the order a rollback acts, so a supplier is previewed as removable once the contracts that would be removed are taken into account.
        var wouldRemove = new HashSet<Guid>(); var wouldRemoveDocuments = new HashSet<Guid>(); var wouldRemoveCategories = new HashSet<Guid>();
        var active = await ActiveAsync(batchId, ct);
        foreach (var created in active.Where(x => x.TargetEntityType == "Category" && x.Action == ProvenanceAction.Created)) wouldRemoveCategories.Add(created.TargetEntityId); // indicative: a child is judged on its own
        foreach (var p in active.OrderBy(x => Array.IndexOf(EntityOrder, x.TargetEntityType)))
        {
            var (action, detail) = await DecideAsync(batch, p, apply: false, compensate: false, ct, preview: wouldRemove, previewDocuments: wouldRemoveDocuments, previewCategories: wouldRemoveCategories);
            if (p.TargetEntityType == "Contract" && action == "Remove") wouldRemove.Add(p.TargetEntityId);
            if (p.TargetEntityType == "Document" && action == "Remove") wouldRemoveDocuments.Add(p.TargetEntityId);
            var key = (p.TargetEntityType, action);
            tally[key] = (tally.GetValueOrDefault(key).Count + 1, detail);
        }
        return new RollbackPreview(true, null, tally.OrderBy(t => Array.IndexOf(EntityOrder, t.Key.Item1)).Select(t => new RollbackPreviewLine(t.Key.Item1, t.Key.Item2, t.Value.Count, t.Value.Detail)).ToList());
    }

    public Task<RollbackResult> RollBackAsync(Guid batchId, string reason, CancellationToken ct = default) => RunAsync(batchId, reason, compensate: false, ct);
    public Task<RollbackResult> CompensateAsync(Guid batchId, string reason, CancellationToken ct = default) => RunAsync(batchId, reason, compensate: true, ct);

    private async Task<RollbackResult> RunAsync(Guid batchId, string reason, bool compensate, CancellationToken ct)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Rollback, track: true, ct);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) throw new DomainException("reason_required", "Record why the batch is being rolled back.");
        reason = reason.Trim();
        if (compensate)
        {
            if (batch.Status == BatchStatus.Compensated) return await SummaryAsync(batchId, batch.Status, ct);
            if (batch.Status is not (BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Accepted)) throw new DomainException("invalid_batch_transition", $"A batch in state {batch.Status} cannot be compensated.");
        }
        else
        {
            if (batch.Status == BatchStatus.RolledBack) return await SummaryAsync(batchId, batch.Status, ct); // idempotent
            batch.EnsureCanRollBack(k.Clock.UtcNow); // refused after acceptance
        }
        var by = await k.ActorLabelAsync(ct);
        var releasedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entity in EntityOrder)
        {
            // A category that still has child categories from this batch waits until the children have been dealt with; if nothing more can progress, it is retained.
            var deferred = new HashSet<Guid>(); var progressed = false; var allowDefer = true;
            while (true)
            {
                var skip = deferred.ToList();
                var more = await k.Uow.ExecuteAsync(async c =>
                {
                    var chunk = await k.Db.Provenance.Where(p => p.BatchId == batchId && p.ReversalState == ReversalState.Active && p.TargetEntityType == entity && !skip.Contains(p.Id))
                        .OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id).Take(ChunkSize).ToListAsync(c);
                    if (chunk.Count == 0) return false;
                    foreach (var p in chunk)
                    {
                        var (action, detail) = await DecideAsync(batch, p, apply: true, compensate, c, releasedKeys, reason, allowDefer: allowDefer);
                        if (action == "Defer") { deferred.Add(p.Id); continue; }
                        var state = action switch { "Remove" or "Restore" or "Unlink" => ReversalState.Reversed, "Archive" => ReversalState.Archived, "Keep" => ReversalState.Retained, _ => ReversalState.NotReversible };
                        p.Resolve(state, detail, k.Clock.UtcNow);
                        progressed = true;
                    }
                    await k.Db.SaveChangesAsync(c);
                    return true;
                }, ct);
                contracts.ReleaseTracked(); documents.ReleaseTracked(); k.Db.ChangeTracker.Clear();
                if (more) continue;
                // The batch row is re-read for each chunk because the tracker was cleared.
                if (deferred.Count == 0) break;
                if (progressed) { deferred.Clear(); progressed = false; continue; }   // children were removed: ask the parents again
                allowDefer = false; deferred.Clear();                                  // nothing can progress: decide the rest as they stand
            }
        }

        await k.Uow.ExecuteAsync(async c =>
        {
            var current = await k.Db.Batches.FirstAsync(b => b.Id == batchId, c);
            if (compensate) current.CompleteCompensation(reason, by, k.Clock.UtcNow); else current.CompleteRollback(reason, by, k.Clock.UtcNow);
            await k.Db.SaveChangesAsync(c);
            var summary = await k.Db.Provenance.AsNoTracking().Where(p => p.BatchId == batchId).GroupBy(p => p.ReversalState).Select(g => new { g.Key, N = g.Count() }).ToListAsync(c);
            k.Record(compensate ? "migration.batch.compensated" : "migration.batch.rolled_back", nameof(MigrationBatch), batchId.ToString(), current.OrganisationId, batchId,
                summary.ToDictionary(x => x.Key.ToString(), x => x.N), reason);
        }, ct);
        k.Db.ChangeTracker.Clear();
        return await SummaryAsync(batchId, compensate ? BatchStatus.Compensated : BatchStatus.RolledBack, ct);
    }

    private async Task<RollbackResult> SummaryAsync(Guid batchId, BatchStatus status, CancellationToken ct)
    {
        var rows = await k.Db.Provenance.AsNoTracking().Where(p => p.BatchId == batchId).GroupBy(p => new { p.TargetEntityType, p.ReversalState }).Select(g => new { g.Key.TargetEntityType, g.Key.ReversalState, N = g.Count() }).ToListAsync(ct);
        int Count(ReversalState s) => rows.Where(r => r.ReversalState == s).Sum(r => r.N);
        return new RollbackResult(status, Count(ReversalState.Reversed), Count(ReversalState.Archived), Count(ReversalState.Retained), Count(ReversalState.NotReversible),
            rows.OrderBy(r => Array.IndexOf(EntityOrder, r.TargetEntityType)).Select(r => new RollbackPreviewLine(r.TargetEntityType, r.ReversalState.ToString(), r.N, "")).ToList());
    }

    private Task<List<ProvenanceRecord>> ActiveAsync(Guid batchId, CancellationToken ct)
        => k.Db.Provenance.AsNoTracking().Where(p => p.BatchId == batchId && p.ReversalState == ReversalState.Active).OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id).ToListAsync(ct);

    /// <summary>
    /// What to do with one provenance record, and (when <paramref name="apply"/>) doing it. Returns the action and a plain explanation that is stored with the record.
    /// Actions: Remove, Restore, Unlink, Archive, Keep, NotReversible.
    /// </summary>
    private async Task<(string Action, string Detail)> DecideAsync(MigrationBatch batch, ProvenanceRecord p, bool apply, bool compensate, CancellationToken ct, HashSet<string>? released = null, string? reason = null, HashSet<Guid>? preview = null, HashSet<Guid>? previewDocuments = null, bool allowDefer = false, HashSet<Guid>? previewCategories = null)
    {
        var why = reason ?? "Rollback";
        switch (p.TargetEntityType)
        {
            case "Document":
            {
                if (p.Action == ProvenanceAction.Linked) return ("Unlink", "Nothing to undo: the batch only linked existing content.");
                var state = await documents.GetRollbackStateAsync(p.TargetEntityId, batch.Id, ct);
                if (state is null) return ("Unlink", "The document is already gone.");
                if (p.Action == ProvenanceAction.Updated)
                {
                    if (compensate) return ("NotReversible", "A version this batch added to an older document is left in place after acceptance.");
                    if (apply)
                    {
                        try { foreach (var key in await documents.RemoveBatchVersionsAsync(p.TargetEntityId, batch.Id, why, ct)) released?.Add(key); return ("Remove", "The versions this batch added were removed."); }
                        catch (DomainException ex) { return ("NotReversible", ex.Message); }
                    }
                    return ("Remove", "The versions this batch added would be removed.");
                }
                var wholly = state.CreatedByBatch && state.VersionsOfBatch == state.VersionCount;
                if (!compensate && wholly)
                {
                    if (apply) foreach (var key in await documents.RemoveDocumentAsync(p.TargetEntityId, batch.Id, why, ct)) released?.Add(key);
                    return ("Remove", "The document was created by this batch and has not changed since.");
                }
                if (apply) await documents.ArchiveDocumentAsync(p.TargetEntityId, batch.Id, why, ct);
                return ("Archive", compensate ? "Archived by compensating action; nothing was deleted." : "Versions were added after the batch, so the document is archived instead of removed.");
            }
            case "Contract":
            {
                var c = await contracts.GetContractAsync(p.TargetEntityId, ct);
                if (c is null) return ("Unlink", "The contract is already gone.");
                if (p.Action == ProvenanceAction.Updated)
                {
                    if (compensate) return ("NotReversible", "Fields this batch updated on an existing contract are left in place after acceptance.");
                    if (c.UpdatedUtc != p.AfterStampUtc) return ("NotReversible", "The contract has changed since the batch updated it, so the earlier values were not restored.");
                    if (apply)
                    {
                        var before = JsonSerializer.Deserialize<Dictionary<string, string?>>(p.BeforeImageJson ?? "{}") ?? [];
                        await contracts.UpdateContractFieldsAsync(c.Id, batch.Id, before, lockedOverwriteAuthorised: true, $"Rollback: {why}", ct);
                    }
                    return ("Restore", "The fields the batch changed were restored to their earlier values.");
                }
                if (c.MigrationBatchId != batch.Id) return ("NotReversible", "The contract does not belong to this batch.");
                var untouched = c.UpdatedUtc == c.CreatedUtc;
                var remainingDocuments = await documents.CountDocumentsOfContractAsync(c.Id, previewDocuments?.ToList(), ct);
                if (!compensate && untouched && remainingDocuments == 0)
                {
                    if (apply) await contracts.RemoveContractAsync(c.Id, batch.Id, why, ct);
                    return ("Remove", "The contract was created by this batch and has not changed since.");
                }
                if (apply) await contracts.ArchiveContractAsync(c.Id, batch.Id, why, ct);
                return ("Archive", compensate ? "Archived by compensating action; nothing was deleted."
                    : !untouched ? "The contract has had activity since the batch loaded it, so it is archived instead of removed." : "Documents are still attached to the contract, so it is archived instead of removed.");
            }
            case "Supplier":
            {
                if (p.Action == ProvenanceAction.Linked) return ("Unlink", "Nothing to undo: the batch only linked an existing supplier.");
                if (compensate) return ("Keep", "Shared reference data is kept after acceptance.");
                var uses = await contracts.CountContractsOfSupplierAsync(p.TargetEntityId, preview?.ToList(), ct);
                if (uses > 0) return ("Keep", $"Kept: {uses} contract(s) still refer to this supplier (it was created by a rolled-back batch).");
                if (apply) await contracts.RemoveSupplierAsync(p.TargetEntityId, batch.Id, why, ct);
                return ("Remove", "The supplier was created by this batch and nothing refers to it.");
            }
            case "Category":
            {
                if (p.Action == ProvenanceAction.Linked) return ("Unlink", "Nothing to undo: the batch only linked an existing category.");
                if (compensate) return ("Keep", "Shared reference data is kept after acceptance.");
                var contractUses = await contracts.CountContractsOfCategoryAsync(p.TargetEntityId, preview?.ToList(), ct);
                var children = await contracts.CountChildCategoriesAsync(p.TargetEntityId, previewCategories?.ToList(), ct);
                if (contractUses == 0 && children > 0 && allowDefer) return ("Defer", "Waiting for the subcategories.");
                if (contractUses + children > 0) return ("Keep", $"Kept: {contractUses + children} contract(s) or subcategories still refer to this category (it was created by a rolled-back batch).");
                if (apply) await contracts.RemoveCategoryAsync(p.TargetEntityId, batch.Id, why, ct);
                return ("Remove", "The category was created by this batch and nothing refers to it.");
            }
            default:
                return ("NotReversible", $"Unknown record type {p.TargetEntityType}.");
        }
    }

    public async Task<int> PurgeStagingAsync(Guid batchId, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Rollback, track: false, ct);
        if (batch.Status is not (BatchStatus.Accepted or BatchStatus.RolledBack or BatchStatus.Compensated))
            throw new DomainException("batch_open", "Staged files are kept until the batch has been accepted, rolled back or compensated.");
        var removed = 0;
        var files = await k.Db.StagedFiles.Where(f => f.BatchId == batchId && f.QuarantineKey != null && f.State != FileState.Unmatched).ToListAsync(ct);
        // Unmatched files stay until a person resolves or discards them: they are evidence that something was not imported.
        await k.Uow.ExecuteAsync(async c =>
        {
            foreach (var f in files)
            {
                var key = f.QuarantineKey!;
                var usedByDocument = await documents.IsBlobReferencedAsync(key, c);
                var usedElsewhere = await k.Db.StagedFiles.AnyAsync(o => o.QuarantineKey == key && o.Id != f.Id && !files.Select(x => x.Id).Contains(o.Id), c)
                    || await k.Db.SourceFiles.AnyAsync(s => s.StorageKey == key, c);
                f.ClearQuarantine();
                await k.Db.SaveChangesAsync(c);
                if (!usedByDocument && !usedElsewhere) { await storage.DeleteAsync(k.TenantId, key, c); removed++; }
            }
            k.Record("migration.staging.purged", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId, new { Files = files.Count, BlobsRemoved = removed });
        }, ct);
        return removed;
    }
}
