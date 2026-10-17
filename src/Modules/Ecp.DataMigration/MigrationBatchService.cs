using System.Text.Json;
using Ecp.Configuration;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Mapping;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

public interface IMigrationBatches
{
    Task<Guid> CreateAsync(CreateBatchInput input, CancellationToken ct = default);
    Task<BatchDetail> GetAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>Pins a published profile version (and the configuration release in force) to the batch. Later changes to the profile or the configuration do not alter the batch.</summary>
    Task SelectProfileAsync(Guid batchId, Guid profileVersionId, CancellationToken ct = default);
    Task SetStrategiesAsync(Guid batchId, DuplicateStrategy duplicateStrategy, DocumentDuplicateOption documentOption, CancellationToken ct = default);
    Task<PagedResult<RowDto>> ListRowsAsync(Guid batchId, RowFilter filter, CancellationToken ct = default);
    Task<PagedResult<MessageDto>> ListMessagesAsync(Guid batchId, MessageFilter filter, CancellationToken ct = default);
    Task<PagedResult<StagedFileDto>> ListFilesAsync(Guid batchId, FileFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<ManifestEntryDto>> ListManifestAsync(Guid batchId, int take = 200, CancellationToken ct = default);
    Task<IReadOnlyList<ContractChoice>> ListContractChoicesAsync(Guid batchId, string? search, int take = 50, CancellationToken ct = default);
    /// <summary>A person's decision on a row the duplicate strategy sent to manual review.</summary>
    Task DecideRowAsync(Guid batchId, Guid rowId, ReviewDecision decision, CancellationToken ct = default);
    /// <summary>A person pairs a queued file with a contract row (method Manual, recorded with their name).</summary>
    Task ResolveFileAsync(Guid batchId, Guid fileId, Guid contractRowId, CancellationToken ct = default);
    Task ReleaseHeldFileAsync(Guid batchId, Guid fileId, CancellationToken ct = default);
    /// <summary>A deliberate, audited discard. The file stays in staging, marked discarded; it is never silently dropped.</summary>
    Task DiscardFileAsync(Guid batchId, Guid fileId, string reason, CancellationToken ct = default);
}

internal sealed class MigrationBatchService(MigrationKernel k, IConfigurationService configuration) : IMigrationBatches
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid> CreateAsync(CreateBatchInput r, CancellationToken ct = default)
    {
        var project = await k.ProjectAsync(r.ProjectId, MigrationPermissions.Stage, track: true, ct);
        if (r.IsLegacyLoad) await k.Access.RequireAsync(MigrationPermissions.LegacyLoad, MigrationKernel.Describe(project.TenantId, project.OrganisationId, project.Id.ToString()), ct);
        Guid? profileVersionId = null; Guid? releaseId = null; int? releaseNumber = null;
        if (r.ProfileVersionId is { } vid)
        {
            await RequirePublishedVersionAsync(vid, project.OrganisationId, ct);
            profileVersionId = vid;
        }
        if (await configuration.LatestReleaseOfAnyKindAsync(project.OrganisationId, ct) is { } release) { releaseId = release.Id; releaseNumber = release.Number; }
        var batch = project.CreateBatch(r.Kind, r.DuplicateStrategy, r.DocumentOption, r.IsLegacyLoad, profileVersionId, releaseId, k.Clock.UtcNow, await k.ActorLabelAsync(ct), k.UserId, r.Name, releaseNumber);
        return await k.Uow.ExecuteAsync(async c =>
        {
            k.Db.Batches.Add(batch);
            await k.Db.SaveChangesAsync(c);
            // The batch id is carried on every audit event the batch produces (INV-41).
            k.Record("migration.batch.created", nameof(MigrationBatch), batch.Id.ToString(), batch.OrganisationId, batch.Id,
                new { batch.Kind, batch.DuplicateStrategy, batch.DocumentDuplicateOption, batch.IsLegacyLoad, profileVersionId, ConfigReleaseNumber = releaseNumber });
            return batch.Id;
        }, ct);
    }

