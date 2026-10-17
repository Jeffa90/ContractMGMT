using System.Text.Json;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Formats;
using Ecp.Formats;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

public enum ExportFormat { Xlsx, Csv }

public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

public interface IMigrationReports
{
    /// <summary>The error report: every message with the row or file it concerns, the source values of rejected rows, and the state of every file. Spreadsheet text is neutralised so no cell can run as a formula.</summary>
    Task<ExportFile> ExportErrorsAsync(Guid batchId, ExportFormat format, CancellationToken ct = default);
    Task<ExportFile> ExportReconciliationAsync(Guid batchId, ExportFormat format, CancellationToken ct = default);
    Task<ExportFile> ExportProvenanceAsync(Guid batchId, ExportFormat format, CancellationToken ct = default);
    /// <summary>The latest saved reconciliation, or the live figures if none has been saved yet.</summary>
    Task<ReconciliationDto> GetReconciliationAsync(Guid batchId, CancellationToken ct = default);
    Task<PagedResult<ProvenanceDto>> ListProvenanceAsync(Guid batchId, int page = 1, int pageSize = 50, CancellationToken ct = default);
}

internal sealed class MigrationReportService(MigrationKernel k, MigrationReconciler reconciler, Ecp.Configuration.ITerminologyResolver terminology, Ecp.Configuration.IRegionalSettingsResolver regional) : IMigrationReports
{
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string Csv = "text/csv; charset=utf-8";

    public Task<ReconciliationDto> GetReconciliationAsync(Guid batchId, CancellationToken ct = default) => reconciler.LatestAsync(batchId, ct);

