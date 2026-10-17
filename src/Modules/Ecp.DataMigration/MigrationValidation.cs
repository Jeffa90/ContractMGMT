using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Contracts;
using Ecp.Contracts.Domain;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Mapping;
using Ecp.DataMigration.Planning;
using Ecp.Documents;
using Ecp.Identity;
using Ecp.SharedKernel;
using Ecp.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

public sealed record MappingPreview(IReadOnlyList<MappingStat> Stats, IReadOnlyList<PreviewRow> Sample, int Rows);

public interface IMigrationValidation
{
    /// <summary>
    /// The dry run: reads the staged sources, maps, validates, resolves suppliers and categories, checks duplicates and legacy-load rules, and matches attachments. It writes staging rows,
    /// messages and audit events only: nothing in the target (INV-40). Repeatable; each run replaces the previous run's results.
    /// </summary>
    Task<ValidationSummary> ValidateAsync(Guid batchId, CancellationToken ct = default);
    /// <summary>Original and transformed values for a sample, and how many rows each mapping changes, before anything is committed.</summary>
    Task<MappingPreview> PreviewMappingAsync(Guid batchId, int sampleRows = 10, CancellationToken ct = default);
}

/// <summary>Loads everything a validation or a commit needs to plan rows, from the current state of the batch and the target.</summary>
internal sealed class BatchRuntime(MigrationKernel k, IContractMigrationPort contracts, IOrganisationStructureService orgs, IClassificationScaleProvider scales)
{
    public sealed record Loaded(MigrationBatch Batch, MigrationProject Project, MappingProfileVersion? Version, MappingDefinition Definition, SourceFile? ContractsFile, SourceFile? ManifestFile,
        SourceFile? ArchiveFile, OrganisationSummary Organisation);

    public async Task<Loaded> LoadAsync(MigrationBatch batch, MigrationProject project, CancellationToken ct)
    {
        MappingProfileVersion? version = null;
        if (batch.MappingProfileVersionId is { } vid) version = await k.Db.ProfileVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct);
        var sources = await k.Db.SourceFiles.AsNoTracking().Where(s => s.BatchId == batch.Id).ToListAsync(ct);
        var org = await orgs.GetOrganisationAsync(batch.OrganisationId, ct) ?? throw new DomainException("organisation_not_found", "The [[organisation|lower]] was not found.");
        return new Loaded(batch, project, version, version is null ? MappingDefinition.Empty : MappingDefinition.FromJson(version.DefinitionJson),
            sources.FirstOrDefault(s => s.Role == SourceFileRole.Contracts), sources.FirstOrDefault(s => s.Role == SourceFileRole.Manifest), sources.FirstOrDefault(s => s.Role == SourceFileRole.Archive), org);
    }

    public async Task<PlanContext> BuildContextAsync(Loaded l, CancellationToken ct)
    {
        var scale = await scales.GetAsync(ct);
        var ctx = new PlanContext
        {
            TenantId = k.TenantId, OrganisationId = l.Batch.OrganisationId, SourceSystem = l.Project.SourceSystem, SourceExtractedUtc = l.Project.ExtractionUtc, Project = l.Project, Batch = l.Batch,
            Definition = l.Definition, InitialStatusCode = await contracts.InitialStatusAsync(l.Batch.OrganisationId, ct), IsKnownClassification = scale.IsKnown, DefaultClassification = scale.DefaultLevel, NowUtc = k.Clock.UtcNow,
            CustomChecker = l.Definition.Fields.Any(f => MappingCatalogue.IsCustomTarget(f.TargetField)) ? await contracts.CustomFieldCheckerAsync(l.Batch.OrganisationId, ct) : null
        };
        var snapshot = await contracts.SnapshotReferenceDataAsync(l.Batch.OrganisationId, ct);
        foreach (var s in snapshot.Suppliers) ctx.Suppliers.Add(new WorkingSupplier(s.Id, s.Name, s.NormalisedName, s.VendorNumber));
        var byId = new Dictionary<Guid, WorkingCategory>();
        // Parents first, so a child can point at its parent object.
        var pending = snapshot.Categories.ToList();
        var guard = 0;
        while (pending.Count > 0 && guard++ < 64)
        {
            foreach (var c in pending.ToList())
            {
                if (c.ParentCategoryId is { } p && !byId.ContainsKey(p)) continue;
                var w = new WorkingCategory(c.Id, c.Code, c.Name, c.NormalisedName, c.ParentCategoryId is { } pid ? byId[pid] : null, c.IsActive);
                byId[c.Id] = w; ctx.Categories.Add(w); pending.Remove(c);
            }
        }
        return ctx;
    }

    public async Task LoadExistingAsync(PlanContext ctx, IReadOnlyCollection<string> sourceIds, IReadOnlyCollection<string> references, CancellationToken ct)
    {
        foreach (var c in await contracts.FindContractsAsync(ctx.OrganisationId, ctx.SourceSystem, sourceIds, references, ct))
        {
            if (c.SourceSystem == ctx.SourceSystem && c.SourceRecordId is not null) ctx.ExistingBySource[c.SourceRecordId] = c;
            ctx.ExistingByReference[c.AgreementReference] = c;
        }
        if (ctx.Batch.DuplicateStrategy is DuplicateStrategy.UpdateMapped or DuplicateStrategy.ManualReview && ctx.ExistingBySource.Count > 0)
            foreach (var (id, fields) in await contracts.CaptureFieldsManyAsync(ctx.ExistingBySource.Values.Select(e => e.Id).ToList(),
                [.. Contract.MigratableFields, .. ctx.Definition.Fields.Where(f => MappingCatalogue.IsCustomTarget(f.TargetField)).Select(f => "cf." + MappingCatalogue.CustomKeyOf(f.TargetField))], ct)) ctx.ExistingFields[id] = fields;
    }

    public static MappingContext MappingContextFor(Loaded l)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(l.Organisation.TimeZoneId); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Utc; }
        return new MappingContext(zone, l.ContractsFile?.Format == SourceFormat.Xlsx);
    }
}

