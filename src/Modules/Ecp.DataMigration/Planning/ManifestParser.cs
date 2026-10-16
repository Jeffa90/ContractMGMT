using System.Globalization;
using System.Text.Json;
using Ecp.DataMigration.Domain;
using Ecp.DataMigration.Formats;
using Ecp.Formats;
using Ecp.DataMigration.Mapping;

namespace Ecp.DataMigration.Planning;

/// <summary>The platform's reference document types (reference-lookups.csv). They are data the platform ships, not business rules; an organisation may add a documentType lookup to its profile.</summary>
public static class ReferenceDocumentTypes
{
    public static readonly IReadOnlyList<(string Code, string Label)> All =
    [
        ("ExecutedContract", "Executed Contract"), ("Schedule", "Schedule"), ("VariationDocument", "Variation"), ("LegalAdvice", "Legal advice"),
        ("ApprovalEvidence", "Approval evidence"), ("SupportingDocument", "Supporting document"), ("Correspondence", "Correspondence")
    ];

    public const string DefaultCode = "SupportingDocument";
    public const string LookupKey = "documentType";

    /// <summary>Resolves a manifest's document type: the profile's documentType lookup first, then an exact match on the code or the label ignoring case, spaces and punctuation.</summary>
    public static string? Resolve(string? raw, MappingDefinition? definition)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (definition is not null)
        {
            var hit = definition.Lookups.FirstOrDefault(l => l.LookupMapKey.Equals(LookupKey, StringComparison.OrdinalIgnoreCase) && l.SourceValue.Trim().Equals(text, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit.TargetCode;
        }
        var key = Squash(text);
        foreach (var (code, label) in All)
            if (Squash(code) == key || Squash(label) == key) return code;
        return null;
    }

    private static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed record ParsedManifestEntry(int Sequence, int RowNumber, string ColumnsJson, string? SourceRecordId, string? AgreementReference, string? RawPath, string? RelativePath, string? FileName,
    string? TypeRaw, string? TypeCode, DateOnly? DocumentDate, string? VersionLabel, bool IsExecutedCopy, string? ParentDocumentSourceId, string? DeclaredSha256, string? DocumentSourceId,
    ManifestEntryState State, IReadOnlyList<RowMessage> Messages);

/// <summary>Reads the attachment manifest (ATTACHMENT_INGESTION.md preferred columns, plus the optional DocumentSourceId and SourceSystem from the supplement).</summary>
public static class ManifestParser
{
    public static readonly IReadOnlyList<string> KnownColumns =
        ["SourceRecordId", "AgreementReference", "RelativePath", "FileName", "DocumentType", "DocumentDate", "VersionLabel", "IsExecutedCopy", "ParentDocumentSourceId", "Sha256", "Notes", "DocumentSourceId", "SourceSystem"];

    public static IReadOnlyList<ParsedManifestEntry> Parse(SourceTable table, MappingDefinition? definition, string projectSourceSystem, out IReadOnlyList<RowMessage> fileMessages)
    {
        var batchMessages = new List<RowMessage>();
        var index = table.Headers.Select((h, i) => (h, i)).ToDictionary(x => x.h, x => x.i, StringComparer.OrdinalIgnoreCase);
        if (!index.ContainsKey("RelativePath"))
            throw new SourceFormatException("MIG-E014", "The manifest needs a RelativePath column (the path of each file inside the archive).");
        foreach (var unknown in table.Headers.Where(h => !KnownColumns.Contains(h, StringComparer.OrdinalIgnoreCase)))
            batchMessages.Add(new(false, "MIG-W002", unknown, $"The manifest column '{unknown}' is not one the platform uses. It is kept with the manifest row and otherwise ignored."));

        var list = new List<ParsedManifestEntry>(); var seq = 0;
        foreach (var row in table.Rows)
        {
            seq++;
            string Get(string name) => index.TryGetValue(name, out var i) && i < row.Fields.Count ? row.Fields[i].Trim() : "";
            string? Opt(string name) { var v = Get(name); return v.Length == 0 ? null : v; }
            var messages = new List<RowMessage>(); var invalid = false;
            if (row.Ragged) { messages.Add(new(true, "MIG-E013", "row", $"The manifest row has {row.Fields.Count} values; the header has {table.Headers.Count}. Ragged rows are errors, not padded.")); invalid = true; }

            var raw = Opt("RelativePath");
            string? path = null;
            if (raw is null) { messages.Add(new(true, "MIG-F019", "RelativePath", "RelativePath is required.")); invalid = true; }
            else
            {
                var (p, reason) = ArchivePaths.Normalise(raw);
                if (p is null) { messages.Add(new(true, "MIG-F001", "RelativePath", $"'{raw}' was rejected: {reason}")); invalid = true; } else path = p;
            }
            var fileName = Opt("FileName") ?? (path is null ? null : ArchivePaths.FileName(path));
            if (path is not null && Opt("FileName") is { } declared && !declared.Equals(ArchivePaths.FileName(path), StringComparison.OrdinalIgnoreCase))
                messages.Add(new(false, "MIG-W002", "FileName", $"FileName '{declared}' differs from the name in RelativePath '{ArchivePaths.FileName(path)}'. The path is used."));

            var typeRaw = Opt("DocumentType");
            string? typeCode = null;
            if (typeRaw is not null)
            {
                typeCode = ReferenceDocumentTypes.Resolve(typeRaw, definition);
                if (typeCode is null) { messages.Add(new(true, "MIG-F019", "DocumentType", $"The document type '{typeRaw}' is not recognised. Correct the manifest or add a documentType lookup to the mapping profile.")); invalid = true; }
            }

            DateOnly? date = null;
            if (Opt("DocumentDate") is { } d)
            {
                if (DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) date = parsed;
                else { messages.Add(new(true, "MIG-F019", "DocumentDate", $"'{d}' is not a date in the format yyyy-MM-dd.")); invalid = true; }
            }

            var executed = false;
            if (Opt("IsExecutedCopy") is { } e)
            {
                if (e.Equals("Yes", StringComparison.OrdinalIgnoreCase) || e.Equals("Y", StringComparison.OrdinalIgnoreCase) || e.Equals("True", StringComparison.OrdinalIgnoreCase) || e == "1") executed = true;
                else if (!(e.Equals("No", StringComparison.OrdinalIgnoreCase) || e.Equals("N", StringComparison.OrdinalIgnoreCase) || e.Equals("False", StringComparison.OrdinalIgnoreCase) || e == "0"))
                { messages.Add(new(true, "MIG-F019", "IsExecutedCopy", $"'{e}' is not yes or no.")); invalid = true; }
            }

            var sha = Opt("Sha256")?.ToLowerInvariant();
            if (sha is not null && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) { messages.Add(new(true, "MIG-F019", "Sha256", "Sha256 must be 64 hexadecimal characters.")); invalid = true; sha = null; }
            var version = Opt("VersionLabel");
            if (version is { Length: > 50 }) { messages.Add(new(true, "MIG-E009", "VersionLabel", "VersionLabel is longer than 50 characters. It is not shortened.")); invalid = true; version = null; }

            var sourceRecordId = Opt("SourceRecordId"); var reference = Opt("AgreementReference");
            if (sourceRecordId is null && reference is null) messages.Add(new(false, "MIG-W002", "SourceRecordId", "Neither SourceRecordId nor AgreementReference is given; the file can be matched only by the naming rule or its folder."));
            var system = Opt("SourceSystem");
            if (system is not null && !system.Equals(projectSourceSystem, StringComparison.Ordinal))
            { messages.Add(new(true, "MIG-E007", "SourceSystem", $"The manifest row names the source system '{system}', not the project's '{projectSourceSystem}'.")); invalid = true; }
            var documentSourceId = Opt("DocumentSourceId") ?? (path is null ? null : $"{sourceRecordId ?? reference ?? "-"}/{path}");
            if (documentSourceId is { Length: > 400 }) { messages.Add(new(true, "MIG-E009", "DocumentSourceId", "The document source id is longer than 400 characters. It is not shortened.")); invalid = true; }

            var columns = table.Headers.Zip(Enumerable.Range(0, table.Headers.Count), (h, i) => (h, v: i < row.Fields.Count ? row.Fields[i] : "")).ToDictionary(x => x.h, x => x.v);
            list.Add(new ParsedManifestEntry(seq, row.RowNumber, JsonSerializer.Serialize(columns), sourceRecordId, reference, raw, path, fileName, typeRaw, typeCode, date, version, executed,
                Opt("ParentDocumentSourceId"), sha, documentSourceId, invalid ? ManifestEntryState.Invalid : ManifestEntryState.Pending, messages));
        }

        // The same path named twice makes pairing ambiguous: the matcher reports it per file; here it is flagged on the rows.
        foreach (var dup in list.Where(e => e.RelativePath is not null).GroupBy(e => e.RelativePath!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var entry in dup) ((List<RowMessage>)entry.Messages).Add(new(true, "MIG-F012", "RelativePath", $"The path '{dup.Key}' is named by {dup.Count()} manifest rows."));
        fileMessages = batchMessages;
        return list;
    }
}
