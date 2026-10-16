using System.Text.RegularExpressions;
using Ecp.Contracts.Domain;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Formats;

namespace Ecp.DataMigration.Planning;

public sealed record MatchFile(Guid Id, string RelativePath);
public sealed record MatchEntry(Guid Id, string? RelativePath, string? SourceRecordId, string? AgreementReference);
/// <param name="Eligible">False for a later duplicate of a source record: it is never a match target.</param>
/// <param name="Rejected">The contract row was rejected, so a file matched to it is held rather than imported.</param>
public sealed record MatchRow(Guid Id, int Sequence, string? SourceRecordId, string? AgreementReference, string? DocumentFolder, bool Eligible, bool Rejected);

public sealed record Candidate(Guid RowId, MatchMethod Method, string Reason);

public enum MatchOutcome { Matched, NoMatch, Ambiguous, TargetRejected }

public sealed record FileMatch(Guid FileId, Guid? EntryId, MatchOutcome Outcome, MatchMethod? Method, Guid? RowId, IReadOnlyList<Candidate> Candidates, IReadOnlyList<(bool IsError, string Code, string Text)> Messages);

public sealed record MatchResult(IReadOnlyList<FileMatch> Files, IReadOnlyList<(Guid EntryId, string Text)> OrphanEntries);