internal sealed class MigrationValidationService(MigrationKernel k, BatchRuntime runtime, IDocumentMigrationPort documents, IContractMigrationPort contracts) : IMigrationValidation
{
    private const int PageSize = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<MappingPreview> PreviewMappingAsync(Guid batchId, int sampleRows = 10, CancellationToken ct = default)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var l = await runtime.LoadAsync(batch, project, ct);
        if (l.Version is null) throw new DomainException("profile_required", "Choose a published mapping profile version first.");
        if (l.ContractsFile is null) throw new DomainException("source_required", "Upload the [[contract|plural|lower]] file first.");
        var stats = new Dictionary<(string, string), int[]>(); var sample = new List<PreviewRow>(); var rows = 0;
        var mctx = BatchRuntime.MappingContextFor(l);
        await foreach (var r in k.Db.StagingRows.AsNoTracking().Where(x => x.BatchId == batchId).OrderBy(x => x.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            rows++;
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(r.RawValuesJson) ?? [];
            var mapped = MappingEngine.MapRow(l.Definition, raw, mctx);
            Tally(stats, l.Definition, mapped);
            if (sample.Count < sampleRows)
                sample.Add(new PreviewRow(r.RowNumber, mapped.HasErrors ? "Error" : "Mapped", "", mapped.Trace.Select(t => new PreviewValue(t.SourceColumn, t.TargetField, t.Original, t.Transformed, t.Changed, t.Failed)).ToList()));
        }
        return new MappingPreview(StatsList(stats, l.Definition), sample, rows);
    }

    public async Task<ValidationSummary> ValidateAsync(Guid batchId, CancellationToken ct = default)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        if (!batch.InputsEditable) throw new DomainException("batch_locked", $"A batch in state {batch.Status} can no longer be validated.");
        var l = await runtime.LoadAsync(batch, project, ct);
        var summary = new ValidationSummary { GeneratedUtc = k.Clock.UtcNow };

