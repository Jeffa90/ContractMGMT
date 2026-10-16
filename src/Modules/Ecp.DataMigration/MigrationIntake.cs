using System.Text.Json;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Formats;
using Ecp.Formats;
using Ecp.DataMigration.Mapping;
using Ecp.DataMigration.Planning;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Ecp.DataMigration;

public interface IMigrationIntake
{
    /// <summary>Registers the contracts file (CSV or XLSX), reads it into staging with the source values retained, and profiles it. Uploading the same file again changes nothing; a different file replaces the earlier one.</summary>
    Task<SourceFileDto> UploadContractsAsync(Guid batchId, Stream content, string fileName, UploadOptions? options = null, CancellationToken ct = default);
    Task<SourceFileDto> UploadManifestAsync(Guid batchId, Stream content, string fileName, UploadOptions? options = null, CancellationToken ct = default);
    /// <summary>Registers the attachment archive (ZIP). Entries are checked and held in restricted staging; nothing is extracted to disk or imported.</summary>
    Task<SourceFileDto> UploadArchiveAsync(Guid batchId, Stream content, string fileName, CancellationToken ct = default);
}

internal sealed class MigrationIntakeService(MigrationKernel k, IDocumentStorageProvider storage, IMalwareScanner scanner) : IMigrationIntake
{
    private const string RowFlagRagged = "__ragged";
    private const string RowFlagHidden = "__hidden";
    private const long MaxEmbeddedManifestBytes = 50L * 1024 * 1024;

    public async Task<SourceFileDto> UploadContractsAsync(Guid batchId, Stream content, string fileName, UploadOptions? options = null, CancellationToken ct = default)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        EnsureEditable(batch);
        var format = TabularFormat(fileName);
        var stored = await StoreAsync(content, ct);
        var existing = await k.Db.SourceFiles.AsNoTracking().FirstOrDefaultAsync(s => s.BatchId == batchId && s.Role == SourceFileRole.Contracts, ct);
        if (existing is not null && existing.Sha256 == stored.Sha256) return await ToDtoAsync(existing, ct); // the same file again: nothing changes

        var table = await ReadTableAsync(stored.StorageKey, format, options, ct);
        var by = await k.ActorLabelAsync(ct);
        var now = k.Clock.UtcNow;
        var source = SourceFile.Create(k.TenantId, batchId, batch.OrganisationId, SourceFileRole.Contracts, format, Safe(fileName), stored.Sha256, stored.SizeBytes, stored.StorageKey,
            table.Count, table.SheetName, table.Encoding, table.Delimiter, ProfileOf(table), null, by, now);

