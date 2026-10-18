using System.Text.Json;
using Ecp.Contracts;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Mapping;
using Ecp.DataMigration.Planning;
using Ecp.Documents;
using Ecp.Documents.Domain;
using Ecp.Documents.Storage;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

public enum FaultPoint { ChunkStart, Item, ChunkEnd, ChunkCommitted }

/// <summary>A seam for tests to stop a commit at an exact point (mid-chunk, at a phase boundary) and prove that resume neither loses nor duplicates anything. Production registers a no-op.</summary>
internal interface IMigrationFaults
{
    Task AtAsync(FaultPoint point, string phase, long sequence, CancellationToken ct);
}

internal sealed class NoMigrationFaults : IMigrationFaults
{
    public Task AtAsync(FaultPoint point, string phase, long sequence, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Stands for the process dying: the commit does not record a failure, it simply stops, leaving the batch Committing under its lease.</summary>
internal sealed class SimulatedProcessKill(string message) : Exception(message);

public interface IMigrationCommit
{
    /// <summary>A second person confirms a legacy load of exactly what was validated (needs migration.legacy_load). The person who commits cannot be the person who confirms.</summary>
    Task ConfirmLegacyLoadAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>
    /// Writes the validated batch to the target in chunks, each committed with its checkpoint. Re-running a completed commit does nothing; a failed or interrupted commit resumes from its
    /// checkpoints and never duplicates a record or file.
    /// </summary>
    Task<CommitProgress> CommitAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>Totals and checks computed now, without saving anything.</summary>
    Task<ReconciliationDto> PreviewReconciliationAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>Saves a reconciliation. The batch becomes Reconciled when everything balances, or when exceptions are approved (needs migration.accept) with a recorded note.</summary>
    Task<ReconciliationDto> ReconcileAsync(Guid batchId, bool approveExceptions, string? note, CancellationToken ct = default);
    Task AcceptAsync(Guid batchId, SampleCheckInput input, CancellationToken ct = default);
}

internal sealed class MigrationCommitService(MigrationKernel k, BatchRuntime runtime, IContractMigrationPort contracts, IDocumentMigrationPort documents, IDocumentStorageProvider storage,
    IMigrationFaults faults, MigrationReconciler reconciler, IMaintenanceGate? maintenanceGate = null) : IMigrationCommit
{
    private const string ContractsPhase = "Contracts";
    private const string DocumentsPhase = "Documents";

    public async Task ConfirmLegacyLoadAsync(Guid batchId, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.LegacyLoad, track: true, ct);
        var user = k.UserId ?? throw new ForbiddenException("A signed-in person is needed to confirm a legacy load.");
        batch.ConfirmLegacyLoad(user, await k.ActorLabelAsync(ct), k.Clock.UtcNow);
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.batch.legacy_confirmed", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId, new { batch.ConfirmedBy, Fingerprint = batch.ConfirmedFingerprint });
        }, ct);
    }

    public async Task<CommitProgress> CommitAsync(Guid batchId, CancellationToken ct = default)
    {
        if (maintenanceGate is not null) await maintenanceGate.EnsureWritableAsync("Committing a migration batch", ct);
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Commit, track: true, ct);
        if (batch.IsLegacyLoad) await k.Access.RequireAsync(MigrationPermissions.LegacyLoad, MigrationKernel.Describe(project.TenantId, project.OrganisationId, project.Id.ToString()), ct);
        if (batch.Status is BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Accepted) return Progress(batch, null, 0, 0); // idempotent
        if (batch.Kind == BatchKind.DryRun) throw new DomainException("dry_run_cannot_commit", "A dry run performs no target writes and cannot be committed.");
        if (project.Status != ProjectStatus.Active) throw new DomainException("project_closed", "The [[migration.project|lower]] is closed.");
        if (batch.IsLegacyLoad && !project.LegacyLoadAllowedAt(k.Clock.UtcNow)) throw new DomainException("legacy_policy_inactive", "MIG-E012: The legacy-load policy has expired or been closed.");
        if (await k.Db.StagingRows.AnyAsync(r => r.BatchId == batchId && r.ReviewDecision == ReviewDecision.Required, ct))
            throw new DomainException("review_required", "Some rows are waiting for a decision. Decide each one (skip, update, create with a new reference, or reject) and validate again.");

        var owner = Guid.NewGuid().ToString("N");
        var resumed = batch.Status is not BatchStatus.Validated;
        var label = await k.ActorLabelAsync(ct);
        await k.Uow.ExecuteAsync(async c =>
        {
            batch.StartCommit(k.Clock.UtcNow, owner, k.Options.CommitLease, label, k.UserId);
            await k.Db.SaveChangesAsync(c);
            k.Record(resumed ? "migration.commit.resumed" : "migration.commit.started", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId, new { Lease = k.Options.CommitLease.TotalSeconds });
        }, ct);
        k.Db.ChangeTracker.Clear();

        try
        {
            await RunAsync(batchId, owner, ct);
        }
        catch (SimulatedProcessKill) { throw; }
        catch (Exception ex)
        {
            contracts.ReleaseTracked(); documents.ReleaseTracked(); k.Db.ChangeTracker.Clear();
            var reason = ex is DomainException d ? $"{d.Code}: {d.Message}" : ex.GetType().Name + ": " + ex.Message;
            await k.Uow.ExecuteAsync(async c =>
            {
                var current = await k.Db.Batches.FirstAsync(b => b.Id == batchId, c);
                if (current.Status == BatchStatus.Committing) current.Fail(reason.Length > 900 ? reason[..900] : reason);
                await k.Db.SaveChangesAsync(c);
                k.Record("migration.commit.failed", nameof(MigrationBatch), batchId.ToString(), current.OrganisationId, batchId, new { reason });
            }, CancellationToken.None);
            var failed = await k.Db.Batches.AsNoTracking().FirstAsync(b => b.Id == batchId, CancellationToken.None);
            return Progress(failed, null, 0, 0);
        }
        var done = await k.Db.Batches.AsNoTracking().FirstAsync(b => b.Id == batchId, ct);
        return Progress(done, null, 0, 0);
    }

    private static CommitProgress Progress(MigrationBatch b, string? phase, long done, long total) => new(b.Status, phase, done, total, b.FailureReason);

    private async Task RunAsync(Guid batchId, string owner, CancellationToken ct)
    {
        var batch = await k.Db.Batches.AsNoTracking().FirstAsync(b => b.Id == batchId, ct);
        var project = await k.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == batch.ProjectId, ct);
        var loaded = await runtime.LoadAsync(batch, project, ct);
        if (loaded.ContractsFile is null || loaded.Version is null) throw new DomainException("source_required", "The batch has no [[contract|plural|lower]] file or mapping profile.");
        var ctx = await runtime.BuildContextAsync(loaded, ct);
        var rawStatusColumn = loaded.Definition.Fields.FirstOrDefault(f => f.TargetField == "status")?.SourceColumn;
        var rawRegisteredColumn = loaded.Definition.Fields.FirstOrDefault(f => f.TargetField == "registeredDate")?.SourceColumn;
        var actorLabel = await k.ActorLabelAsync(ct);

        await ContractsAsync(batchId, owner, loaded, ctx, (rawStatusColumn, rawRegisteredColumn), actorLabel, ct);
        await DocumentsAsync(batchId, owner, loaded, ctx, actorLabel, ct);

        await k.Uow.ExecuteAsync(async c =>
        {
            var current = await k.Db.Batches.FirstAsync(b => b.Id == batchId, c);
            current.CompleteCommit(k.Clock.UtcNow);
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.commit.completed", nameof(MigrationBatch), batchId.ToString(), current.OrganisationId, batchId, new { current.CommitStartedUtc, current.CommitCompletedUtc });
        }, ct);
        k.Db.ChangeTracker.Clear();
    }

    // ---- phase 1: reference data and contracts -----------------------------------------------------------------------------------------------------

    private async Task ContractsAsync(Guid batchId, string owner, BatchRuntime.Loaded l, PlanContext ctx, (string? Status, string? Registered) rawColumns, string actor, CancellationToken ct)
    {
        while (true)
        {
            var more = await k.Uow.ExecuteAsync(async c =>
            {
                var batch = await k.Db.Batches.FirstAsync(b => b.Id == batchId, c);
                var cp = await CheckpointAsync(batchId, ContractsPhase, c);
                var rows = await k.Db.StagingRows.Where(r => r.BatchId == batchId && r.Outcome == RowOutcome.Accepted && r.Sequence > cp.LastCompletedSequence)
                    .OrderBy(r => r.Sequence).Take(k.Options.ChunkSize).ToListAsync(c);
                if (rows.Count == 0) { cp.Advance(cp.LastCompletedSequence, true, k.Clock.UtcNow); await k.Db.SaveChangesAsync(c); return false; }

                await faults.AtAsync(FaultPoint.ChunkStart, ContractsPhase, rows[0].Sequence, c);
                var parsed = rows.Select(r => (Row: r, Mapped: Reconstruct(r))).ToList();
                await runtime.LoadExistingAsync(ctx, parsed.Select(p => p.Row.SourceRecordId).Where(s => s is not null).Cast<string>().ToList(),
                    parsed.Select(p => p.Row.AgreementReference).Where(s => s is not null).Cast<string>().ToList(), c);
                var provenance = await ExistingProvenanceAsync(batchId, c);
                var messages = new List<StagingMessage>();
                foreach (var (row, mapped) in parsed)
                {
                    await faults.AtAsync(FaultPoint.Item, ContractsPhase, row.Sequence, c);
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(row.RawValuesJson) ?? [];
                    // The plan is recomputed against the target as it is now: a record created by someone else since the dry run changes the outcome, it does not cause a failure.
                    var plan = RowPlanner.Plan(mapped, row, ctx);
                    switch (plan.Outcome)
                    {
                        case RowOutcome.Rejected:
                            row.MarkRejectedAtCommit(plan.Messages.FirstOrDefault(m => m.IsError)?.Text ?? "Rejected at commit.");
                            foreach (var m in plan.Messages.Where(m => m.IsError)) messages.Add(k.Message(batchId, MessagePhase.Commit, MessageSubject.Row, row.Id, $"row {row.RowNumber}", true, m.Code, m.Text, m.Field));
                            continue;
                        case RowOutcome.Skipped or RowOutcome.Pending:
                            row.MarkSkipped(plan.ExistingContractId, plan.Summary);
                            continue;
                    }
                    if (plan.Action == PlannedAction.Update)
                        await UpdateAsync(batch, l, ctx, row, plan, provenance, c);
                    else
                        await CreateAsync(batch, l, ctx, row, plan, raw, rawColumns, actor, provenance, c);
                }
                k.Db.Messages.AddRange(messages);
                cp.Advance(rows[^1].Sequence, false, k.Clock.UtcNow);
                batch.RenewLease(owner, k.Clock.UtcNow, k.Options.CommitLease);
                await faults.AtAsync(FaultPoint.ChunkEnd, ContractsPhase, rows[^1].Sequence, c);
                await k.Db.SaveChangesAsync(c);
                return true;
            }, ct);
            contracts.ReleaseTracked(); documents.ReleaseTracked(); k.Db.ChangeTracker.Clear();
            await faults.AtAsync(FaultPoint.ChunkCommitted, ContractsPhase, 0, ct);
            if (!more) return;
        }
    }

    private async Task CreateAsync(MigrationBatch batch, BatchRuntime.Loaded l, PlanContext ctx, StagingRow row, RowPlan plan, Dictionary<string, string> raw, (string? Status, string? Registered) rawColumns, string actor,
        HashSet<string> provenance, CancellationToken ct)
    {
        var v = plan.Values!;
        var supplierId = await EnsureSupplierAsync(batch, l, plan.Supplier!, row, provenance, ct);
        var categoryId = await EnsureCategoryAsync(batch, l, plan.Category!, row, provenance, ct);
        var reference = plan.GenerateReference ? await contracts.AllocateReferenceAsync(batch.OrganisationId, ct) : plan.TargetReference;
        var sourceStatus = rawColumns.Status is not null && raw.TryGetValue(rawColumns.Status, out var s) ? s : v.Status;
        var sourceRegistered = rawColumns.Registered is not null && raw.TryGetValue(rawColumns.Registered, out var rr) && !string.IsNullOrWhiteSpace(rr) ? rr.Trim() : v.RegisteredSource;
        var importRegistered = batch.AllowRegisteredDateImport && plan.LockAsLegacy && v.RegisteredUtc is not null;
        var contractId = await contracts.CreateContractAsync(new MigratedContract(batch.OrganisationId, batch.Id, reference, v.SourceReference, v.Title, v.Description, supplierId, categoryId, v.Status,
            plan.LockAsLegacy, ctx.SourceSystem, v.SourceRecordId, sourceStatus, ctx.SourceExtractedUtc, v.Classification, v.Currency, v.Value, v.ExtensionType, v.LegalReview, v.Start, v.Effective, v.Review, v.End,
            v.RegisteredUtc, sourceRegistered, importRegistered, actor, v.ContractType, v.AgreementType, v.RiskLevel, v.FunctionCode, v.SapOutlineAgreementNumber, v.NoticePeriodValue, v.NoticePeriodUnit, v.CriticalNotice,
            plan.CustomValues, SourceValuesOf(plan, l, raw)), ct);
        row.MarkCreated(contractId);
        AddProvenance(provenance, batch, l, nameof(Ecp.Contracts.Domain.Contract), contractId, ProvenanceAction.Created, null, null, v.SourceRecordId, null, row.Id, null, sourceStatus);
        ctx.ExistingBySource[v.SourceRecordId] = new ExistingContractRef(contractId, reference, ctx.SourceSystem, v.SourceRecordId, plan.LockAsLegacy, plan.LockAsLegacy, false, v.Status, batch.Id, k.Clock.UtcNow, k.Clock.UtcNow);
        ctx.ExistingByReference[reference] = ctx.ExistingBySource[v.SourceRecordId];
    }

    /// <summary>The configured field values as the source file gave them (before any transformation), kept next to the stored value as provenance.</summary>
    private static IReadOnlyDictionary<string, string>? SourceValuesOf(RowPlan plan, BatchRuntime.Loaded l, Dictionary<string, string> raw)
    {
        if (plan.CustomValues is not { Count: > 0 } || plan.CustomSourceValues is null) return plan.CustomSourceValues;
        var result = new Dictionary<string, string>(plan.CustomSourceValues, StringComparer.OrdinalIgnoreCase);
        foreach (var m in l.Definition.Fields.Where(f => MappingCatalogue.IsCustomTarget(f.TargetField)))
            if (raw.TryGetValue(m.SourceColumn, out var original) && !string.IsNullOrWhiteSpace(original) && result.ContainsKey(MappingCatalogue.CustomKeyOf(m.TargetField)))
                result[MappingCatalogue.CustomKeyOf(m.TargetField)] = original.Trim().Length > 450 ? original.Trim()[..450] : original.Trim();
        return result;
    }

    private async Task UpdateAsync(MigrationBatch batch, BatchRuntime.Loaded l, PlanContext ctx, StagingRow row, RowPlan plan, HashSet<string> provenance, CancellationToken ct)
    {
        var existing = plan.Existing!;
        var fields = new Dictionary<string, string?>(plan.UpdateFields!, StringComparer.Ordinal);
        if (fields.ContainsKey("supplierId")) fields["supplierId"] = (await EnsureSupplierAsync(batch, l, plan.Supplier!, row, provenance, ct)).ToString("D");
        if (fields.ContainsKey("categoryId")) fields["categoryId"] = (await EnsureCategoryAsync(batch, l, plan.Category!, row, provenance, ct)).ToString("D");
        var overwrite = batch.IsLegacyLoad && l.Project.LegacyAllowOverwriteLocked;
        var result = await contracts.UpdateContractFieldsAsync(existing.Id, batch.Id, fields, overwrite, overwrite && existing.RecordLocked ? (l.Project.LegacyOverwriteReason ?? "Legacy-load policy") : "Update of mapped fields by migration", ct);
        row.MarkUpdated(existing.Id);
        AddProvenance(provenance, batch, l, nameof(Ecp.Contracts.Domain.Contract), existing.Id, ProvenanceAction.Updated, JsonSerializer.Serialize(result.Before), result.AfterStampUtc, row.SourceRecordId ?? "", null, row.Id, null, null);
    }

    private async Task<Guid> EnsureSupplierAsync(MigrationBatch batch, BatchRuntime.Loaded l, WorkingSupplier s, StagingRow row, HashSet<string> provenance, CancellationToken ct)
    {
        var key = s.VendorNumber ?? "name:" + s.Normalised;
        if (s.Id is { } existing)
        {
            AddProvenance(provenance, batch, l, nameof(Ecp.Contracts.Domain.Supplier), existing, ProvenanceAction.Linked, null, null, key, null, row.Id, null, null);
            return existing;
        }
        var id = await contracts.CreateSupplierAsync(batch.OrganisationId, batch.Id, s.Name, s.VendorNumber, l.Project.SourceSystem, key, s.TaxIdentifier, ct);
        s.Id = id;
        AddProvenance(provenance, batch, l, nameof(Ecp.Contracts.Domain.Supplier), id, ProvenanceAction.Created, null, null, key, null, row.Id, null, null);
        return id;
    }

    private async Task<Guid> EnsureCategoryAsync(MigrationBatch batch, BatchRuntime.Loaded l, WorkingCategory c, StagingRow row, HashSet<string> provenance, CancellationToken ct)
    {
        if (c.Id is { } existing)
        {
            AddProvenance(provenance, batch, l, nameof(Ecp.Contracts.Domain.Category), existing, ProvenanceAction.Linked, null, null, c.Code, null, row.Id, null, null);
            return existing;
        }
        Guid? parent = c.Parent is null ? null : await EnsureCategoryAsync(batch, l, c.Parent, row, provenance, ct);
        var id = await contracts.CreateCategoryAsync(batch.OrganisationId, batch.Id, c.Code, c.Name, parent, l.Project.SourceSystem, c.Code, ct);
        c.Id = id;
        AddProvenance(provenance, batch, l, nameof(Ecp.Contracts.Domain.Category), id, ProvenanceAction.Created, null, null, c.Code, null, row.Id, null, null);
        return id;
    }

    // ---- phase 2: documents ---------------------------------------------------------------------------------------------------------------------------

    private async Task DocumentsAsync(Guid batchId, string owner, BatchRuntime.Loaded l, PlanContext ctx, string actor, CancellationToken ct)
    {
        while (true)
        {
            var more = await k.Uow.ExecuteAsync(async c =>
            {
                var batch = await k.Db.Batches.FirstAsync(b => b.Id == batchId, c);
                var cp = await CheckpointAsync(batchId, DocumentsPhase, c);
                var files = await k.Db.StagedFiles.Where(f => f.BatchId == batchId && f.State == FileState.Matched && f.ContractStagingRowId != null).OrderBy(f => f.RelativePath).Take(k.Options.ChunkSize).ToListAsync(c);
                if (files.Count == 0) { cp.Advance(cp.LastCompletedSequence, true, k.Clock.UtcNow); await k.Db.SaveChangesAsync(c); return false; }

                await faults.AtAsync(FaultPoint.ChunkStart, DocumentsPhase, cp.LastCompletedSequence + 1, c);
                var rowIds = files.Select(f => f.ContractStagingRowId!.Value).Distinct().ToList();
                var rows = await k.Db.StagingRows.AsNoTracking().Where(r => rowIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, c);
                var entryIds = files.Where(f => f.ManifestEntryId != null).Select(f => f.ManifestEntryId!.Value).ToList();
                var entries = await k.Db.ManifestEntries.AsNoTracking().Where(e => entryIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, c);
                var provenance = await ExistingProvenanceAsync(batchId, c);
                var messages = new List<StagingMessage>();

                // Files whose contract was not loaded stay in the queue; the rest are planned together so duplicates inside the chunk are seen.
                var plannable = new List<(StagedFile File, StagingRow Row)>();
                foreach (var f in files)
                {
                    var row = rows[f.ContractStagingRowId!.Value];
                    if (row.TargetContractId is null)
                    {
                        f.RecordMatch(FileState.Unmatched, QueueReason.TargetRejected, f.ManifestEntryId, f.ContractStagingRowId, f.MatchedBy, f.DocumentSourceId, "The [[contract|lower]] this file belongs to was not loaded.");
                        messages.Add(k.Message(batchId, MessagePhase.Commit, MessageSubject.File, f.Id, f.RelativePath, true, "MIG-F015", "The [[contract|lower]] this file belongs to was not loaded, so the file stays in the queue."));
                        continue;
                    }
                    if (f.QuarantineKey is null || !Ecp.Documents.Domain.StorageKey.IsValidFor(k.TenantId, f.QuarantineKey))
                    {
                        // Defence in depth: a staged file can only ever point into this tenant's own store.
                        f.MarkFailed("MIG-F016: the staged content does not belong to this tenant.");
                        messages.Add(k.Message(batchId, MessagePhase.Commit, MessageSubject.File, f.Id, f.RelativePath, true, "MIG-F016", "The staged content does not belong to this tenant, so the file was not imported."));
                        continue;
                    }
                    if (!await storage.ExistsAsync(k.TenantId, f.QuarantineKey!, c))
                    {
                        f.MarkFailed("MIG-F005: the staged content could not be found.");
                        messages.Add(k.Message(batchId, MessagePhase.Commit, MessageSubject.File, f.Id, f.RelativePath, true, "MIG-F005", "The staged content for this file could not be found in the tenant's store."));
                        continue;
                    }
                    plannable.Add((f, row));
                }
                var docFiles = plannable.Select(p => new DocFile(p.File.Id, p.File.RelativePath, p.File.Sha256!, p.Row.Id, p.Row.TargetContractId, p.File.DocumentSourceId!, p.File.ReleasedFromHold)).ToList();
                var bySource = docFiles.Count == 0 ? [] : await documents.FindBySourceAsync(batch.OrganisationId, l.Project.SourceSystem, docFiles.Select(d => d.DocumentSourceId).ToList(), c);
                var byHash = docFiles.Count == 0 ? [] : await documents.FindByHashAsync(docFiles.Select(d => d.Sha256).ToList(), c);
                var decisions = DocumentPlanner.Plan(docFiles, batch.DocumentDuplicateOption, bySource.Select(d => new ExistingDoc(d.Id, d.ContractId, d.DocumentSourceId, d.CurrentSha256)).ToList(),
                    byHash.Select(d => new ExistingDoc(d.Id, d.ContractId, d.DocumentSourceId, d.CurrentSha256)).ToList());

                var created = new Dictionary<Guid, Guid>(); // staged file -> document created in this chunk
                var parentLookup = new Dictionary<string, Guid>(StringComparer.Ordinal);
                foreach (var d in decisions)
                {
                    var (file, row) = plannable.First(p => p.File.Id == d.FileId);
                    await faults.AtAsync(FaultPoint.Item, DocumentsPhase, cp.LastCompletedSequence + 1, c);
                    var entry = file.ManifestEntryId is { } eid && entries.TryGetValue(eid, out var en) ? en : null;
                    if (d.Message is not null)
                        messages.Add(k.Message(batchId, MessagePhase.Commit, MessageSubject.File, file.Id, file.RelativePath, d.IsError, d.MessageCode ?? "MIG-F007", d.Message));
                    switch (d.Action)
                    {
                        case DocAction.ImportNew or DocAction.AddVersionToExisting or DocAction.AddVersionToBatchDocument:
                        {
                            var classification = ClassificationOf(rows[file.ContractStagingRowId!.Value], ctx);
                            Guid? parentId = null;
                            if (entry?.ParentDocumentSourceId is { } parentSource)
                            {
                                parentId = await ResolveParentAsync(batch, l, parentSource, parentLookup, c);
                                if (parentId is null) messages.Add(k.Message(batchId, MessagePhase.Commit, MessageSubject.File, file.Id, file.RelativePath, false, "MIG-W009", $"The parent document '{parentSource}' was not found, so the document was imported without a parent."));
                            }
                            var md = ToDocument(batch, l, file, entry, row, classification, parentId);
                            ImportedDocument imported;
                            if (d.Action == DocAction.ImportNew) imported = await documents.ImportAsync(md, c);
                            else
                            {
                                var target = d.ExistingDocumentId ?? created[d.OriginalFileId!.Value];
                                var before = await documents.GetRollbackStateAsync(target, batch.Id, c);
                                imported = await documents.AddVersionAsync(target, md, c);
                                if (before is { CreatedByBatch: false })
                                    AddProvenance(provenance, batch, l, nameof(Document), target, ProvenanceAction.Updated, JsonSerializer.Serialize(new { versionsBefore = before.VersionCount }), k.Clock.UtcNow,
                                        file.DocumentSourceId!, file.Sha256, row.Id, file.Id, null);
                            }
                            created[file.Id] = imported.DocumentId;
                            file.MarkImported(imported.DocumentId);
                            if (d.Action == DocAction.ImportNew)
                                AddProvenance(provenance, batch, l, nameof(Document), imported.DocumentId, ProvenanceAction.Created, null, null, file.DocumentSourceId!, file.Sha256, row.Id, file.Id, null);
                            parentLookup[file.DocumentSourceId!] = imported.DocumentId;
                            break;
                        }
                        case DocAction.LinkExisting or DocAction.LinkBatchDocument:
                        {
                            var target = d.ExistingDocumentId ?? created[d.OriginalFileId!.Value];
                            file.MarkDuplicate(target, d.Message ?? "Linked to existing content.");
                            AddProvenance(provenance, batch, l, nameof(Document), target, ProvenanceAction.Linked, null, null, file.DocumentSourceId!, file.Sha256, row.Id, file.Id, null);
                            break;
                        }
                        case DocAction.AlreadyImported:
                            file.MarkDuplicate(d.ExistingDocumentId, d.Message ?? "Already imported.");
                            break;
                        case DocAction.RejectDuplicate:
                            file.MarkDuplicate(d.ExistingDocumentId, d.Message ?? "Duplicate content rejected.");
                            break;
                        default:
                            file.HoldForReview(d.Message ?? "Held for review.");
                            break;
                    }
                }
                k.Db.Messages.AddRange(messages);
                cp.Advance(cp.LastCompletedSequence + files.Count, false, k.Clock.UtcNow);
                batch.RenewLease(owner, k.Clock.UtcNow, k.Options.CommitLease);
                await faults.AtAsync(FaultPoint.ChunkEnd, DocumentsPhase, cp.LastCompletedSequence, c);
                await k.Db.SaveChangesAsync(c);
                return true;
            }, ct);
            contracts.ReleaseTracked(); documents.ReleaseTracked(); k.Db.ChangeTracker.Clear();
            await faults.AtAsync(FaultPoint.ChunkCommitted, DocumentsPhase, 0, ct);
            if (!more) return;
        }
    }

    private async Task<Guid?> ResolveParentAsync(MigrationBatch batch, BatchRuntime.Loaded l, string parentSource, Dictionary<string, Guid> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(parentSource, out var id)) return id;
        var found = await documents.FindBySourceAsync(batch.OrganisationId, l.Project.SourceSystem, [parentSource], ct);
        return found.Count > 0 ? found[0].Id : null;
    }

    private static MigratedDocument ToDocument(MigrationBatch batch, BatchRuntime.Loaded l, StagedFile file, AttachmentManifestEntry? entry, StagingRow row, string classification, Guid? parentId)
    {
        var fileName = entry?.FileName ?? file.FileName;
        var title = Path.GetFileNameWithoutExtension(fileName);
        return new MigratedDocument(batch.OrganisationId, batch.Id, row.TargetContractId!.Value, entry?.DocumentTypeCode ?? ReferenceDocumentTypes.DefaultCode, string.IsNullOrWhiteSpace(title) ? fileName : title,
            fileName, file.MimeType ?? "application/octet-stream", file.QuarantineKey!, file.Sha256!, file.SizeBytes, classification, entry?.IsExecutedCopy ?? false, entry?.VersionLabel, entry?.DocumentDate,
            l.Project.SourceSystem, file.DocumentSourceId!, parentId, file.Scan switch { FileScan.Clean => ScanStatus.Clean, FileScan.Skipped => ScanStatus.Skipped, FileScan.Error => ScanStatus.Error, _ => ScanStatus.Pending });
    }

    private static string ClassificationOf(StagingRow row, PlanContext ctx)
    {
        if (row.MappedValuesJson is not null && JsonSerializer.Deserialize<Dictionary<string, string>>(row.MappedValuesJson) is { } m && m.TryGetValue("classification", out var c) && !string.IsNullOrWhiteSpace(c)) return c;
        return ctx.DefaultClassification;
    }

    // ---- shared ----------------------------------------------------------------------------------------------------------------------------------------

    private static MappedRow Reconstruct(StagingRow row)
    {
        var mapped = new MappedRow();
        if (row.MappedValuesJson is not null)
            foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(row.MappedValuesJson) ?? []) mapped.Values[key] = value;
        return mapped;
    }

    private async Task<Checkpoint> CheckpointAsync(Guid batchId, string phase, CancellationToken ct)
    {
        var cp = await k.Db.Checkpoints.FirstOrDefaultAsync(x => x.BatchId == batchId && x.Phase == phase, ct);
        if (cp is null) { cp = Checkpoint.Start(k.TenantId, batchId, phase, k.Clock.UtcNow); k.Db.Checkpoints.Add(cp); }
        return cp;
    }

    private async Task<HashSet<string>> ExistingProvenanceAsync(Guid batchId, CancellationToken ct)
        => (await k.Db.Provenance.AsNoTracking().Where(p => p.BatchId == batchId).Select(p => new { p.TargetEntityType, p.TargetEntityId, p.Action }).ToListAsync(ct))
            .Select(p => $"{p.TargetEntityType}|{p.TargetEntityId}|{p.Action}").ToHashSet(StringComparer.Ordinal);

    private void AddProvenance(HashSet<string> seen, MigrationBatch batch, BatchRuntime.Loaded l, string entityType, Guid entityId, ProvenanceAction action, string? before, DateTime? afterStamp,
        string sourceRecordId, string? sha, Guid? rowId, Guid? fileId, string? sourceStatus)
    {
        if (!seen.Add($"{entityType}|{entityId}|{action}")) return; // already recorded for this batch (a replay or a second use of the same supplier)
        k.Db.Provenance.Add(ProvenanceRecord.Create(k.TenantId, batch.Id, batch.OrganisationId, entityType, entityId, action, before, afterStamp, l.Project.SourceSystem, string.IsNullOrEmpty(sourceRecordId) ? "-" : sourceRecordId,
            sha, rowId, fileId, l.ContractsFile?.Sha256, batch.MappingProfileVersionId, l.Project.ExtractionUtc, sourceStatus, k.Clock.UtcNow));
    }

    // ---- reconciliation and acceptance -----------------------------------------------------------------------------------------------------------------

    public Task<ReconciliationDto> PreviewReconciliationAsync(Guid batchId, CancellationToken ct = default) => reconciler.PreviewAsync(batchId, ct);

    public Task<ReconciliationDto> ReconcileAsync(Guid batchId, bool approveExceptions, string? note, CancellationToken ct = default) => reconciler.ReconcileAsync(batchId, approveExceptions, note, ct);

    public async Task AcceptAsync(Guid batchId, SampleCheckInput input, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Accept, track: true, ct);
        if (batch.Status != BatchStatus.Reconciled) throw new DomainException("invalid_batch_transition", $"A batch in state {batch.Status} cannot be accepted. Reconcile it first.");
        if (string.IsNullOrWhiteSpace(input.Note) || input.Note.Trim().Length < 10)
            throw new DomainException("acceptance_note_required", "Record what you checked (at least a sentence): the sample of records and [[document|plural|lower]] the business owner verified.");
        var outstanding = await k.Db.StagedFiles.CountAsync(f => f.BatchId == batchId && (f.State == FileState.Unmatched || f.State == FileState.Failed), ct)
            + await k.Db.StagingRows.CountAsync(r => r.BatchId == batchId && r.Outcome == RowOutcome.Rejected, ct);
        if (outstanding > 0 && !input.OutstandingAcknowledged)
            throw new DomainException("outstanding_items", $"{outstanding} rejected row(s) or unresolved file(s) remain. Resolve or discard them, or acknowledge that the business accepts them as exceptions.");
        batch.Accept(await k.ActorLabelAsync(ct), input.Note.Trim(), k.Clock.UtcNow);
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.batch.accepted", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId, new { batch.AcceptedBy, batch.AcceptedUtc, Outstanding = outstanding, input.OutstandingAcknowledged }, input.Note.Trim());
        }, ct);
    }
}
