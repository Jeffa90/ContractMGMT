using System.Globalization;
using System.IO.Compression;
using Dapper;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// The Legal record (D-122): a contract's (or Variation's) Legal conversation to file in Legal's matter system. One ZIP: a Word
/// document with the contract's basic details and every entry (who, when, what was said, which files), and the files attached to
/// the conversation, numbered to match. Any time there's a conversation; Legal and admins only; Confidential rules apply; audited.
/// </summary>
public static class LegalRecord
{
    private static readonly TimeZoneInfo Brisbane = TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane");
    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");
    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Brisbane);
    private static string When(DateTime utc) => Local(utc).ToString("ddd d MMM yyyy, h:mmtt", CultureInfo.InvariantCulture).Replace("AM", "am").Replace("PM", "pm");
    private static string Day(DateTime? d) => d is { } v ? v.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : "Not filled in";

    private sealed class FileRow
    {
        public int LegalTurnId { get; set; }
        public int VersionNumber { get; set; }
        public string Title { get; set; } = "";
        public string FileName { get; set; } = "";
        public string StoredName { get; set; } = "";
    }

    private sealed class VariationInfo
    {
        public int Number { get; set; }
        public string Reason { get; set; } = "";
        public string Stage { get; set; } = "";
        public string? WhoseTurn { get; set; }
        public string? LegalPersonName { get; set; }
        public DateTime? LegalClearedUtc { get; set; }
        public string? LegalClearedByName { get; set; }
    }

    /// <summary>One entry in plain words, as the conversation card shows it.</summary>
    private static string Said(LegalEndpoints.Entry e) => e.Action switch
    {
        "SentToLegal" => $"{e.ByName} sent it to Legal{(e.LegalPersonName is { } lp ? $" ({lp})" : "")}",
        "Taken" => $"{e.ByName} took it",
        "Assigned" => e.LegalPersonName == e.ByName ? $"{e.ByName} took it" : $"{e.ByName} gave it to {e.LegalPersonName}",
        "StatusChanged" => $"{e.ByName} set the status to {(e.Status is { } s ? LegalEndpoints.StatusNames.GetValueOrDefault(s, s) : "")}",
        "SentToOwner" => $"{e.ByName} sent it back to the owner",
        "Cleared" => $"{e.ByName} cleared it",
        _ => $"{e.ByName}: {e.Action}",
    };

    /// <summary>A name that's safe in a ZIP on any computer.</summary>
    private static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(name.Select(ch => bad.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "file" : clean.Length > 100 ? clean[..100] : clean;
    }

    public static void MapLegalRecordEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/contracts/{id:int}/legal-record.zip", (int id, CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock) =>
            DownloadAsync(id, null, current, db, audit, settings, clock));
        app.MapGet("/api/contracts/{id:int}/variations/{vid:int}/legal-record.zip", (int id, int vid, CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock) =>
            DownloadAsync(id, vid, current, db, audit, settings, clock));
    }

    private static async Task<IResult> DownloadAsync(int id, int? vid, CurrentUser current, Db db, AuditLog audit, AppSettings settings, TimeProvider clock)
    {
        var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
        if (refusal is not null) return refusal;
        await using var c = await db.OpenAsync();
        var k = await ContractEndpoints.LoadAsync(c, id);
        if (k is null || !ContractAccess.CanSee(me!, k.IsConfidential, k.OwnerId, k.BackupOwnerId))
            return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
        if (!me!.IsLegal && !me.IsAdmin) return ApiResults.NotAllowed("The Legal record is for Legal and admins.");
        VariationInfo? v = null;
        if (vid is { } variationId)
        {
            v = await c.QuerySingleOrDefaultAsync<VariationInfo>("""
                SELECT v.Number, v.Reason, v.Stage, v.WhoseTurn, lp.Name AS LegalPersonName, v.LegalClearedUtc, lc.Name AS LegalClearedByName
                FROM contracts.Variation v LEFT JOIN contracts.Person lp ON lp.Id = v.LegalPersonId LEFT JOIN contracts.Person lc ON lc.Id = v.LegalClearedById
                WHERE v.Id = @variationId AND v.ContractId = @id
                """, new { variationId, id });
            if (v is null) return ApiResults.NotFound("We couldn't find that Variation.");
        }
        var entries = (await c.QueryAsync<LegalEndpoints.Entry>("""
            SELECT t.Id, t.Number, t.Action, by_.Name AS ByName, t.AtUtc, t.WhoseTurn, lp.Name AS LegalPersonName, t.Status, t.Note
            FROM contracts.LegalTurn t JOIN contracts.Person by_ ON by_.Id = t.ById LEFT JOIN contracts.Person lp ON lp.Id = t.LegalPersonId
            WHERE t.ContractId = @id AND ((@vid IS NULL AND t.VariationId IS NULL) OR t.VariationId = @vid) ORDER BY t.Number
            """, new { id, vid })).ToList();
        if (entries.Count == 0) return ApiResults.NotFound("There's no Legal conversation to download yet.");
        var files = (await c.QueryAsync<FileRow>("""
            SELECT td.LegalTurnId, dv.VersionNumber, d.Title, dv.FileName, dv.StoredName
            FROM contracts.LegalTurnDocument td JOIN contracts.LegalTurn t ON t.Id = td.LegalTurnId
            JOIN contracts.DocumentVersion dv ON dv.Id = td.DocumentVersionId JOIN contracts.Document d ON d.Id = dv.DocumentId
            WHERE t.ContractId = @id AND ((@vid IS NULL AND t.VariationId IS NULL) OR t.VariationId = @vid)
            ORDER BY t.Number, d.Title, dv.VersionNumber
            """, new { id, vid })).ToLookup(f => f.LegalTurnId);

        var name = v is null ? $"{k.Reference}" : $"{k.Reference} Variation {v.Number}";
        var now = clock.GetUtcNow().UtcDateTime;
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), $"jbs-legal-{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var inZip = new Dictionary<(int Turn, string Stored), string>();
        var missing = 0;
        using (var zip = new ZipArchive(temp, ZipArchiveMode.Create, leaveOpen: true))
        {
            // The files first, so the record can say where each one is.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
                foreach (var f in files[e.Id])
                {
                    var source = settings.DocumentsDirectory is null ? null : Path.GetFullPath(Path.Combine(settings.DocumentsDirectory, f.StoredName.Replace('/', Path.DirectorySeparatorChar)));
                    if (source is null || !source.StartsWith(Path.GetFullPath(settings.DocumentsDirectory!), StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(source)) { missing++; continue; }
                    var entryName = $"Files/{e.Number} - {Safe(Path.GetFileNameWithoutExtension(f.FileName))} (v{f.VersionNumber}){Safe(Path.GetExtension(f.FileName))}";
                    var unique = entryName;
                    for (var i = 2; !used.Add(unique); i++) unique = $"Files/{Path.GetFileNameWithoutExtension(entryName)} ({i}){Path.GetExtension(entryName)}";
                    var zipEntry = zip.CreateEntry(unique, CompressionLevel.Fastest);
                    await using (var to = await zipEntry.OpenAsync())
                    await using (var from = System.IO.File.OpenRead(source))
                        await from.CopyToAsync(to);
                    inZip[(e.Id, f.StoredName)] = unique;
                }
            var doc = Build(k, v, entries, files, inZip, me, now);
            var record = zip.CreateEntry($"Legal record - {Safe(name)}.docx", CompressionLevel.Fastest);
            await using (var to = await record.OpenAsync()) await to.WriteAsync(doc);
        }
        await using (var tx = await Db.BeginWriteAsync(c))
        {
            var count = inZip.Count;
            await audit.WriteAsync(c, tx, me.AsActor(), "legal.record-downloaded", "Contract", id,
                $"{me.Name} downloaded the Legal record of {name}: {entries.Count} entries and {count} {(count == 1 ? "file" : "files")}{(missing > 0 ? $" ({missing} missing from the server)" : "")}.",
                new { variation = v?.Number, entries = entries.Count, files = count, missing }, contractId: id);
            await tx.CommitAsync();
        }
        temp.Position = 0;
        return Results.File(temp, "application/zip", $"{Safe(name)} Legal record {Local(now):yyyy-MM-dd}.zip");
    }

    private static byte[] Build(ContractRow k, VariationInfo? v, List<LegalEndpoints.Entry> entries, ILookup<int, FileRow> files,
        Dictionary<(int Turn, string Stored), string> inZip, Person me, DateTime nowUtc)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);
            const string Navy = "232378";
            body.Append(ReviewDocument.Para(v is null ? $"Legal record: {k.Reference}" : $"Legal record: {k.Reference} Variation {v.Number}", size: 32, bold: true, colour: Navy, after: 80));
            body.Append(ReviewDocument.Para(k.Title, size: 24, after: 80));

            // Where Legal's review stands, as at the download.
            var clearedUtc = v is null ? k.LegalClearedUtc : v.LegalClearedUtc;
            var clearedBy = v is null ? k.LegalClearedByName : v.LegalClearedByName;
            var whoseTurn = v is null ? k.WhoseTurn : v.WhoseTurn;
            var stage = v is null ? k.Stage : v.Stage;
            var status = clearedUtc is { } cu ? $"Cleared by Legal{(clearedBy is { } cb ? $" ({cb})" : "")}, {When(cu)}."
                : stage == "LegalReview" ? (whoseTurn == "Legal" ? $"With Legal{((v is null ? k.LegalPersonName : v.LegalPersonName) is { } lp ? $" ({lp})" : " (not picked up yet)")}." : "With the owner, back from Legal.")
                : "Not cleared by Legal.";
            body.Append(ReviewDocument.Para($"Legal review: {status}", colour: "555555", after: 240));

            body.Append(ReviewDocument.Heading(v is null ? "The contract" : "The contract and the Variation"));
            var rows = new List<string[]>
            {
                new[] { "Reference", k.Reference }, new[] { "Title", k.Title }, new[] { "Supplier", k.SupplierName ?? "Not chosen" },
                new[] { "Category", k.CategoryDisplay ?? "Not chosen" }, new[] { "Agreement type", k.AgreementTypeName ?? "Not chosen" },
                new[] { "Stage", ContractEndpoints.StageNames.GetValueOrDefault(k.Stage, k.Stage) },
                new[] { "Value", k.Value is { } val ? $"${val.ToString("N2", Au)} {k.CurrencyCode}" : "Not filled in" },
                new[] { "Start date", Day(k.StartDate) },
                new[] { "End date", k.IsEvergreen ? "Evergreen (no end date)" : Day(k.EndDate) },
                new[] { "Notice period", k.NoticePeriodDays is { } n ? $"{n} days" : "Not filled in" },
                new[] { "Owner", k.OwnerName + (k.BackupOwnerName is { } b ? $" (backup {b})" : "") },
                new[] { "Confidential", k.IsConfidential ? "Yes" : "No" },
            };
            if (v is not null)
            {
                rows.Add(["Variation", $"Variation {v.Number}, {ContractEndpoints.StageNames.GetValueOrDefault(v.Stage, v.Stage)}"]);
                rows.Add(["Why it's changing", v.Reason]);
            }
            body.Append(ReviewDocument.Table(["", ""], rows, [2600, 6400], header: false));

            body.Append(ReviewDocument.Heading("The conversation"));
            foreach (var e in entries)
            {
                body.Append(ReviewDocument.Para($"{e.Number}. {Said(e)}, {When(e.AtUtc)}", bold: true, before: 160, after: 60, keepNext: true));
                if (!string.IsNullOrWhiteSpace(e.Note))
                    foreach (var line in e.Note.Replace("\r\n", "\n").Split('\n')) body.Append(ReviewDocument.Para(line, colour: "333333", after: 40));
                foreach (var f in files[e.Id])
                    body.Append(ReviewDocument.Para(
                        $"Attached: {f.FileName} (version {f.VersionNumber}){(inZip.TryGetValue((e.Id, f.StoredName), out var path) ? $", in this ZIP as {path}" : ", missing from the server")}",
                        size: 20, colour: "555555", after: 40));
            }

            body.Append(ReviewDocument.Para($"From JBS Contracts, {When(nowUtc)}, by {me.Name}.", size: 16, colour: "777777", before: 360));
            body.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 708, Footer = 708, Gutter = 0 }));
            main.Document.Save();
        }
        return stream.ToArray();
    }
}