        // ---- preconditions: anything that makes the run meaningless is reported together and stops it -------------------------------------------------
        if (l.ContractsFile is null) summary.Blockers.Add("Upload the [[contract|plural|lower]] file first.");
        if (l.Version is null) summary.Blockers.Add("Choose a mapping profile version for the batch.");
        else if (l.Version.Status != ProfileVersionStatus.Published) summary.Blockers.Add("The mapping profile version must be published before it can be used by a batch.");
        else foreach (var issue in MappingDefinitionValidator.Validate(l.Definition).Where(i => i.IsError)) summary.Blockers.Add($"Profile: {issue.Field}: {issue.Message}");
        try { await contracts.InitialStatusAsync(batch.OrganisationId, ct); }
        catch (DomainException ex) { summary.Blockers.Add($"MIG-E026: The organisation has no lifecycle definition to take its [[contract|plural|lower]]' initial state from ({ex.Message})"); }
        if (batch.IsLegacyLoad && !project.LegacyLoadAllowedAt(k.Clock.UtcNow)) summary.Blockers.Add("MIG-E012: The legacy-load policy has expired or been closed.");
        if (l.Version is not null && l.Version.Status == ProfileVersionStatus.Published && l.Definition.Fields.Any(f => MappingCatalogue.IsCustomTarget(f.TargetField)))
        {
            // Every additional field the profile loads must exist, be in force and be one a migration can fill, before any row is looked at.
            var checker = await contracts.CustomFieldCheckerAsync(batch.OrganisationId, ct);
            foreach (var f in l.Definition.Fields.Where(f => MappingCatalogue.IsCustomTarget(f.TargetField)))
            {
                var key = MappingCatalogue.CustomKeyOf(f.TargetField);
                if (checker.Rule(key) is not { } rule) summary.Blockers.Add($"MIG-E020: {f.TargetField} is not an additional field of this organisation (release {checker.Release} of its field definitions).");
                else if (rule.IsRetired || !rule.IsCurrent) summary.Blockers.Add($"MIG-E021: {rule.Label} ({f.TargetField}) is retired or not in force, so a migration cannot load into it.");
                else if (!rule.Supported) summary.Blockers.Add($"MIG-E025: {rule.Label} ({f.TargetField}) cannot be loaded: {rule.UnsupportedReason}.");
            }
        }
        if (l.ContractsFile is not null && l.Version is not null)
        {
            var headers = HeadersOf(l.ContractsFile);
            foreach (var m in l.Definition.Fields.Where(f => !headers.Contains(f.SourceColumn, StringComparer.Ordinal) && !MappingDefinitionValidator.Chain(f.Transformation).Contains("Concat")))
                if (m.Required || MappingCatalogue.RequiredTargets.Contains(m.TargetField)) summary.Blockers.Add($"MIG-E014: The column '{m.SourceColumn}' (for {m.TargetField}) is missing from the file.");
        }
        if (summary.Blockers.Count > 0)
        {
            await k.Uow.ExecuteAsync(async c =>
            {
                await k.Db.EnlistAsync(c);
                k.Record("migration.dryrun.blocked", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId, new { Blockers = summary.Blockers });
                await Task.CompletedTask;
            }, ct);
            return summary;
        }

        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Feed(string text) => hash.AppendData(Encoding.UTF8.GetBytes(text + "\n"));
        Feed("v1");
        Feed(l.Version!.DefinitionHash);
        foreach (var s in new[] { l.ContractsFile, l.ManifestFile, l.ArchiveFile }.Where(s => s is not null)) Feed($"{s!.Role}:{s.Sha256}");
        Feed($"{batch.Kind}|{batch.DuplicateStrategy}|{batch.DocumentDuplicateOption}|{batch.IsLegacyLoad}|{batch.AllowRegisteredDateImport}");
        Feed($"{project.ReferencePolicy}|{project.LegacyAllowedStatusCodes}|{project.LegacyAllowOverwriteLocked}|{project.LegacyPolicyExpiresUtc:O}|{project.SourceSystem}");