    public async Task SelectProfileAsync(Guid batchId, Guid profileVersionId, CancellationToken ct = default)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        await RequirePublishedVersionAsync(profileVersionId, project.OrganisationId, ct);
        int? number = batch.ConfigReleaseNumber; Guid? releaseId = batch.ConfigReleaseId;
        if (await configuration.LatestReleaseOfAnyKindAsync(project.OrganisationId, ct) is { } release) { releaseId = release.Id; number = release.Number; }
        batch.CapturePinnedVersions(profileVersionId, releaseId, number);
        batch.InputsChanged(hasSources: await k.Db.SourceFiles.AnyAsync(s => s.BatchId == batchId, ct));
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.batch.profile_selected", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId, new { profileVersionId, ConfigReleaseNumber = number });
        }, ct);
    }

    public async Task SetStrategiesAsync(Guid batchId, DuplicateStrategy duplicateStrategy, DocumentDuplicateOption documentOption, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        batch.SetStrategies(duplicateStrategy, documentOption); // the duplicate strategy is chosen before commit
        batch.InputsChanged(hasSources: await k.Db.SourceFiles.AnyAsync(s => s.BatchId == batchId, ct));
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.batch.strategies_set", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId, new { duplicateStrategy, documentOption });
        }, ct);
    }

    private async Task RequirePublishedVersionAsync(Guid versionId, Guid organisationId, CancellationToken ct)
    {
        var info = await (from v in k.Db.ProfileVersions.AsNoTracking()
                          join p in k.Db.Profiles.AsNoTracking() on v.ProfileId equals p.Id
                          where v.Id == versionId
                          select new { v.Status, p.OrganisationId }).FirstOrDefaultAsync(ct);
        // A profile of another organisation (or tenant) is simply not found.
        if (info is null || info.OrganisationId != organisationId) throw new ResourceNotFoundException();
        if (info.Status != ProfileVersionStatus.Published) throw new DomainException("profile_not_published", "Only a published profile version can be used by a batch.");
    }

    public async Task<BatchDetail> GetAsync(Guid batchId, CancellationToken ct = default)
    {
        var (b, p) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var sources = await k.Db.SourceFiles.AsNoTracking().Where(s => s.BatchId == batchId).OrderBy(s => s.Role).ToListAsync(ct);
        var sourceDtos = sources.Select(s =>
        {
            using var doc = JsonDocument.Parse(s.ProfileJson);
            var headers = doc.RootElement.TryGetProperty("headers", out var h) ? h.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            var notices = doc.RootElement.TryGetProperty("notices", out var n) ? n.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            return new SourceFileDto(s.Id, s.Role, s.Format, s.FileName, s.Sha256, s.SizeBytes, s.RowCount, s.SheetName, s.Encoding, s.Delimiter, s.UploadedBy, s.UploadedUtc, headers, notices, s.Note);
        }).ToList();

        string? profileName = null; int? profileVersion = null;
        if (b.MappingProfileVersionId is { } vid)
        {
            var v = await (from ver in k.Db.ProfileVersions.AsNoTracking() join prof in k.Db.Profiles.AsNoTracking() on ver.ProfileId equals prof.Id where ver.Id == vid select new { prof.Name, ver.VersionNumber }).FirstOrDefaultAsync(ct);
            profileName = v?.Name; profileVersion = v?.VersionNumber;
        }
        var rowGroups = await k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId).GroupBy(r => r.Outcome).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var review = await k.Db.StagingRows.AsNoTracking().CountAsync(r => r.BatchId == batchId && r.ReviewDecision == ReviewDecision.Required, ct);
        var msg = await k.Db.Messages.AsNoTracking().Where(m => m.BatchId == batchId).GroupBy(m => m.Severity).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var files = await k.Db.StagedFiles.AsNoTracking().Where(f => f.BatchId == batchId).GroupBy(f => f.State).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var manifest = await k.Db.ManifestEntries.AsNoTracking().CountAsync(e => e.BatchId == batchId, ct);
        var counts = new StagingCounts(rowGroups.Values.Sum(), rowGroups.GetValueOrDefault(RowOutcome.Accepted), rowGroups.GetValueOrDefault(RowOutcome.Rejected), rowGroups.GetValueOrDefault(RowOutcome.Skipped),
            rowGroups.GetValueOrDefault(RowOutcome.Pending), rowGroups.GetValueOrDefault(RowOutcome.Created), rowGroups.GetValueOrDefault(RowOutcome.Updated), review,
            msg.GetValueOrDefault(MessageSeverity.Error), msg.GetValueOrDefault(MessageSeverity.Warning), manifest, files.Values.Sum(), files.GetValueOrDefault(FileState.Matched),
            files.GetValueOrDefault(FileState.Unmatched), files.GetValueOrDefault(FileState.Invalid), files.GetValueOrDefault(FileState.Duplicate), files.GetValueOrDefault(FileState.Imported),
            files.GetValueOrDefault(FileState.Failed), files.GetValueOrDefault(FileState.Discarded));
        var validation = b.ValidationSummaryJson is null ? null : JsonSerializer.Deserialize<ValidationSummary>(b.ValidationSummaryJson, Json);
        var checkpoints = (await k.Db.Checkpoints.AsNoTracking().Where(c => c.BatchId == batchId).OrderBy(c => c.UpdatedUtc).ToListAsync(ct)).Select(c => new CheckpointDto(c.Phase, c.LastCompletedSequence, c.PhaseComplete, c.UpdatedUtc)).ToList();

        var now = k.Clock.UtcNow;
        var stage = await k.HoldsAsync(MigrationPermissions.Stage, p, ct); var commit = await k.HoldsAsync(MigrationPermissions.Commit, p, ct);
        var legacy = await k.HoldsAsync(MigrationPermissions.LegacyLoad, p, ct); var accept = await k.HoldsAsync(MigrationPermissions.Accept, p, ct);
        var rollback = await k.HoldsAsync(MigrationPermissions.Rollback, p, ct);
        var leaseExpired = b.Status == BatchStatus.Committing && (b.LeaseExpiresUtc is null || b.LeaseExpiresUtc <= now);
        var committable = b.Kind != BatchKind.DryRun && (b.Status is BatchStatus.Validated or BatchStatus.Failed || leaseExpired);
        var confirmationOk = !b.IsLegacyLoad || (b.HasValidConfirmation && b.ConfirmedByUserId != k.UserId);
        var can = new BatchCapabilities(
            CanStage: stage && b.InputsEditable,
            CanValidate: stage && b.InputsEditable && sources.Any(s => s.Role == SourceFileRole.Contracts) && b.MappingProfileVersionId is not null,
            CanConfirm: legacy && b.IsLegacyLoad && b.Status == BatchStatus.Validated && !b.HasValidConfirmation,
            CanCommit: commit && committable && (!b.IsLegacyLoad || legacy) && confirmationOk && review == 0,
            CanReconcile: commit && b.Status == BatchStatus.Committed,
            CanApproveExceptions: accept && b.Status == BatchStatus.Committed,
            CanAccept: accept && b.Status == BatchStatus.Reconciled,
            CanRollBack: rollback && (b.Status is BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Failed || leaseExpired),
            CanCompensate: rollback && b.Status is BatchStatus.Committed or BatchStatus.Reconciled or BatchStatus.Accepted);

        return new BatchDetail(b.Id, b.ProjectId, p.Name, b.OrganisationId, p.SourceSystem, b.Name, b.Kind, b.Status, b.IsLegacyLoad, b.AllowRegisteredDateImport, b.DuplicateStrategy, b.DocumentDuplicateOption,
            p.ReferencePolicy, b.MappingProfileVersionId, profileName, profileVersion, b.ConfigReleaseNumber, b.CreatedBy, b.CreatedUtc, b.ValidatedUtc, b.ConfirmedBy, b.ConfirmedUtc, b.HasValidConfirmation,
            b.CommittedBy, b.CommitStartedUtc, b.CommitCompletedUtc, b.FailureReason, b.AcceptedBy, b.AcceptedUtc, b.AcceptanceNote, b.RollbackReason, b.RolledBackBy, b.RolledBackUtc, b.ExceptionsApproved,
            sourceDtos, counts, validation, checkpoints, can);
    }

    public async Task<PagedResult<RowDto>> ListRowsAsync(Guid batchId, RowFilter f, CancellationToken ct = default)
    {
        await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var query = k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId);
        if (f.Outcome is { } o) query = query.Where(r => r.Outcome == o);
        if (f.ReviewOnly) query = query.Where(r => r.ReviewDecision != ReviewDecision.None);
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, f.Page); var size = Math.Clamp(f.PageSize, 1, 200);
        var rows = await query.OrderBy(r => r.Sequence).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var items = rows.Select(r => new RowDto(r.Id, r.Sequence, r.RowNumber, r.SourceRecordId, r.AgreementReference, r.Outcome, r.PlannedAction, r.ReviewDecision, r.ErrorCount, r.WarningCount, r.PlanSummary,
            r.TargetContractId, Visible(JsonSerializer.Deserialize<Dictionary<string, string>>(r.RawValuesJson) ?? []), r.MappedValuesJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(r.MappedValuesJson))).ToList();
        return new PagedResult<RowDto>(items, total, page, size);
    }

    private static Dictionary<string, string> Visible(Dictionary<string, string> raw) => raw.Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value);

    public async Task<PagedResult<MessageDto>> ListMessagesAsync(Guid batchId, MessageFilter f, CancellationToken ct = default)
    {
        await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var query = k.Db.Messages.AsNoTracking().Where(m => m.BatchId == batchId);
        if (f.Severity is { } s) query = query.Where(m => m.Severity == s);
        if (!string.IsNullOrWhiteSpace(f.Code)) { var code = f.Code.Trim(); query = query.Where(m => m.Code == code); }
        if (f.Subject is { } sub) query = query.Where(m => m.Subject == sub);
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, f.Page); var size = Math.Clamp(f.PageSize, 1, 500);
        var items = await query.OrderBy(m => m.Severity).ThenBy(m => m.Code).ThenBy(m => m.Location).ThenBy(m => m.Id).Skip((page - 1) * size).Take(size)
            .Select(m => new MessageDto(m.Id, m.Phase, m.Subject, m.SubjectId, m.Location, m.Severity, m.Code, m.Message, m.FieldName)).ToListAsync(ct);
        return new PagedResult<MessageDto>(items, total, page, size);
    }

    public async Task<PagedResult<StagedFileDto>> ListFilesAsync(Guid batchId, FileFilter f, CancellationToken ct = default)
    {
        await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var query = k.Db.StagedFiles.AsNoTracking().Where(x => x.BatchId == batchId);
        if (f.State is { } st) query = query.Where(x => x.State == st);
        if (f.QueueOnly) query = query.Where(x => x.State == FileState.Unmatched || x.State == FileState.Failed);
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, f.Page); var size = Math.Clamp(f.PageSize, 1, 200);
        var files = await query.OrderBy(x => x.RelativePath).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = files.Select(x => x.Id).ToList();
        var proposals = await k.Db.Proposals.AsNoTracking().Where(p => ids.Contains(p.StagedFileId)).ToListAsync(ct);
        var rowIds = files.Where(x => x.ContractStagingRowId != null).Select(x => x.ContractStagingRowId!.Value).Concat(proposals.Select(p => p.ContractStagingRowId)).Distinct().ToList();
        var rows = await k.Db.StagingRows.AsNoTracking().Where(r => rowIds.Contains(r.Id)).Select(r => new { r.Id, r.Sequence, r.SourceRecordId, r.AgreementReference }).ToDictionaryAsync(r => r.Id, ct);
        var entryIds = files.Where(x => x.ManifestEntryId != null).Select(x => x.ManifestEntryId!.Value).ToList();
        var types = await k.Db.ManifestEntries.AsNoTracking().Where(e => entryIds.Contains(e.Id)).Select(e => new { e.Id, e.DocumentTypeCode }).ToDictionaryAsync(e => e.Id, e => e.DocumentTypeCode, ct);
        var items = files.Select(x => new StagedFileDto(x.Id, x.RelativePath, x.FileName, x.Sha256, x.SizeBytes, x.Scan, x.State, x.QueueReason, x.MatchedBy, x.ContractStagingRowId,
            x.ContractStagingRowId is { } rid && rows.TryGetValue(rid, out var row) ? row.AgreementReference : null, x.Note, x.ResolvedBy, x.ResolvedUtc, x.TargetDocumentId,
            x.ManifestEntryId is { } eid && types.TryGetValue(eid, out var t) ? t : null,
            proposals.Where(p => p.StagedFileId == x.Id).OrderBy(p => p.Reason).Select(p => rows.TryGetValue(p.ContractStagingRowId, out var pr)
                ? new ProposalDto(p.Id, p.ContractStagingRowId, pr.Sequence, pr.SourceRecordId, pr.AgreementReference, p.Method, p.Reason, p.Status, p.ConfirmedBy)
                : new ProposalDto(p.Id, p.ContractStagingRowId, 0, null, null, p.Method, p.Reason, p.Status, p.ConfirmedBy)).ToList())).ToList();
        return new PagedResult<StagedFileDto>(items, total, page, size);
    }

    public async Task<IReadOnlyList<ManifestEntryDto>> ListManifestAsync(Guid batchId, int take = 200, CancellationToken ct = default)
    {
        await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        return await k.Db.ManifestEntries.AsNoTracking().Where(e => e.BatchId == batchId).OrderBy(e => e.Sequence).Take(Math.Clamp(take, 1, 1000))
            .Select(e => new ManifestEntryDto(e.Id, e.RowNumber, e.SourceRecordId, e.AgreementReference, e.RelativePath, e.DocumentTypeRaw, e.DocumentTypeCode, e.State, e.StagedFileId)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ContractChoice>> ListContractChoicesAsync(Guid batchId, string? search, int take = 50, CancellationToken ct = default)
    {
        await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var query = k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId && r.Outcome != RowOutcome.Rejected);
        if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToUpperInvariant(); query = query.Where(r => (r.AgreementReference != null && r.AgreementReference.ToUpper().Contains(term)) || (r.SourceRecordId != null && r.SourceRecordId.ToUpper().Contains(term))); }
        return await query.OrderBy(r => r.Sequence).Take(Math.Clamp(take, 1, 200)).Select(r => new ContractChoice(r.Id, r.Sequence, r.SourceRecordId, r.AgreementReference, r.Outcome)).ToListAsync(ct);
    }

    public async Task DecideRowAsync(Guid batchId, Guid rowId, ReviewDecision decision, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        if (!batch.InputsEditable) throw new DomainException("batch_locked", "Decisions can be made only before the batch starts to commit.");
        var row = await k.Db.StagingRows.FirstOrDefaultAsync(r => r.Id == rowId && r.BatchId == batchId, ct) ?? throw new ResourceNotFoundException();
        row.Decide(decision);
        batch.InputsChanged(hasSources: true);
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.row.decided", nameof(StagingRow), rowId.ToString(), batch.OrganisationId, batchId, new { row.RowNumber, row.SourceRecordId, decision = decision.ToString() });
        }, ct);
    }

    public async Task ResolveFileAsync(Guid batchId, Guid fileId, Guid contractRowId, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        if (!batch.InputsEditable) throw new DomainException("batch_locked", "Files can be matched only before the batch starts to commit.");
        var file = await k.Db.StagedFiles.FirstOrDefaultAsync(x => x.Id == fileId && x.BatchId == batchId, ct) ?? throw new ResourceNotFoundException();
        // F015: the target must be a contract row of this batch (another batch's or tenant's rows are not visible here).
        var row = await k.Db.StagingRows.AsNoTracking().FirstOrDefaultAsync(r => r.Id == contractRowId && r.BatchId == batchId, ct)
            ?? throw new DomainException("target_not_found", "MIG-F015: The target [[contract|lower]] was not found in this batch.");
        if (row.Outcome == RowOutcome.Rejected) throw new DomainException("target_rejected", "MIG-F015: That [[contract|lower]] row was rejected, so a file cannot be attached to it.");
        var by = await k.ActorLabelAsync(ct);
        file.ResolveManually(contractRowId, by, k.Clock.UtcNow);
        var now = k.Clock.UtcNow;
        foreach (var old in await k.Db.Proposals.Where(p => p.StagedFileId == fileId).ToListAsync(ct)) old.Supersede();
        var proposal = MatchProposal.Create(k.TenantId, batchId, fileId, contractRowId, MatchMethod.Manual, $"Matched by hand by {by} to contract row {row.Sequence} ({row.AgreementReference ?? row.SourceRecordId}).", 1, ProposalStatus.Proposed);
        proposal.Confirm(by, now);
        k.Db.Proposals.Add(proposal);
        batch.InputsChanged(hasSources: true);
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.file.matched_manually", nameof(StagedFile), fileId.ToString(), batch.OrganisationId, batchId, new { file.RelativePath, contractRow = row.Sequence, row.SourceRecordId });
        }, ct);
    }

    public async Task ReleaseHeldFileAsync(Guid batchId, Guid fileId, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        if (!batch.InputsEditable) throw new DomainException("batch_locked", "Files can be released only before the batch starts to commit.");
        var file = await k.Db.StagedFiles.FirstOrDefaultAsync(x => x.Id == fileId && x.BatchId == batchId, ct) ?? throw new ResourceNotFoundException();
        file.ReleaseHeld(await k.ActorLabelAsync(ct), k.Clock.UtcNow);
        batch.InputsChanged(hasSources: true);
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.file.released", nameof(StagedFile), fileId.ToString(), batch.OrganisationId, batchId, new { file.RelativePath });
        }, ct);
    }

    public async Task DiscardFileAsync(Guid batchId, Guid fileId, string reason, CancellationToken ct = default)
    {
        var (batch, _) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        if (!batch.InputsEditable && batch.Status is not (BatchStatus.Committed or BatchStatus.Reconciled)) throw new DomainException("batch_locked", "Files can be discarded only before the batch starts to commit, or after it has committed.");
        var file = await k.Db.StagedFiles.FirstOrDefaultAsync(x => x.Id == fileId && x.BatchId == batchId, ct) ?? throw new ResourceNotFoundException();
        file.Discard(reason, await k.ActorLabelAsync(ct), k.Clock.UtcNow);
        if (batch.InputsEditable) batch.InputsChanged(hasSources: true);
        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.file.discarded", nameof(StagedFile), fileId.ToString(), batch.OrganisationId, batchId, new { file.RelativePath, file.Sha256 }, reason);
        }, ct);
    }
}
