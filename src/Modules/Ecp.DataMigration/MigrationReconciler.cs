using System.Text.Json;
using Ecp.Contracts;
using Ecp.DataMigration.Domain;
using Ecp.Documents;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

/// <summary>
/// Reconciliation (CUTOVER_AND_RECONCILIATION.md, MIGRATION_SUPPLEMENT section 11): rows, manifest rows, files, and what actually reached the target, checked against each other.
/// A batch is not complete until the identities balance or the exceptions are approved and recorded.
/// </summary>
internal sealed class MigrationReconciler(MigrationKernel k, IContractMigrationPort contracts, IDocumentMigrationPort documents)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record ReconciliationPayload(ReconciliationDistributions Distributions, List<string> Imbalances, List<string> Outstanding);

    public async Task<ReconciliationDto> PreviewAsync(Guid batchId, CancellationToken ct)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        return await ComputeAsync(batch, 0, DateTime.MinValue, "", false, null, null, false, ct);
    }

    public async Task<ReconciliationDto> LatestAsync(Guid batchId, CancellationToken ct)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var report = await k.Db.Reconciliations.AsNoTracking().Where(r => r.BatchId == batchId).OrderByDescending(r => r.Sequence).FirstOrDefaultAsync(ct);
        if (report is null) return await ComputeAsync(batch, 0, DateTime.MinValue, "", false, null, null, false, ct);
        var totals = JsonSerializer.Deserialize<ReconciliationTotals>(report.TotalsJson, Json)!;
        var files = JsonSerializer.Deserialize<FileReconciliation>(report.FileTotalsJson, Json)!;
        var stored = JsonSerializer.Deserialize<ReconciliationPayload>(report.DistributionsJson, Json)!;
        return new ReconciliationDto(report.Id, report.Sequence, report.GeneratedUtc, report.GeneratedBy, totals, files, stored.Distributions, report.Balanced, report.ExceptionsApproved, stored.Imbalances, stored.Outstanding,
            report.ApprovedBy, report.ApprovalNote, true);
    }

    public async Task<ReconciliationDto> ReconcileAsync(Guid batchId, bool approveExceptions, string? note, CancellationToken ct)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Commit, track: true, ct);
        if (approveExceptions) await k.Access.RequireAsync(MigrationPermissions.Accept, MigrationKernel.Describe(project.TenantId, project.OrganisationId, project.Id.ToString()), ct);
        if (batch.Status != BatchStatus.Committed) throw new DomainException("invalid_batch_transition", $"A batch in state {batch.Status} cannot be reconciled; it must have committed.");
        if (approveExceptions && (string.IsNullOrWhiteSpace(note) || note.Trim().Length < 10))
            throw new DomainException("exception_note_required", "Approving exceptions needs a recorded reason (at least a sentence).");

        var by = await k.ActorLabelAsync(ct);
        var sequence = (await k.Db.Reconciliations.Where(r => r.BatchId == batchId).MaxAsync(r => (int?)r.Sequence, ct) ?? 0) + 1;
        var dto = await ComputeAsync(batch, sequence, k.Clock.UtcNow, by, false, note, null, true, ct);
        var approved = approveExceptions && !dto.Balanced;
        var payload = new ReconciliationPayload(dto.Distributions, dto.Imbalances.ToList(), dto.OutstandingItems.ToList());
        var report = ReconciliationReport.Create(k.TenantId, batchId, sequence, by, k.Clock.UtcNow, JsonSerializer.Serialize(dto.Totals, Json), JsonSerializer.Serialize(dto.Files, Json),
            JsonSerializer.Serialize(payload, Json), dto.Balanced, approved, approved ? JsonSerializer.Serialize(dto.Imbalances.Concat(dto.OutstandingItems), Json) : null, approved ? by : null, note);
        if (dto.Balanced || approved) batch.Reconcile(dto.Totals, approved, dto.Balanced);
        await k.Uow.ExecuteAsync(async c =>
        {
            k.Db.Reconciliations.Add(report);
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.reconciliation.saved", nameof(ReconciliationReport), report.Id.ToString(), batch.OrganisationId, batchId,
                new { sequence, dto.Balanced, ExceptionsApproved = approved, dto.Totals, Imbalances = dto.Imbalances }, approved ? note : null);
        }, ct);
        return dto with { ReportId = report.Id, ExceptionsApproved = approved, ApprovedBy = approved ? by : null, ApprovalNote = approved ? note : null, Saved = true };
    }

    private async Task<ReconciliationDto> ComputeAsync(MigrationBatch batch, int sequence, DateTime generatedUtc, string by, bool exceptions, string? note, string? approvedBy, bool saved, CancellationToken ct)
    {
        var batchId = batch.Id;
        var rowGroups = await k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId).GroupBy(r => r.Outcome).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => (long)x.N, ct);
        long Rows(RowOutcome o) => rowGroups.GetValueOrDefault(o);
        var created = Rows(RowOutcome.Created); var updated = Rows(RowOutcome.Updated);
        var totals = new ReconciliationTotals(rowGroups.Values.Sum(), created + updated + Rows(RowOutcome.Accepted), Rows(RowOutcome.Rejected), Rows(RowOutcome.Skipped), created, updated);

        var imbalances = new List<string>(); var outstanding = new List<string>();
        if (totals.SourceRows != totals.Accepted + totals.Rejected + totals.Skipped) imbalances.Add($"{Rows(RowOutcome.Pending)} source row(s) have no result yet (source {totals.SourceRows} ≠ accepted {totals.Accepted} + rejected {totals.Rejected} + skipped {totals.Skipped}).");
        if (Rows(RowOutcome.Accepted) > 0) imbalances.Add($"{Rows(RowOutcome.Accepted)} accepted row(s) were not committed (accepted {totals.Accepted} ≠ created {totals.Created} + updated {totals.Updated}).");

        var entries = await k.Db.ManifestEntries.AsNoTracking().Where(e => e.BatchId == batchId).GroupBy(e => e.State).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => (long)x.N, ct);
        var fileStates = await k.Db.StagedFiles.AsNoTracking().Where(f => f.BatchId == batchId).GroupBy(f => f.State).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => (long)x.N, ct);
        long Files(FileState s) => fileStates.GetValueOrDefault(s);
        var docSummary = await documents.SummariseBatchAsync(batchId, ct);
        var contractSummary = await contracts.SummariseBatchAsync(batchId, ct);
        var documentsCreated = await k.Db.Provenance.AsNoTracking().LongCountAsync(p => p.BatchId == batchId && p.TargetEntityType == "Document" && p.Action == ProvenanceAction.Created, ct);
        var fileTotals = new FileReconciliation(entries.Values.Sum(), entries.GetValueOrDefault(ManifestEntryState.Paired), entries.GetValueOrDefault(ManifestEntryState.OrphanManifest), entries.GetValueOrDefault(ManifestEntryState.Invalid),
            fileStates.Values.Sum(), Files(FileState.Imported), Files(FileState.Failed), Files(FileState.Invalid), Files(FileState.Duplicate), Files(FileState.Unmatched), Files(FileState.Discarded),
            Files(FileState.Staged) + Files(FileState.Matched), documentsCreated, docSummary.Versions - docSummary.Documents);
        if (!fileTotals.ManifestBalanced) imbalances.Add($"Manifest rows {fileTotals.ManifestRows} ≠ matched {fileTotals.ManifestMatched} + orphan {fileTotals.ManifestOrphans} + invalid {fileTotals.ManifestInvalid}: {entries.GetValueOrDefault(ManifestEntryState.Pending)} manifest row(s) were never paired.");
        if (!fileTotals.FilesBalanced) imbalances.Add($"{fileTotals.Other} file(s) are still waiting for a result (files {fileTotals.FilesInArchive} ≠ imported + failed + invalid + duplicate-handled + unmatched + discarded).");

        if (batch.Status is BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Accepted)
        {
            if (contractSummary.Contracts != totals.Created) imbalances.Add($"The target holds {contractSummary.Contracts} contract(s) created by this batch but {totals.Created} source row(s) are marked created.");
            if (docSummary.Documents != documentsCreated) imbalances.Add($"The target holds {docSummary.Documents} document(s) created by this batch but the provenance records {documentsCreated}.");
            var importedFiles = Files(FileState.Imported);
            if (docSummary.Versions != importedFiles) imbalances.Add($"The target holds {docSummary.Versions} document version(s) from this batch but {importedFiles} file(s) are marked imported.");
        }

        // Source versus target distributions for the records this batch created.
        var createdRows = await k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId && r.Outcome == RowOutcome.Created).Select(r => r.MappedValuesJson).ToListAsync(ct);
        var sourceStatus = new Dictionary<string, long>(); var sourceCategory = new Dictionary<string, long>(); var sourceValue = new Dictionary<string, decimal>();
        foreach (var json in createdRows)
        {
            if (json is null) continue;
            var m = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            if (m.TryGetValue("status", out var st)) sourceStatus[st] = sourceStatus.GetValueOrDefault(st) + 1;
            var cat = m.GetValueOrDefault("subcategory.name") ?? m.GetValueOrDefault("category.name");
            if (cat is not null) { var key = cat.ToUpperInvariant(); sourceCategory[key] = sourceCategory.GetValueOrDefault(key) + 1; }
            if (m.TryGetValue("currencyCode", out var cur) && m.TryGetValue("estimatedValue", out var v) && decimal.TryParse(v, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount))
                sourceValue[cur] = sourceValue.GetValueOrDefault(cur) + amount;
        }
        var distributions = new ReconciliationDistributions(sourceStatus, contractSummary.ByStatus.ToDictionary(x => x.Key, x => (long)x.Value), sourceCategory,
            contractSummary.ByCategoryCode.ToDictionary(x => x.Key.ToUpperInvariant(), x => (long)x.Value), sourceValue, contractSummary.ValueByCurrency);
        if (batch.Status is BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Accepted)
        {
            foreach (var (status, n) in sourceStatus) if (contractSummary.ByStatus.GetValueOrDefault(status) != n) imbalances.Add($"Status '{status}': {n} in the source, {contractSummary.ByStatus.GetValueOrDefault(status)} in the target.");
            foreach (var (cur, amount) in sourceValue) if (contractSummary.ValueByCurrency.GetValueOrDefault(cur) != amount) imbalances.Add($"Estimated value in {cur}: {amount:N2} in the source, {contractSummary.ValueByCurrency.GetValueOrDefault(cur):N2} in the target.");
        }

        if (Rows(RowOutcome.Rejected) > 0) outstanding.Add($"{Rows(RowOutcome.Rejected)} row(s) were rejected. They are counted in the totals and listed in the error report; none were loaded.");
        if (fileTotals.Unmatched > 0) outstanding.Add($"{fileTotals.Unmatched} file(s) are still in the unmatched queue.");
        if (fileTotals.Failed > 0) outstanding.Add($"{fileTotals.Failed} file(s) failed.");
        if (fileTotals.Invalid > 0) outstanding.Add($"{fileTotals.Invalid} file(s) were rejected as invalid.");
        if (fileTotals.ManifestOrphans > 0) outstanding.Add($"{fileTotals.ManifestOrphans} manifest row(s) name a file that is not in the archive.");

        var balanced = imbalances.Count == 0;
        return new ReconciliationDto(null, sequence, generatedUtc, by, totals, fileTotals, distributions, balanced, exceptions, imbalances, outstanding, approvedBy, note, saved);
    }
}