        var mctx = BatchRuntime.MappingContextFor(l);
        var stats = new Dictionary<(string, string), int[]>();
        var sampleIds = new List<(Guid RowId, int RowNumber, List<MappingTrace> Trace)>();
        var sourceIds = new List<string>(); var references = new List<string>();
        var errors = 0; var warnings = 0;
        var maxSeq = await k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId).MaxAsync(r => (int?)r.Sequence, ct) ?? 0;
        summary.Rows = maxSeq;

        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.EnlistAsync(c);
            await k.Db.Messages.Where(m => m.BatchId == batchId && m.Phase == MessagePhase.Validation).ExecuteDeleteAsync(c);

            // ---- pass A: map every row, keep the mapped values, collect the keys to look up ---------------------------------------------------------------
            for (var from = 1; from <= maxSeq; from += PageSize)
            {
                var page = await k.Db.StagingRows.Where(r => r.BatchId == batchId && r.Sequence >= from && r.Sequence < from + PageSize).OrderBy(r => r.Sequence).ToListAsync(c);
                var messages = new List<StagingMessage>();
                foreach (var row in page)
                {
                    var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(row.RawValuesJson) ?? [];
                    if (raw.ContainsKey("__ragged"))
                    {
                        row.RecordValidation(null, null, null, null, RowOutcome.Rejected, PlannedAction.None, null, 1, 0, "Ragged row.", false);
                        continue; // the intake already recorded MIG-E013 for this row
                    }
                    var mapped = MappingEngine.MapRow(l.Definition, raw, mctx);
                    Tally(stats, l.Definition, mapped);
                    if (sampleIds.Count < 8) sampleIds.Add((row.Id, row.RowNumber, mapped.Trace));
                    var e = mapped.Messages.Count(m => m.IsError); var w = mapped.Messages.Count - e;
                    foreach (var m in mapped.Messages) messages.Add(k.Message(batchId, MessagePhase.Validation, MessageSubject.Row, row.Id, $"row {row.RowNumber}", m.IsError, m.Code, m.Text, m.Field));
                    var values = mapped.Values.Count == 0 ? null : JsonSerializer.Serialize(mapped.Values);
                    mapped.Values.TryGetValue("sourceRecordId", out var sid); mapped.Values.TryGetValue("agreementReference", out var aref);
                    row.RecordValidation(values, sid, aref, null, e > 0 ? RowOutcome.Rejected : RowOutcome.Pending, PlannedAction.None, null, e, w, e > 0 ? "Values could not be mapped." : null, e > 0 ? false : null);
                    if (e == 0 && sid is not null) sourceIds.Add(sid);
                    if (e == 0 && aref is not null) references.Add(aref);
                }
                k.Db.Messages.AddRange(messages);
                await k.Db.SaveChangesAsync(c);
                k.Db.ChangeTracker.Clear();
            }

            // ---- reference data and the records already in the target --------------------------------------------------------------------------------------
            var batchTracked = await k.Db.Batches.AsNoTracking().FirstAsync(b => b.Id == batchId, c);
            var ctx = await runtime.BuildContextAsync(l with { Batch = batchTracked }, c);
            await runtime.LoadExistingAsync(ctx, sourceIds.Distinct().ToList(), references.Distinct().ToList(), c);

            // ---- pass B: plan each row --------------------------------------------------------------------------------------------------------------------
            var ineligible = new HashSet<Guid>(); var rejectedRows = new HashSet<Guid>();
            var newSuppliers = new HashSet<WorkingSupplier>(); var newCategories = new HashSet<WorkingCategory>(); var linkedSuppliers = new HashSet<WorkingSupplier>(); var linkedCategories = new HashSet<WorkingCategory>();
            for (var from = 1; from <= maxSeq; from += PageSize)
            {
                var page = await k.Db.StagingRows.Where(r => r.BatchId == batchId && r.Sequence >= from && r.Sequence < from + PageSize).OrderBy(r => r.Sequence).ToListAsync(c);
                var messages = new List<StagingMessage>();
                foreach (var row in page)
                {
                    RowPlan? plan = null;
                    if (row.Outcome == RowOutcome.Rejected) { rejectedRows.Add(row.Id); Feed($"{row.Sequence}|Rejected"); continue; }
                    var values = row.MappedValuesJson is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(row.MappedValuesJson) ?? [];
                    var mapped = new MappedRow(); foreach (var (key, value) in values) mapped.Values[key] = value;
                    plan = RowPlanner.Plan(mapped, row, ctx);
                    var e = plan.Messages.Count(m => m.IsError); var w = plan.Messages.Count - e;
                    foreach (var m in plan.Messages) messages.Add(k.Message(batchId, MessagePhase.Validation, MessageSubject.Row, row.Id, $"row {row.RowNumber}", m.IsError, m.Code, m.Text, m.Field));
                    if (plan.Messages.Any(m => m.Code == "MIG-E005")) ineligible.Add(row.Id);
                    row.RecordValidation(row.MappedValuesJson, plan.Values?.SourceRecordId ?? row.SourceRecordId, plan.Values?.SourceReference ?? row.AgreementReference, plan.IdempotencyKey, plan.Outcome, plan.Action,
                        plan.ExistingContractId, e, row.WarningCount + w, plan.Summary, plan.ReviewApplicable);
                    if (plan.Outcome == RowOutcome.Rejected) rejectedRows.Add(row.Id);
                    Feed($"{row.Sequence}|{plan.Outcome}|{plan.Action}|{plan.ExistingContractId}|{row.ReviewDecision}|{plan.TargetReference}");
                    if (plan.Outcome == RowOutcome.Accepted)
                    {
                        if (plan.Supplier is { } s) (s.IsNew ? newSuppliers : linkedSuppliers).Add(s);
                        if (plan.Category is { } cat) (cat.IsNew ? newCategories : linkedCategories).Add(cat);
                        if (plan.Values is { } v)
                        {
                            Increment(summary.StatusDistribution, v.Status);
                            Increment(summary.CategoryDistribution, plan.Category?.Code ?? "?");
                            if (v.Currency is not null && v.Value is { } amount) summary.ValueByCurrency[v.Currency] = summary.ValueByCurrency.GetValueOrDefault(v.Currency) + amount;
                        }
                    }
                }
                k.Db.Messages.AddRange(messages);
                await k.Db.SaveChangesAsync(c);
                k.Db.ChangeTracker.Clear();
            }
            summary.SuppliersToCreate = newSuppliers.Count; summary.SuppliersToLink = linkedSuppliers.Count;
            summary.CategoriesToCreate = newCategories.Count; summary.CategoriesToLink = linkedCategories.Count;

            // ---- attachments: pair, match, check duplicates -----------------------------------------------------------------------------------------------
            await MatchAttachmentsAsync(batchId, l, ineligible, rejectedRows, summary, Feed, c);

            // ---- totals ----------------------------------------------------------------------------------------------------------------------------------
            var rowStats = await k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId).GroupBy(r => r.Outcome).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, c);
            summary.Accepted = rowStats.GetValueOrDefault(RowOutcome.Accepted); summary.Rejected = rowStats.GetValueOrDefault(RowOutcome.Rejected);
            summary.Skipped = rowStats.GetValueOrDefault(RowOutcome.Skipped); summary.ReviewRequired = await k.Db.StagingRows.AsNoTracking().CountAsync(r => r.BatchId == batchId && r.ReviewDecision == ReviewDecision.Required, c);
            summary.ToCreate = await k.Db.StagingRows.AsNoTracking().CountAsync(r => r.BatchId == batchId && r.Outcome == RowOutcome.Accepted && r.PlannedAction == PlannedAction.Create, c);
            summary.ToUpdate = await k.Db.StagingRows.AsNoTracking().CountAsync(r => r.BatchId == batchId && r.Outcome == RowOutcome.Accepted && r.PlannedAction == PlannedAction.Update, c);
            var byCode = await k.Db.Messages.AsNoTracking().Where(m => m.BatchId == batchId).GroupBy(m => new { m.Code, m.Severity }).Select(g => new { g.Key.Code, g.Key.Severity, N = g.Count() }).ToListAsync(c);
            foreach (var g in byCode) { summary.MessagesByCode[g.Code] = summary.MessagesByCode.GetValueOrDefault(g.Code) + g.N; if (g.Severity == MessageSeverity.Error) errors += g.N; else warnings += g.N; }
            summary.Errors = errors; summary.Warnings = warnings;
            summary.Mapping = StatsList(stats, l.Definition);
            foreach (var (rowId, rowNumber, trace) in sampleIds)
            {
                var row = await k.Db.StagingRows.AsNoTracking().FirstAsync(r => r.Id == rowId, c);
                summary.Sample.Add(new PreviewRow(rowNumber, row.Outcome.ToString(), row.PlanSummary ?? "", trace.Select(t => new PreviewValue(t.SourceColumn, t.TargetField, t.Original, t.Transformed, t.Changed, t.Failed)).ToList()));
            }
            Feed($"sum|{summary.Accepted}|{summary.Rejected}|{summary.Skipped}|{summary.DocumentsToImport}");

            var fingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
            var current = await k.Db.Batches.FirstAsync(b => b.Id == batchId, c);
            current.MarkValidated(fingerprint, JsonSerializer.Serialize(summary, Json), k.Clock.UtcNow);
            await k.Db.SaveChangesAsync(c);
            k.Record("migration.dryrun.validated", nameof(MigrationBatch), batchId.ToString(), batch.OrganisationId, batchId,
                new { summary.Rows, summary.Accepted, summary.Rejected, summary.Skipped, summary.Errors, summary.Warnings, summary.Files, summary.FilesMatched, summary.FilesUnmatched, Fingerprint = fingerprint });
        }, ct);
        return summary;
    }

    // ---- attachments ------------------------------------------------------------------------------------------------------------------------------------

    private async Task MatchAttachmentsAsync(Guid batchId, BatchRuntime.Loaded l, HashSet<Guid> ineligible, HashSet<Guid> rejectedRows, ValidationSummary summary, Action<string> feed, CancellationToken ct)
    {
        var entries = await k.Db.ManifestEntries.Where(e => e.BatchId == batchId).OrderBy(e => e.Sequence).ToListAsync(ct);
        var files = await k.Db.StagedFiles.Where(f => f.BatchId == batchId).OrderBy(f => f.RelativePath).ToListAsync(ct);
        summary.ManifestRows = entries.Count; summary.Files = files.Count;
        if (entries.Count == 0 && files.Count == 0) return;

        var rows = await k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId).OrderBy(r => r.Sequence)
            .Select(r => new { r.Id, r.Sequence, r.SourceRecordId, r.AgreementReference, r.MappedValuesJson, r.Outcome, r.TargetContractId }).ToListAsync(ct);
        var matchRows = rows.Select(r =>
        {
            string? folder = null;
            if (r.MappedValuesJson is not null)
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(r.MappedValuesJson);
                values?.TryGetValue("staging.documentFolder", out folder);
            }
            return new MatchRow(r.Id, r.Sequence, r.SourceRecordId, r.AgreementReference is null ? null : Contract.NormaliseReference(r.AgreementReference), folder, !ineligible.Contains(r.Id), r.Outcome == RowOutcome.Rejected);
        }).ToList();
        var rowById = rows.ToDictionary(r => r.Id);

        System.Text.RegularExpressions.Regex? naming = null;
        if (!string.IsNullOrWhiteSpace(l.Definition.Options.NamingRule))
            naming = NamingRule.Compile(l.Definition.Options.NamingRule, out var problem);

        var messages = new List<StagingMessage>();
        void Say(MessageSubject subject, Guid id, string location, bool isError, string code, string text) => messages.Add(k.Message(batchId, MessagePhase.Validation, subject, id, location, isError, code, text));

        // Files already settled stay as they are. A manual match is kept while its contract row can still receive the file.
        var candidates = new List<StagedFile>();
        foreach (var f in files)
        {
            if (f.State is FileState.Invalid or FileState.Discarded or FileState.Imported or FileState.Failed) continue;
            if (f.MatchedBy == MatchMethod.Manual && f.ContractStagingRowId is { } target && f.ResolvedBy is not null)
            {
                if (rowById.TryGetValue(target, out var tr) && !ineligible.Contains(target) && tr.Outcome != RowOutcome.Rejected) { f.RecordMatch(FileState.Matched, QueueReason.None, f.ManifestEntryId, target, MatchMethod.Manual, f.DocumentSourceId, f.Note); continue; }
                f.RecordMatch(FileState.Unmatched, QueueReason.TargetRejected, f.ManifestEntryId, target, MatchMethod.Manual, f.DocumentSourceId, "The [[contract|lower]] chosen for this file was not loaded.");
                Say(MessageSubject.File, f.Id, f.RelativePath, true, "MIG-F015", "The [[contract|lower]] chosen for this file is rejected or missing, so the file is back in the queue.");
                continue;
            }
            candidates.Add(f);
        }

        // Entries that cannot be paired are reported; a declared hash that differs from the real content makes the file invalid.
        var entryByPath = entries.Where(e => e.RelativePath is not null).GroupBy(e => e.RelativePath!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var matchable = new List<StagedFile>();
        foreach (var f in candidates)
        {
            if (entryByPath.TryGetValue(f.RelativePath, out var named) && named.Count == 1)
            {
                var e = named[0];
                if (e.State == ManifestEntryState.Invalid)
                { f.MarkInvalid($"MIG-F019: manifest row {e.RowNumber} is invalid, so its file cannot be imported with reliable details."); Say(MessageSubject.File, f.Id, f.RelativePath, true, "MIG-F019", $"Manifest row {e.RowNumber} for this file is invalid. Correct the manifest and upload it again."); continue; }
                if (e.DeclaredSha256 is not null && !string.Equals(e.DeclaredSha256, f.Sha256, StringComparison.OrdinalIgnoreCase))
                { f.MarkInvalid("MIG-F017: the content hash differs from the one the manifest declares."); Say(MessageSubject.File, f.Id, f.RelativePath, true, "MIG-F017", $"The manifest declares SHA-256 {e.DeclaredSha256} but the file's content hashes to {f.Sha256}."); continue; }
            }
            matchable.Add(f);
        }

        var result = AttachmentMatcher.Match(matchable.Select(f => new MatchFile(f.Id, f.RelativePath)).ToList(),
            entries.Where(e => e.State != ManifestEntryState.Invalid).Select(e => new MatchEntry(e.Id, e.RelativePath, e.SourceRecordId, e.AgreementReference)).ToList(), matchRows, naming);
        var fileById = matchable.ToDictionary(f => f.Id);
        var oldProposals = await k.Db.Proposals.Where(p => p.BatchId == batchId && p.Method != MatchMethod.Manual).ToListAsync(ct);
        k.Db.Proposals.RemoveRange(oldProposals);

        foreach (var e in entries) e.MarkOrphan(); // re-paired below
        var entryById = entries.ToDictionary(e => e.Id);
        foreach (var m in result.Files)
        {
            var file = fileById[m.FileId];
            if (m.EntryId is { } eid) entryById[eid].Pair(file.Id);
            foreach (var msg in m.Messages) Say(MessageSubject.File, file.Id, file.RelativePath, msg.IsError, msg.Code, msg.Text);
            switch (m.Outcome)
            {
                case MatchOutcome.Matched:
                    file.RecordMatch(FileState.Matched, QueueReason.None, m.EntryId, m.RowId, m.Method, null, m.Candidates[0].Reason);
                    k.Db.Proposals.Add(MatchProposal.Create(k.TenantId, batchId, file.Id, m.RowId!.Value, m.Method!.Value, m.Candidates[0].Reason, 1, ProposalStatus.Accepted));
                    break;
                case MatchOutcome.TargetRejected:
                    file.RecordMatch(FileState.Unmatched, QueueReason.TargetRejected, m.EntryId, m.RowId, m.Method, null, m.Candidates[0].Reason);
                    k.Db.Proposals.Add(MatchProposal.Create(k.TenantId, batchId, file.Id, m.RowId!.Value, m.Method!.Value, m.Candidates[0].Reason, 1, ProposalStatus.Proposed));
                    break;
                case MatchOutcome.Ambiguous:
                    file.RecordMatch(FileState.Unmatched, QueueReason.Ambiguous, m.EntryId, null, null, null, "More than one [[contract|lower]] matches.");
                    foreach (var cand in m.Candidates) k.Db.Proposals.Add(MatchProposal.Create(k.TenantId, batchId, file.Id, cand.RowId, cand.Method, cand.Reason, m.Candidates.Count, ProposalStatus.Proposed));
                    break;
                default:
                    file.RecordMatch(FileState.Unmatched, QueueReason.NoMatch, m.EntryId, null, null, null, "No [[contract|lower]] matches.");
                    break;
            }
        }
        foreach (var (entryId, text) in result.OrphanEntries) { Say(MessageSubject.ManifestEntry, entryId, $"manifest row {entryById[entryId].RowNumber}", true, "MIG-F009", text); }
        summary.ManifestPaired = entries.Count(e => e.State == ManifestEntryState.Paired);
        summary.ManifestOrphans = entries.Count(e => e.State == ManifestEntryState.OrphanManifest);
        summary.ManifestInvalid = entries.Count(e => e.State == ManifestEntryState.Invalid);

        // ---- duplicates among the matched files -------------------------------------------------------------------------------------------------------
        var matched = files.Where(f => f.State == FileState.Matched && f.ContractStagingRowId is not null).ToList();
        var docFiles = new List<DocFile>();
        var sourceSystem = l.Project.SourceSystem;
        foreach (var f in matched)
        {
            var row = rowById[f.ContractStagingRowId!.Value];
            var entry = f.ManifestEntryId is { } id && entryById.TryGetValue(id, out var en) ? en : entries.FirstOrDefault(e => e.StagedFileId == f.Id);
            var documentSourceId = entry?.DocumentSourceId ?? $"{row.SourceRecordId ?? row.AgreementReference ?? "-"}/{f.RelativePath}";
            f.RecordMatch(FileState.Matched, QueueReason.None, entry?.Id, row.Id, f.MatchedBy, documentSourceId, f.Note);
            docFiles.Add(new DocFile(f.Id, f.RelativePath, f.Sha256!, row.Id, row.TargetContractId, documentSourceId, f.ReleasedFromHold));
        }
        // MIG-F008: two files of one contract with the same name are allowed, but a person should know.
        foreach (var group in matched.GroupBy(f => (f.ContractStagingRowId, Name: f.FileName.ToUpperInvariant())).Where(g => g.Count() > 1))
            foreach (var f in group.OrderBy(x => x.RelativePath, StringComparer.Ordinal).Skip(1))
                Say(MessageSubject.File, f.Id, f.RelativePath, false, "MIG-F008", $"Another file of this [[contract|lower]] is also named '{f.FileName}'.");
        var bySource = docFiles.Count == 0 ? [] : await documents.FindBySourceAsync(l.Batch.OrganisationId, sourceSystem, docFiles.Select(d => d.DocumentSourceId).ToList(), ct);
        var byHash = docFiles.Count == 0 ? [] : await documents.FindByHashAsync(docFiles.Select(d => d.Sha256).ToList(), ct);
        var decisions = DocumentPlanner.Plan(docFiles, l.Batch.DocumentDuplicateOption,
            bySource.Select(d => new ExistingDoc(d.Id, d.ContractId, d.DocumentSourceId, d.CurrentSha256)).ToList(), byHash.Select(d => new ExistingDoc(d.Id, d.ContractId, d.DocumentSourceId, d.CurrentSha256)).ToList());
        var fileMap = files.ToDictionary(f => f.Id);
        foreach (var d in decisions)
        {
            var f = fileMap[d.FileId];
            if (d.Message is not null) Say(MessageSubject.File, f.Id, f.RelativePath, d.IsError, d.MessageCode ?? "MIG-F007", d.Message);
            if (d.Action == DocAction.Hold) f.HoldForReview(d.Message ?? "Held for review.");
            feed($"doc|{f.RelativePath}|{d.Action}|{d.ExistingDocumentId}|{f.Sha256}|{f.ContractStagingRowId}");
        }
        summary.DocumentsToImport = decisions.Count(d => d.Action is DocAction.ImportNew or DocAction.AddVersionToExisting or DocAction.AddVersionToBatchDocument);
        foreach (var f in files.Where(f => f.State == FileState.Invalid)) feed($"inv|{f.RelativePath}");

        summary.FilesMatched = files.Count(f => f.State == FileState.Matched);
        summary.FilesUnmatched = files.Count(f => f.State == FileState.Unmatched);
        summary.FilesInvalid = files.Count(f => f.State is FileState.Invalid or FileState.Failed);
        summary.FilesDuplicate = decisions.Count(d => d.Action is DocAction.RejectDuplicate or DocAction.LinkExisting or DocAction.LinkBatchDocument or DocAction.AlreadyImported);
        k.Db.Messages.AddRange(messages);
        await k.Db.SaveChangesAsync(ct);
        k.Db.ChangeTracker.Clear();
    }

    // ---- statistics ----------------------------------------------------------------------------------------------------------------------------------

    private static void Tally(Dictionary<(string, string), int[]> stats, MappingDefinition definition, MappedRow mapped)
    {
        foreach (var t in mapped.Trace)
        {
            var key = (t.SourceColumn, t.TargetField);
            if (!stats.TryGetValue(key, out var s)) stats[key] = s = new int[4];
            s[0]++; if (t.Changed) s[1]++; if (t.DefaultApplied) s[2]++; if (t.Failed) s[3]++;
        }
    }

    private static List<MappingStat> StatsList(Dictionary<(string, string), int[]> stats, MappingDefinition definition)
        => definition.Fields.Select(f =>
        {
            stats.TryGetValue((f.SourceColumn, f.TargetField), out var s);
            s ??= new int[4];
            return new MappingStat(f.SourceColumn, f.TargetField, f.Transformation, s[0], s[1], s[2], s[3]);
        }).ToList();

    private static void Increment(Dictionary<string, int> map, string key) => map[key] = map.GetValueOrDefault(key) + 1;

    private static IReadOnlyList<string> HeadersOf(SourceFile file)
    {
        using var doc = JsonDocument.Parse(file.ProfileJson);
        return doc.RootElement.TryGetProperty("headers", out var h) ? h.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
    }
}