        var rows = new List<StagingRow>(table.Count); var messages = new List<StagingMessage>();
        var sequence = 0;
        foreach (var row in table.Rows)
        {
            sequence++;
            var raw = table.ToMap(row);
            if (row.Ragged) raw[RowFlagRagged] = "true";
            if (row.Hidden) raw[RowFlagHidden] = "true";
            var json = JsonSerializer.Serialize(raw);
            var staged = StagingRow.Create(k.TenantId, batchId, batch.OrganisationId, source.Id, sequence, row.RowNumber, json, Hashing.Sha256Hex(json));
            rows.Add(staged);
            if (row.Ragged)
                messages.Add(k.Message(batchId, MessagePhase.Intake, MessageSubject.Row, staged.Id, $"row {row.RowNumber}", true, "MIG-E013",
                    $"Row {row.RowNumber} has {row.Fields.Count} values but the header has {table.Headers.Count}. A ragged row is an error; it is not padded."));
            if (row.Hidden)
                messages.Add(k.Message(batchId, MessagePhase.Intake, MessageSubject.Row, staged.Id, $"row {row.RowNumber}", false, "MIG-W007", $"Row {row.RowNumber} is hidden in the workbook. It was read and is flagged."));
        }
        foreach (var notice in table.Notices) messages.Add(k.Message(batchId, MessagePhase.Intake, MessageSubject.Batch, null, source.FileName, false, "MIG-W008", notice));

        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.EnlistAsync(c);
            await ClearContractsAsync(batchId, c);
            batch.InputsChanged(hasSources: true);
            k.Db.SourceFiles.Add(source);
            await k.Db.SaveChangesAsync(c);
            await AddInChunksAsync(rows, messages, c);
            k.Record("migration.source.uploaded", nameof(SourceFile), source.Id.ToString(), batch.OrganisationId, batchId, new { Role = "Contracts", source.FileName, source.Sha256, source.RowCount });
            await k.Db.SaveChangesAsync(c);
        }, ct);
        return await ToDtoAsync(source, ct, table.Headers, table.Notices);
    }

    public async Task<SourceFileDto> UploadManifestAsync(Guid batchId, Stream content, string fileName, UploadOptions? options = null, CancellationToken ct = default)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        EnsureEditable(batch);
        var format = TabularFormat(fileName);
        var stored = await StoreAsync(content, ct);
        return await IngestManifestAsync(batch, project, stored, format, fileName, options, "Uploaded", ct);
    }

    private async Task<SourceFileDto> IngestManifestAsync(MigrationBatch batch, MigrationProject project, StoredObject stored, SourceFormat format, string fileName, UploadOptions? options, string note, CancellationToken ct)
    {
        var existing = await k.Db.SourceFiles.AsNoTracking().FirstOrDefaultAsync(s => s.BatchId == batch.Id && s.Role == SourceFileRole.Manifest, ct);
        if (existing is not null && existing.Sha256 == stored.Sha256) return await ToDtoAsync(existing, ct);

        var table = await ReadTableAsync(stored.StorageKey, format, options, ct);
        var definition = await ProfileDefinitionAsync(batch, ct);
        IReadOnlyList<ParsedManifestEntry> parsed; IReadOnlyList<RowMessage> fileMessages;
        try { parsed = ManifestParser.Parse(table, definition, project.SourceSystem, out fileMessages); }
        catch (SourceFormatException ex) { throw new DomainException("source_unreadable", $"{ex.Code}: {ex.Message}"); }

        var by = await k.ActorLabelAsync(ct);
        var source = SourceFile.Create(k.TenantId, batch.Id, batch.OrganisationId, SourceFileRole.Manifest, format, Safe(fileName), stored.Sha256, stored.SizeBytes, stored.StorageKey,
            table.Count, table.SheetName, table.Encoding, table.Delimiter, ProfileOf(table), note, by, k.Clock.UtcNow);
        var entries = new List<AttachmentManifestEntry>(); var messages = new List<StagingMessage>();
        foreach (var p in parsed)
        {
            var entry = AttachmentManifestEntry.Create(k.TenantId, batch.Id, source.Id, p.Sequence, p.RowNumber, p.ColumnsJson, p.SourceRecordId, p.AgreementReference, p.RawPath, p.RelativePath,
                p.FileName, p.TypeRaw, p.TypeCode, p.DocumentDate, p.VersionLabel, p.IsExecutedCopy, p.ParentDocumentSourceId, p.DeclaredSha256, p.DocumentSourceId, p.State);
            entries.Add(entry);
            foreach (var m in p.Messages)
                messages.Add(k.Message(batch.Id, MessagePhase.Intake, MessageSubject.ManifestEntry, entry.Id, $"manifest row {p.RowNumber}", m.IsError, m.Code, m.Text, m.Field));
        }
        foreach (var m in fileMessages) messages.Add(k.Message(batch.Id, MessagePhase.Intake, MessageSubject.Batch, null, source.FileName, m.IsError, m.Code, m.Text, m.Field));
        foreach (var notice in table.Notices) messages.Add(k.Message(batch.Id, MessagePhase.Intake, MessageSubject.Batch, null, source.FileName, false, "MIG-W008", notice));

        await k.Uow.ExecuteAsync(async c =>
        {
            await k.Db.EnlistAsync(c);
            await ClearManifestAsync(batch.Id, c);
            batch.InputsChanged(hasSources: true);
            k.Db.SourceFiles.Add(source);
            await k.Db.SaveChangesAsync(c);
            foreach (var chunk in entries.Chunk(1000)) { k.Db.ManifestEntries.AddRange(chunk); await k.Db.SaveChangesAsync(c); }
            foreach (var chunk in messages.Chunk(1000)) { k.Db.Messages.AddRange(chunk); await k.Db.SaveChangesAsync(c); }
            k.Record("migration.source.uploaded", nameof(SourceFile), source.Id.ToString(), batch.OrganisationId, batch.Id, new { Role = "Manifest", source.FileName, source.Sha256, source.RowCount, Origin = note });
            await k.Db.SaveChangesAsync(c);
        }, ct);
        return await ToDtoAsync(source, ct, table.Headers, table.Notices);
    }

    public async Task<SourceFileDto> UploadArchiveAsync(Guid batchId, Stream content, string fileName, CancellationToken ct = default)
    {
        var (batch, project) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
        EnsureEditable(batch);
        if (!Safe(fileName).EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new DomainException("unsupported_format", "The attachment archive must be a .zip file.");
        var stored = await StoreAsync(content, ct);
        var existing = await k.Db.SourceFiles.AsNoTracking().FirstOrDefaultAsync(s => s.BatchId == batchId && s.Role == SourceFileRole.Archive, ct);
        if (existing is not null && existing.Sha256 == stored.Sha256) return await ToDtoAsync(existing, ct);

        var by = await k.ActorLabelAsync(ct);
        var now = k.Clock.UtcNow;
        var limits = k.Options.ArchiveLimits;
        var files = new List<StagedFile>(); var messages = new List<StagingMessage>();
        byte[]? embeddedManifest = null; string? embeddedManifestName = null;

        ArchiveListing listing;
        await using (var zipStream = await storage.OpenReadAsync(k.TenantId, stored.StorageKey, ct))
        {
            try { listing = ZipIntake.Open(zipStream, limits); }
            catch (SourceFormatException ex) { throw new DomainException("archive_refused", $"{ex.Code}: {ex.Message}"); }
            using (listing)
            {
                var source = SourceFile.Create(k.TenantId, batchId, batch.OrganisationId, SourceFileRole.Archive, SourceFormat.Zip, Safe(fileName), stored.Sha256, stored.SizeBytes, stored.StorageKey,
                    listing.Entries.Count, null, null, null, JsonSerializer.Serialize(new { entries = listing.Entries.Count, folders = listing.Directories }), null, by, now);
                foreach (var entry in listing.Entries)
                {
                    // A manifest at the root of the archive is the manifest, not an attachment.
                    if (entry.Path is not null && !entry.IsRejected && !entry.Path.Contains('/') && IsManifestName(entry.Path))
                    {
                        embeddedManifestName = entry.Path;
                        embeddedManifest = await ReadAllAsync(entry, MaxEmbeddedManifestBytes, ct);
                        continue;
                    }
                    files.Add(await StageEntryAsync(batch, source.Id, entry, messages, ct));
                }

                var hasManifest = await k.Db.SourceFiles.AsNoTracking().AnyAsync(s => s.BatchId == batchId && s.Role == SourceFileRole.Manifest, ct);
                await k.Uow.ExecuteAsync(async c =>
                {
                    await k.Db.EnlistAsync(c);
                    await ClearArchiveAsync(batchId, c);
                    batch.InputsChanged(hasSources: true);
                    k.Db.SourceFiles.Add(source);
                    await k.Db.SaveChangesAsync(c);
                    foreach (var chunk in files.Chunk(1000)) { k.Db.StagedFiles.AddRange(chunk); await k.Db.SaveChangesAsync(c); }
                    foreach (var chunk in messages.Chunk(1000)) { k.Db.Messages.AddRange(chunk); await k.Db.SaveChangesAsync(c); }
                    if (embeddedManifest is not null && hasManifest)
                    {
                        k.Db.Messages.Add(k.Message(batchId, MessagePhase.Intake, MessageSubject.Batch, null, embeddedManifestName!, false, "MIG-W006",
                            $"The archive contains '{embeddedManifestName}' but a manifest was already uploaded separately. The uploaded manifest is used."));
                    }
                    k.Record("migration.source.uploaded", nameof(SourceFile), source.Id.ToString(), batch.OrganisationId, batchId,
                        new { Role = "Archive", source.FileName, source.Sha256, Entries = listing.Entries.Count, Staged = files.Count(f => f.State == FileState.Staged), Invalid = files.Count(f => f.State == FileState.Invalid) });
                    await k.Db.SaveChangesAsync(c);
                }, ct);

                if (embeddedManifest is not null && !hasManifest)
                {
                    await using var ms = new MemoryStream(embeddedManifest);
                    var manifestStored = await StoreAsync(ms, ct);
                    var (b2, p2) = await k.BatchAsync(batchId, MigrationPermissions.Stage, track: true, ct);
                    await IngestManifestAsync(b2, p2, manifestStored, TabularFormat(embeddedManifestName!), embeddedManifestName!, null, "Read from the attachment archive", ct);
                }
                return await ToDtoAsync(source, ct);
            }
        }
    }

    // ---- staging an archive entry ------------------------------------------------------------------------------------------------------

    private async Task<StagedFile> StageEntryAsync(MigrationBatch batch, Guid archiveId, ArchiveEntry entry, List<StagingMessage> messages, CancellationToken ct)
    {
        var path = entry.Path ?? SafeDisplay(entry.RawName);
        StagedFile Make(FileState state, string? sha, long size, string? key, FileScan scan, string? note)
            => StagedFile.Create(k.TenantId, batch.Id, batch.OrganisationId, archiveId, path, entry.Path is null ? SafeDisplay(entry.FileName) : entry.FileName, string.IsNullOrEmpty(entry.Extension) ? null : entry.Extension,
                sha, size, key, entry.Extension.Length == 0 ? null : FileSignatures.MimeFor(entry.Extension), scan, state, note);
        void Say(StagedFile f, bool error, string code, string text) => messages.Add(k.Message(batch.Id, MessagePhase.Intake, MessageSubject.File, f.Id, path, error, code, text));

        if (entry.IsRejected)
        {
            var rejected = Make(FileState.Invalid, null, entry.Length, null, FileScan.Pending, $"{entry.RejectCode}: {entry.RejectMessage}");
            Say(rejected, true, entry.RejectCode!, entry.RejectMessage!);
            return rejected;
        }

        StoredObject? stored = null; byte[] head = [];
        try
        {
            await using var raw = entry.Open();
            await using var capture = new HeaderCaptureStream(raw, FileSignatures.HeaderBytes);
            stored = await storage.PutAsync(k.TenantId, capture, entry.Length > 0 ? Math.Min(entry.Length, k.Options.ArchiveLimits.MaxEntryBytes) : k.Options.ArchiveLimits.MaxEntryBytes, ct);
            head = capture.Header;
        }
        catch (SourceFormatException ex)
        {
            var failed = Make(FileState.Invalid, null, entry.Length, null, FileScan.Pending, $"{ex.Code}: {ex.Message}");
            Say(failed, true, ex.Code, ex.Message);
            return failed;
        }
        catch (DomainException ex) when (ex.Code is "empty_file")
        {
            var empty = Make(FileState.Invalid, null, 0, null, FileScan.Pending, "MIG-F004: The file is empty.");
            Say(empty, true, "MIG-F004", "The file is empty.");
            return empty;
        }
        catch (DomainException ex) when (ex.Code is "file_too_large")
        {
            var big = Make(FileState.Invalid, null, entry.Length, null, FileScan.Pending, "MIG-F006: " + ex.Message);
            Say(big, true, "MIG-F006", ex.Message);
            return big;
        }

        if (!FileSignatures.Matches(entry.Extension, head))
        {
            var mismatch = Make(FileState.Invalid, stored.Sha256, stored.SizeBytes, stored.StorageKey, FileScan.Pending, $"MIG-F003: The content does not look like a .{entry.Extension} file.");
            Say(mismatch, true, "MIG-F003", $"The content does not look like a .{entry.Extension} file, so it is not accepted.");
            return mismatch;
        }

        ScanVerdict verdict;
        try
        {
            await using var read = await storage.OpenReadAsync(k.TenantId, stored.StorageKey, ct);
            verdict = await scanner.ScanAsync(read, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { verdict = ScanVerdict.Error; }

        switch (verdict)
        {
            case ScanVerdict.Infected:
            {
                var infected = Make(FileState.Invalid, stored.Sha256, stored.SizeBytes, stored.StorageKey, FileScan.Infected, "MIG-F013: The malware scan reported an infection.");
                Say(infected, true, "MIG-F013", "The malware scan reported an infection. The file is held and will not be imported.");
                return infected;
            }
            case ScanVerdict.Clean:
                return Make(FileState.Staged, stored.Sha256, stored.SizeBytes, stored.StorageKey, FileScan.Clean, null);
            default:
            {
                var scan = verdict == ScanVerdict.Skipped ? FileScan.Skipped : FileScan.Error;
                if (k.Options.RequireMalwareScan)
                {
                    var held = Make(FileState.Failed, stored.Sha256, stored.SizeBytes, stored.StorageKey, scan, "MIG-F014: The malware scanner was unavailable.");
                    Say(held, true, "MIG-F014", "The malware scanner was unavailable, so the file is held. This environment does not import unscanned files.");
                    return held;
                }
                var f = Make(FileState.Staged, stored.Sha256, stored.SizeBytes, stored.StorageKey, scan, null);
                Say(f, false, "MIG-F014", "No malware scan was performed (no scanner is configured). The document will not be downloadable unless the tenant allows unscanned documents.");
                return f;
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------------

    private static bool IsManifestName(string name)
        => name.Equals("attachment-manifest.csv", StringComparison.OrdinalIgnoreCase) || name.Equals("attachment-manifest.xlsx", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadAllAsync(ArchiveEntry entry, long max, CancellationToken ct)
    {
        await using var s = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920]; int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max) throw new DomainException("archive_refused", "MIG-F006: The manifest inside the archive is too large.");
        }
        return ms.ToArray();
    }

    private static void EnsureEditable(MigrationBatch batch)
    {
        if (!batch.InputsEditable) throw new DomainException("batch_locked", $"A batch in state {batch.Status} can no longer take new source files.");
    }

    private static SourceFormat TabularFormat(string fileName)
    {
        var name = Safe(fileName);
        if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return SourceFormat.Csv;
        if (name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) return SourceFormat.Xlsx;
        if (name.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase)) throw new DomainException("unsupported_format", "MIG-F002: Macro-enabled workbooks (.xlsm) are not accepted. Save the workbook as .xlsx.");
        throw new DomainException("unsupported_format", "Upload a .csv or .xlsx file.");
    }

    private static string Safe(string? name) => Ecp.Documents.Domain.FileNames.Sanitise(name);
    private static string SafeDisplay(string? name) => Safe(name);

    private async Task<StoredObject> StoreAsync(Stream content, CancellationToken ct)
    {
        try { return await storage.PutAsync(k.TenantId, content, k.Options.MaxUploadBytes, ct); }
        catch (DomainException ex) when (ex.Code == "empty_file") { throw new DomainException("source_unreadable", "MIG-F004: The file is empty."); }
        catch (DomainException ex) when (ex.Code == "file_too_large") { throw new DomainException("source_unreadable", "MIG-F006: " + ex.Message); }
    }

    private async Task<SourceTable> ReadTableAsync(string storageKey, SourceFormat format, UploadOptions? options, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(k.TenantId, storageKey, ct);
        try
        {
            return format == SourceFormat.Xlsx
                ? XlsxReader.Read(stream, k.Options.ReadLimits, options?.SheetName)
                : CsvReader.Read(stream, k.Options.ReadLimits, options?.Delimiter, options?.Encoding);
        }
        catch (SourceFormatException ex) { throw new DomainException("source_unreadable", $"{ex.Code}: {ex.Message}"); }
    }

    private async Task<MappingDefinition?> ProfileDefinitionAsync(MigrationBatch batch, CancellationToken ct)
    {
        if (batch.MappingProfileVersionId is not { } id) return null;
        var json = await k.Db.ProfileVersions.AsNoTracking().Where(v => v.Id == id).Select(v => v.DefinitionJson).FirstOrDefaultAsync(ct);
        return json is null ? null : MappingDefinition.FromJson(json);
    }

    private static string ProfileOf(SourceTable table)
    {
        var columns = new List<object>();
        for (var c = 0; c < table.Headers.Count; c++)
        {
            var values = table.Rows.Select(r => c < r.Fields.Count ? r.Fields[c] : "").ToList();
            var filled = values.Where(v => v.Length > 0).ToList();
            columns.Add(new { name = table.Headers[c], filled = filled.Count, empty = values.Count - filled.Count, distinct = filled.Distinct().Take(1000).Count(), maxLength = filled.Count == 0 ? 0 : filled.Max(v => v.Length) });
        }
        return JsonSerializer.Serialize(new { headers = table.Headers, rows = table.Count, columns, notices = table.Notices });
    }

    private async Task<SourceFileDto> ToDtoAsync(SourceFile s, CancellationToken ct, IReadOnlyList<string>? headers = null, IReadOnlyList<string>? notices = null)
    {
        if (headers is null)
        {
            using var doc = JsonDocument.Parse(s.ProfileJson);
            headers = doc.RootElement.TryGetProperty("headers", out var h) ? h.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            notices = doc.RootElement.TryGetProperty("notices", out var n) ? n.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
        }
        await Task.CompletedTask;
        return new SourceFileDto(s.Id, s.Role, s.Format, s.FileName, s.Sha256, s.SizeBytes, s.RowCount, s.SheetName, s.Encoding, s.Delimiter, s.UploadedBy, s.UploadedUtc, headers, notices ?? [], s.Note);
    }

    private async Task AddInChunksAsync(List<StagingRow> rows, List<StagingMessage> messages, CancellationToken ct)
    {
        foreach (var chunk in rows.Chunk(1000)) { k.Db.StagingRows.AddRange(chunk); await k.Db.SaveChangesAsync(ct); k.Db.ChangeTracker.Clear(); } // the batch was saved first
        foreach (var chunk in messages.Chunk(1000)) { k.Db.Messages.AddRange(chunk); await k.Db.SaveChangesAsync(ct); k.Db.ChangeTracker.Clear(); }
    }

    /// <summary>Removes the contracts file and everything derived from it, and sends attachments back to the start of matching.</summary>
    private async Task ClearContractsAsync(Guid batchId, CancellationToken ct)
    {
        await ResetMatchingAsync(batchId, ct);
        await k.Db.Messages.Where(m => m.BatchId == batchId && (m.Subject == MessageSubject.Row || m.Phase == MessagePhase.Validation)).ExecuteDeleteAsync(ct);
        await k.Db.StagingRows.Where(r => r.BatchId == batchId).ExecuteDeleteAsync(ct);
        await k.Db.SourceFiles.Where(s => s.BatchId == batchId && s.Role == SourceFileRole.Contracts).ExecuteDeleteAsync(ct);
    }

    private async Task ClearManifestAsync(Guid batchId, CancellationToken ct)
    {
        await ResetMatchingAsync(batchId, ct);
        await k.Db.Messages.Where(m => m.BatchId == batchId && (m.Subject == MessageSubject.ManifestEntry || m.Phase == MessagePhase.Validation)).ExecuteDeleteAsync(ct);
        await k.Db.ManifestEntries.Where(e => e.BatchId == batchId).ExecuteDeleteAsync(ct);
        await k.Db.SourceFiles.Where(s => s.BatchId == batchId && s.Role == SourceFileRole.Manifest).ExecuteDeleteAsync(ct);
    }

    private async Task ClearArchiveAsync(Guid batchId, CancellationToken ct)
    {
        await k.Db.Proposals.Where(p => p.BatchId == batchId).ExecuteDeleteAsync(ct);
        await k.Db.Messages.Where(m => m.BatchId == batchId && (m.Subject == MessageSubject.File || m.Phase == MessagePhase.Validation)).ExecuteDeleteAsync(ct);
        await k.Db.ManifestEntries.Where(e => e.BatchId == batchId).ExecuteUpdateAsync(s => s.SetProperty(e => e.StagedFileId, (Guid?)null)
            .SetProperty(e => e.State, e => e.State == ManifestEntryState.Invalid ? ManifestEntryState.Invalid : ManifestEntryState.Pending), ct);
        await k.Db.StagedFiles.Where(f => f.BatchId == batchId).ExecuteDeleteAsync(ct);
        await k.Db.SourceFiles.Where(s => s.BatchId == batchId && s.Role == SourceFileRole.Archive).ExecuteDeleteAsync(ct);
    }

    private async Task ResetMatchingAsync(Guid batchId, CancellationToken ct)
    {
        await k.Db.Proposals.Where(p => p.BatchId == batchId).ExecuteDeleteAsync(ct);
        await k.Db.StagedFiles.Where(f => f.BatchId == batchId && f.State != FileState.Invalid && f.State != FileState.Discarded)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.State, FileState.Staged).SetProperty(f => f.QueueReason, QueueReason.None).SetProperty(f => f.ContractStagingRowId, (Guid?)null)
                .SetProperty(f => f.ManifestEntryId, (Guid?)null).SetProperty(f => f.MatchedBy, (MatchMethod?)null).SetProperty(f => f.ResolvedBy, (string?)null).SetProperty(f => f.ResolvedUtc, (DateTime?)null), ct);
        await k.Db.ManifestEntries.Where(e => e.BatchId == batchId).ExecuteUpdateAsync(s => s.SetProperty(e => e.StagedFileId, (Guid?)null)
            .SetProperty(e => e.State, e => e.State == ManifestEntryState.Invalid ? ManifestEntryState.Invalid : ManifestEntryState.Pending), ct);
    }
}

/// <summary>Passes content through while keeping its first bytes, so a content-signature check needs no second read.</summary>
internal sealed class HeaderCaptureStream(Stream inner, int capture) : Stream
{
    private readonly MemoryStream _head = new();
    public byte[] Header => _head.ToArray();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Take(buffer.AsSpan(offset), inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await inner.ReadAsync(buffer, ct); return Take(buffer.Span, n); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    private int Take(ReadOnlySpan<byte> data, int n)
    {
        var room = capture - (int)_head.Length;
        if (room > 0 && n > 0) _head.Write(data[..Math.Min(room, n)]);
        return n;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); _head.Dispose(); } base.Dispose(disposing); }
}