/// <summary>
/// Deterministic attachment matching (ATTACHMENT_INGESTION.md, matching order): explicit SourceRecordId, exact AgreementReference, a configured naming rule, the exact parent
/// folder name, then the manual queue. The same inputs in any order give the same proposals; a match that is not unique is never resolved by the platform.
/// </summary>
public static class AttachmentMatcher
{
    public static MatchResult Match(IReadOnlyList<MatchFile> files, IReadOnlyList<MatchEntry> entries, IReadOnlyList<MatchRow> rows, Regex? namingRule)
    {
        var orderedRows = rows.Where(r => r.Eligible).OrderBy(r => r.Sequence).ToList();
        var bySource = orderedRows.Where(r => !string.IsNullOrWhiteSpace(r.SourceRecordId)).ToLookup(r => r.SourceRecordId!.Trim(), StringComparer.Ordinal);
        var byReference = orderedRows.Where(r => !string.IsNullOrWhiteSpace(r.AgreementReference)).ToLookup(r => Norm(r.AgreementReference!), StringComparer.Ordinal);
        var byFolder = orderedRows.Where(r => !string.IsNullOrWhiteSpace(r.DocumentFolder)).ToLookup(r => Norm(r.DocumentFolder!), StringComparer.Ordinal);
        var entriesByPath = entries.Where(e => e.RelativePath is not null).GroupBy(e => e.RelativePath!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderBy(e => e.Id).ToList(), StringComparer.OrdinalIgnoreCase);

        var results = new List<FileMatch>();
        var pairedEntries = new HashSet<Guid>();
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var messages = new List<(bool, string, string)>();
            MatchEntry? entry = null;
            if (entriesByPath.TryGetValue(file.RelativePath, out var named))
            {
                foreach (var e in named) pairedEntries.Add(e.Id);
                if (named.Count > 1)
                {
                    messages.Add((true, "MIG-F012", $"More than one manifest row names '{file.RelativePath}', so the file cannot be paired automatically."));
                    results.Add(new FileMatch(file.Id, null, MatchOutcome.Ambiguous, null, null, [], messages));
                    continue;
                }
                entry = named[0];
            }
            results.Add(MatchOne(file, entry, orderedRows, bySource, byReference, byFolder, namingRule, messages));
        }
        var orphans = entries.Where(e => !pairedEntries.Contains(e.Id) && e.RelativePath is not null)
            .Select(e => (e.Id, $"The manifest names '{e.RelativePath}' but no file with that path is in the archive.")).ToList();
        return new MatchResult(results, orphans);
    }

    private static FileMatch MatchOne(MatchFile file, MatchEntry? entry, List<MatchRow> rows, ILookup<string, MatchRow> bySource, ILookup<string, MatchRow> byReference,
        ILookup<string, MatchRow> byFolder, Regex? naming, List<(bool, string, string)> messages)
    {
        // Method 1 and 2: the manifest row's explicit keys. They must agree: a SourceRecordId that points at one contract and a reference that points at another is a conflict.
        if (entry is not null)
        {
            var viaSource = string.IsNullOrWhiteSpace(entry.SourceRecordId) ? [] : bySource[entry.SourceRecordId.Trim()].ToList();
            var viaReference = string.IsNullOrWhiteSpace(entry.AgreementReference) ? [] : byReference[Norm(entry.AgreementReference)].ToList();
            if (viaSource.Count > 0 && viaReference.Count > 0 && !viaSource.Select(r => r.Id).Intersect(viaReference.Select(r => r.Id)).Any())
            {
                messages.Add((true, "MIG-F012", $"The manifest's SourceRecordId '{entry.SourceRecordId}' and AgreementReference '{entry.AgreementReference}' point at different contracts."));
                return Ambiguous(file, entry, viaSource.Select(r => new Candidate(r.Id, MatchMethod.SourceRecordId, $"SourceRecordId '{entry.SourceRecordId}' equals contract row {r.Sequence}")).Concat(
                    viaReference.Select(r => new Candidate(r.Id, MatchMethod.AgreementReference, $"AgreementReference '{entry.AgreementReference}' equals contract row {r.Sequence}"))).ToList(), messages);
            }
            if (viaSource.Count == 1) return Matched(file, entry, viaSource[0], MatchMethod.SourceRecordId, $"The manifest's SourceRecordId '{entry.SourceRecordId}' equals the SourceRecordId of contract row {viaSource[0].Sequence}.", messages);
            if (viaSource.Count > 1) return Ambiguous(file, entry, viaSource.Select(r => new Candidate(r.Id, MatchMethod.SourceRecordId, $"SourceRecordId '{entry.SourceRecordId}' equals contract row {r.Sequence}")).ToList(), messages);
            if (viaReference.Count == 1) return Matched(file, entry, viaReference[0], MatchMethod.AgreementReference, $"The manifest's AgreementReference '{entry.AgreementReference}' equals the agreement reference of contract row {viaReference[0].Sequence}.", messages);
            if (viaReference.Count > 1) return Ambiguous(file, entry, viaReference.Select(r => new Candidate(r.Id, MatchMethod.AgreementReference, $"AgreementReference '{entry.AgreementReference}' equals contract row {r.Sequence}")).ToList(), messages);
        }

        // Method 3: the configured naming rule, applied to the file's path.
        if (naming is not null)
        {
            try
            {
                var m = naming.Match(file.RelativePath);
                if (m.Success)
                {
                    var sourceValue = m.Groups["sourceRecordId"].Success ? m.Groups["sourceRecordId"].Value.Trim() : null;
                    var referenceValue = m.Groups["agreementReference"].Success ? m.Groups["agreementReference"].Value.Trim() : null;
                    var found = sourceValue is { Length: > 0 } ? bySource[sourceValue].ToList() : referenceValue is { Length: > 0 } ? byReference[Norm(referenceValue)].ToList() : [];
                    var shown = sourceValue is { Length: > 0 } ? $"sourceRecordId '{sourceValue}'" : $"agreementReference '{referenceValue}'";
                    if (found.Count == 1) return Matched(file, entry, found[0], MatchMethod.NamingRule, $"The naming rule read {shown} from the path, which equals contract row {found[0].Sequence}.", messages);
                    if (found.Count > 1) return Ambiguous(file, entry, found.Select(r => new Candidate(r.Id, MatchMethod.NamingRule, $"The naming rule read {shown}, which equals contract row {r.Sequence}")).ToList(), messages);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                messages.Add((false, "MIG-F011", "The naming rule timed out on this path and was skipped; the next matching method was tried."));
            }
        }

        // Method 4: the exact parent folder name.
        var folder = ArchivePaths.ParentFolder(file.RelativePath);
        if (!string.IsNullOrEmpty(folder))
        {
            var key = Norm(folder);
            var viaFolder = byFolder[key].Concat(rows.Where(r => string.IsNullOrWhiteSpace(r.DocumentFolder) && !string.IsNullOrWhiteSpace(r.AgreementReference) && Norm(r.AgreementReference!) == key)).DistinctBy(r => r.Id).OrderBy(r => r.Sequence).ToList();
            if (viaFolder.Count == 1) return Matched(file, entry, viaFolder[0], MatchMethod.FolderName, $"The folder name '{folder}' equals the document folder or agreement reference of contract row {viaFolder[0].Sequence}.", messages);
            if (viaFolder.Count > 1) return Ambiguous(file, entry, viaFolder.Select(r => new Candidate(r.Id, MatchMethod.FolderName, $"The folder name '{folder}' equals contract row {r.Sequence}")).ToList(), messages);
        }

        messages.Add(entry is null
            ? (true, "MIG-F010", $"'{file.RelativePath}' has no manifest row and no contract matches its name or folder. It stays in the unmatched queue.")
            : (true, "MIG-F011", $"No contract matches the manifest row for '{file.RelativePath}'. It stays in the unmatched queue."));
        return new FileMatch(file.Id, entry?.Id, MatchOutcome.NoMatch, null, null, [], messages);
    }

    private static FileMatch Matched(MatchFile file, MatchEntry? entry, MatchRow row, MatchMethod method, string reason, List<(bool, string, string)> messages)
    {
        var candidate = new Candidate(row.Id, method, reason);
        if (row.Rejected)
        {
            messages.Add((true, "MIG-F015", $"The contract row {row.Sequence} that this file belongs to was rejected, so the file is held in the queue until the contract is loaded or the file is discarded."));
            return new FileMatch(file.Id, entry?.Id, MatchOutcome.TargetRejected, method, row.Id, [candidate], messages);
        }
        return new FileMatch(file.Id, entry?.Id, MatchOutcome.Matched, method, row.Id, [candidate], messages);
    }

    private static FileMatch Ambiguous(MatchFile file, MatchEntry? entry, IReadOnlyList<Candidate> candidates, List<(bool, string, string)> messages)
    {
        if (messages.All(m => m.Item2 != "MIG-F012"))
            messages.Add((true, "MIG-F012", $"More than one contract matches '{file.RelativePath}'. A person must choose; the platform does not break ties."));
        return new FileMatch(file.Id, entry?.Id, MatchOutcome.Ambiguous, null, null, candidates, messages);
    }

    private static string Norm(string value) => value.Trim().ToUpperInvariant();
}