    public async Task<PagedResult<ProvenanceDto>> ListProvenanceAsync(Guid batchId, int page = 1, int pageSize = 50, CancellationToken ct = default)
    {
        await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var query = k.Db.Provenance.AsNoTracking().Where(p => p.BatchId == batchId);
        var total = await query.CountAsync(ct);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 200);
        var items = await query.OrderBy(p => p.TargetEntityType).ThenBy(p => p.CreatedUtc).ThenBy(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new ProvenanceDto(p.Id, p.TargetEntityType, p.TargetEntityId, p.Action, p.SourceSystem, p.SourceRecordId, p.Sha256, p.ReversalState, p.ReversalNote, p.CreatedUtc)).ToListAsync(ct);
        return new PagedResult<ProvenanceDto>(items, total, page, pageSize);
    }

    public async Task<ExportFile> ExportErrorsAsync(Guid batchId, ExportFormat format, CancellationToken ct = default)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var messages = await k.Db.Messages.AsNoTracking().Where(m => m.BatchId == batchId).OrderBy(m => m.Severity).ThenBy(m => m.Code).ThenBy(m => m.Location).ThenBy(m => m.Id).ToListAsync(ct);
        var rows = await k.Db.StagingRows.AsNoTracking().Where(r => r.BatchId == batchId).Select(r => new { r.Id, r.RowNumber, r.SourceRecordId, r.AgreementReference, r.RawValuesJson, r.Outcome, r.PlanSummary, r.Sequence }).ToListAsync(ct);
        var rowById = rows.ToDictionary(r => r.Id);
        var files = await k.Db.StagedFiles.AsNoTracking().Where(f => f.BatchId == batchId).OrderBy(f => f.RelativePath).ToListAsync(ct);

        var words = await terminology.GetAsync(project.OrganisationId, null, ct);   // the report speaks in the words of the project's organisation
        var messageRows = new List<IReadOnlyList<object?>> { new object?[] { "Severity", "Code", "Applies to", "Location", "Field", "Message", "SourceRecordId", "AgreementReference", "Phase" } };
        foreach (var m in messages)
        {
            string? sid = null, aref = null;
            if (m.Subject == MessageSubject.Row && m.SubjectId is { } id && rowById.TryGetValue(id, out var row)) { sid = row.SourceRecordId; aref = row.AgreementReference; }
            messageRows.Add(new object?[] { m.Severity.ToString(), m.Code, m.Subject.ToString(), m.Location, m.FieldName, words.Expand(m.Message), sid, aref, m.Phase.ToString() });
        }
        if (format == ExportFormat.Csv) return new ExportFile($"{Slug(project.Name)}-batch-errors.csv", Csv, CsvWriter.Write(messageRows));

        var summary = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Migration error report" }, new object?[] { "Project", project.Name }, new object?[] { "Source system", project.SourceSystem }, new object?[] { "Batch", batch.Id.ToString() },
            new object?[] { "Kind", batch.Kind.ToString() }, new object?[] { "Status", batch.Status.ToString() }, new object?[] { "Generated (UTC)", k.Clock.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }, new object?[] { "Generated (local time)", (await regional.FormatterAsync(project.OrganisationId, ct)).DateTime(k.Clock.UtcNow) },
            new object?[] { "Source rows", rows.Count }, new object?[] { "Rejected rows", rows.Count(r => r.Outcome == RowOutcome.Rejected) },
            new object?[] { "Errors", messages.Count(m => m.Severity == MessageSeverity.Error) }, new object?[] { "Warnings", messages.Count(m => m.Severity == MessageSeverity.Warning) }, new object?[] { },
            new object?[] { "Code", "Severity", "Count" }
        };
        foreach (var g in messages.GroupBy(m => (m.Code, m.Severity)).OrderBy(g => g.Key.Severity).ThenBy(g => g.Key.Code)) summary.Add(new object?[] { g.Key.Code, g.Key.Severity.ToString(), g.Count() });

        var headers = rows.SelectMany(r => (JsonSerializer.Deserialize<Dictionary<string, string>>(r.RawValuesJson) ?? []).Keys).Where(h => !h.StartsWith("__", StringComparison.Ordinal)).Distinct().ToList();
        var rejected = new List<IReadOnlyList<object?>> { new object?[] { "Row", "Outcome", "What happened" }.Concat(headers).ToArray() };
        foreach (var r in rows.Where(r => r.Outcome == RowOutcome.Rejected).OrderBy(r => r.Sequence))
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(r.RawValuesJson) ?? [];
            rejected.Add(new object?[] { r.RowNumber, r.Outcome.ToString(), words.Expand(r.PlanSummary ?? "") }.Concat(headers.Select(h => (object?)raw.GetValueOrDefault(h))).ToArray());
        }
        var fileSheet = new List<IReadOnlyList<object?>> { new object?[] { "Path", "State", "Queue reason", "Matched by", "Scan", "SHA-256", "Size (bytes)", "Note", "Resolved by" } };
        foreach (var f in files) fileSheet.Add(new object?[] { f.RelativePath, f.State.ToString(), f.QueueReason.ToString(), f.MatchedBy?.ToString(), f.Scan.ToString(), f.Sha256, f.SizeBytes, words.Expand(f.Note ?? ""), f.ResolvedBy });

        var bytes = XlsxWriter.Write([new("Summary", summary, HeaderRow: false), new("Messages", messageRows), new("Rejected rows", rejected), new("Files", fileSheet)]);
        return new ExportFile($"{Slug(project.Name)}-batch-errors.xlsx", Xlsx, bytes);
    }

    public async Task<ExportFile> ExportReconciliationAsync(Guid batchId, ExportFormat format, CancellationToken ct = default)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var r = await reconciler.LatestAsync(batchId, ct);
        var words = await terminology.GetAsync(project.OrganisationId, null, ct);
        var totals = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Measure", "Value" },
            new object?[] { "Project", project.Name }, new object?[] { "Batch", batch.Id.ToString() }, new object?[] { "Status", batch.Status.ToString() }, new object?[] { "Report sequence", r.Sequence },
            new object?[] { "Generated (UTC)", r.GeneratedUtc == DateTime.MinValue ? "(live)" : r.GeneratedUtc.ToString("yyyy-MM-dd HH:mm:ss") },
            new object?[] { "Generated (local time)", r.GeneratedUtc == DateTime.MinValue ? "(live)" : (await regional.FormatterAsync(project.OrganisationId, ct)).DateTime(DateTime.SpecifyKind(r.GeneratedUtc, DateTimeKind.Utc)) }, new object?[] { "Balanced", r.Balanced ? "Yes" : "No" },
            new object?[] { "Exceptions approved", r.ExceptionsApproved ? "Yes" : "No" }, new object?[] { "Source rows", r.Totals.SourceRows }, new object?[] { "Accepted", r.Totals.Accepted },
            new object?[] { "Rejected", r.Totals.Rejected }, new object?[] { "Skipped", r.Totals.Skipped }, new object?[] { "Created", r.Totals.Created }, new object?[] { "Updated", r.Totals.Updated },
            new object?[] { "Manifest rows", r.Files.ManifestRows }, new object?[] { "Manifest matched", r.Files.ManifestMatched }, new object?[] { "Manifest orphan", r.Files.ManifestOrphans }, new object?[] { "Manifest invalid", r.Files.ManifestInvalid },
            new object?[] { "Files in archive", r.Files.FilesInArchive }, new object?[] { "Files imported", r.Files.Imported }, new object?[] { "Files failed", r.Files.Failed }, new object?[] { "Files invalid", r.Files.Invalid },
            new object?[] { "Files duplicate-handled", r.Files.DuplicateHandled }, new object?[] { "Files unmatched", r.Files.Unmatched }, new object?[] { "Files discarded", r.Files.Discarded },
            new object?[] { "Documents created", r.Files.DocumentsCreated }, new object?[] { "Document versions added", r.Files.DocumentVersionsAdded }
        };
        if (format == ExportFormat.Csv) return new ExportFile($"{Slug(project.Name)}-reconciliation.csv", Csv, CsvWriter.Write(totals));

        var checks = new List<IReadOnlyList<object?>> { new object?[] { "Kind", "Detail" } };
        foreach (var i in r.Imbalances) checks.Add(new object?[] { "Does not balance", i });
        foreach (var o in r.OutstandingItems) checks.Add(new object?[] { "Outstanding", o });
        if (r.Imbalances.Count == 0) checks.Add(new object?[] { "Balanced", "Every identity holds." });
        if (r.ApprovedBy is not null) checks.Add(new object?[] { "Exceptions approved by", $"{r.ApprovedBy}: {r.ApprovalNote}" });
        var dist = new List<IReadOnlyList<object?>> { new object?[] { "Measure", "Key", "Source", "Target" } };
        void Add(string measure, IReadOnlyDictionary<string, long> source, IReadOnlyDictionary<string, long> target)
        { foreach (var key in source.Keys.Union(target.Keys).OrderBy(x => x, StringComparer.Ordinal)) dist.Add(new object?[] { measure, key, source.GetValueOrDefault(key), target.GetValueOrDefault(key) }); }
        Add("Status", r.Distributions.SourceStatus, r.Distributions.TargetStatus);
        Add(words.Expand("[[category]]"), r.Distributions.SourceCategory, r.Distributions.TargetCategory);
        foreach (var cur in r.Distributions.SourceValueByCurrency.Keys.Union(r.Distributions.TargetValueByCurrency.Keys).OrderBy(x => x, StringComparer.Ordinal))
            dist.Add(new object?[] { words.Expand("[[field.estimated.value]]"), cur, r.Distributions.SourceValueByCurrency.GetValueOrDefault(cur), r.Distributions.TargetValueByCurrency.GetValueOrDefault(cur) });
        return new ExportFile($"{Slug(project.Name)}-reconciliation.xlsx", Xlsx, XlsxWriter.Write([new("Totals", totals), new("Checks", checks), new("Distributions", dist)]));
    }

    public async Task<ExportFile> ExportProvenanceAsync(Guid batchId, ExportFormat format, CancellationToken ct = default)
    {
        var (_, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: false, ct);
        var list = await k.Db.Provenance.AsNoTracking().Where(p => p.BatchId == batchId).OrderBy(p => p.TargetEntityType).ThenBy(p => p.CreatedUtc).ThenBy(p => p.Id).ToListAsync(ct);
        var rows = new List<IReadOnlyList<object?>> { new object?[] { "Entity", "Target id", "Action", "Source system", "Source record id", "SHA-256", "Source status", "Source extracted (UTC)", "Source file SHA-256", "Profile version", "Created (UTC)", "Reversal", "Reversal note" } };
        foreach (var p in list)
            rows.Add(new object?[] { p.TargetEntityType, p.TargetEntityId.ToString(), p.Action.ToString(), p.SourceSystem, p.SourceRecordId, p.Sha256, p.SourceStatus, p.SourceExtractedUtc?.ToString("yyyy-MM-dd HH:mm:ss"),
                p.SourceFileSha256, p.MappingProfileVersionId?.ToString(), p.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss"), p.ReversalState.ToString(), p.ReversalNote });
        return format == ExportFormat.Csv ? new ExportFile($"{Slug(project.Name)}-provenance.csv", Csv, CsvWriter.Write(rows)) : new ExportFile($"{Slug(project.Name)}-provenance.xlsx", Xlsx, XlsxWriter.Write([new("Provenance", rows)]));
    }

    private static string Slug(string name)
    {
        var s = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Length == 0 ? "migration" : s.Length > 60 ? s[..60] : s;
    }
}
