using System.Security.Cryptography;
using Dapper;
using JbsContracts.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace JbsContracts.Api.Features;

/// <summary>
/// Documents on a contract. Files are stored in the server's data folder under a random name (never the uploaded name), with a
/// SHA-256, and every download checks the person can see the contract. JBS scans files before upload (decision ADR-0049 of the old
/// app, kept): the app does not scan. Nothing is deleted: a removed document is hidden from the contract and kept.
/// </summary>
public static class DocumentEndpoints
{
    public const long MaxBytes = 25 * 1024 * 1024; // under IIS's default 30 MB request limit

    public static readonly Dictionary<string, string> Kinds = new()
    {
        ["Draft"] = "Draft", ["FinalAgreement"] = "Final agreement (for signing)", ["SignedCopy"] = "Signed copy", ["Schedule"] = "Schedule",
        ["LegalMarkup"] = "Legal mark-up", ["Variation"] = "Variation", ["Correspondence"] = "Correspondence", ["Supporting"] = "Supporting document",
    };

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".msg"] = "application/vnd.ms-outlook",
        [".eml"] = "message/rfc822",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    /// <summary>What's wrong with an uploaded file, in plain words, or null if it's fine (size and kind). Shared with supplier documents.</summary>
    public static string? FileProblem(IFormFile? file)
    {
        if (file is null || file.Length == 0) return "Choose a file to upload.";
        if (file.Length > MaxBytes) return "That file is over 25 MB. Make it smaller (for example, save it as a PDF) and try again.";
        if (!Types.ContainsKey(Path.GetExtension(file.FileName))) return "That kind of file can't be added. Use PDF, Word, Excel, Outlook email, text, CSV, PNG or JPG.";
        return null;
    }

    public sealed record StoredFile(string FileName, string ContentType, long Size, string Sha256, string StoredName, string Path);

    /// <summary>
    /// Saves an uploaded file under a random name in the documents folder (the uploaded name is kept only as text, never used as a path) and
    /// works out its SHA-256 on the way. The caller deletes <see cref="StoredFile.Path"/> if the database change doesn't go ahead.
    /// </summary>
    public static async Task<StoredFile> StoreAsync(string root, IFormFile file)
    {
        var extension = System.IO.Path.GetExtension(file.FileName);
        var fileName = System.IO.Path.GetFileName(file.FileName.Replace('\\', '/'));
        if (fileName.Length > 255) fileName = fileName[^255..];
        var stored = $"{DateTime.UtcNow:yyyy}/{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var path = System.IO.Path.Combine(root, stored.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await using var input = file.OpenReadStream();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
        return new StoredFile(fileName, Types[extension], file.Length, Convert.ToHexString(hash.GetHashAndReset()), stored, path);
    }

    /// <summary>Contract owners add documents until a contract is approved; during a Legal review, Legal can too (mark-ups), on either side's turn.</summary>
    /// <remarks>During an emergency unlock, admins can add and remove documents too (D-79). Documents for a Variation go through it instead.</remarks>
    public static bool CanUpload(Person me, ContractRow c) => c.IsLocked
        ? me.IsAdmin && c.UnlockId is not null && c.Stage is "Effective" or "Ended"
        : c.Stage switch
        {
            "Draft" or "Signing" => me.IsContractOwner,
            "LegalReview" => me.IsContractOwner || me.IsLegal,
            _ => false,
        };

    private sealed class DocRow
    {
        public int Id { get; set; }
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public int CurrentVersion { get; set; }
        public int VersionId { get; set; }
        public int VersionNumber { get; set; }
        public string FileName { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime UploadedUtc { get; set; }
        public string UploadedByName { get; set; } = "";
        public int? VariationId { get; set; }
        public int? VariationNumber { get; set; }
    }

    /// <summary>The contract's documents (each Variation's included, marked with its number), or only one Variation's.</summary>
    public static async Task<object> ListAsync(SqlConnection c, int contractId, int? variationId = null)
    {
        var rows = (await c.QueryAsync<DocRow>("""
            SELECT d.Id, d.Kind, d.Title, d.CurrentVersion, v.Id AS VersionId, v.VersionNumber, v.FileName, v.SizeBytes, v.UploadedUtc, p.Name AS UploadedByName,
                   d.VariationId, vr.Number AS VariationNumber
            FROM contracts.Document d
            JOIN contracts.DocumentVersion v ON v.DocumentId = d.Id
            JOIN contracts.Person p ON p.Id = v.UploadedById
            LEFT JOIN contracts.Variation vr ON vr.Id = d.VariationId
            WHERE d.ContractId = @contractId AND d.IsRemoved = 0 AND (@variationId IS NULL OR d.VariationId = @variationId)
            ORDER BY d.Id, v.VersionNumber DESC
            """, new { contractId, variationId })).ToList();
        return rows.GroupBy(r => r.Id).Select(g =>
        {
            var first = g.First();
            return new
            {
                first.Id, first.Kind, KindName = Kinds[first.Kind], first.Title, first.CurrentVersion, first.VariationId, first.VariationNumber,
                Versions = g.Select(v => new { Id = v.VersionId, v.VersionNumber, v.FileName, v.SizeBytes, UploadedUtc = DateTime.SpecifyKind(v.UploadedUtc, DateTimeKind.Utc), v.UploadedByName }),
            };
        }).ToList();
    }

    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/contracts/{id:int}/documents", async (int id, HttpRequest request, CurrentUser current, Db db, AuditLog audit, AppSettings settings, ILoggerFactory logs) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            if (settings.DocumentsDirectory is not { } root)
                return ApiResults.Problem(StatusCodes.Status503ServiceUnavailable, "no-storage", "Documents can't be saved yet because the server has no data folder set. Tell your admin.");
            if (!request.HasFormContentType) return ApiResults.Invalid("Choose a file to upload.", new() { ["file"] = "Choose a file to upload." });
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            var kind = form["kind"].ToString();
            int? documentId = int.TryParse(form["documentId"], out var d) ? d : null;
            int? variationId = int.TryParse(form["variationId"], out var vid) ? vid : null;
            var fields = new Dictionary<string, string>();
            if (file is null || file.Length == 0) fields["file"] = "Choose a file to upload.";
            else if (file.Length > MaxBytes) fields["file"] = "That file is over 25 MB. Make it smaller (for example, save it as a PDF) and try again.";
            var extension = Path.GetExtension(file?.FileName ?? "");
            if (file is not null && !Types.ContainsKey(extension)) fields["file"] = "That kind of file can't be added. Use PDF, Word, Excel, Outlook email, text, CSV, PNG or JPG.";
            if (documentId is null && !Kinds.ContainsKey(kind)) fields["kind"] = "Choose what kind of document this is.";
            if (fields.Count > 0) return ApiResults.Invalid("Check the file.", fields);

            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var contract = await ContractEndpoints.LoadForUpdateAsync(c, tx, id);
            if (contract is null || !ContractAccess.CanSee(me!, contract.IsConfidential, contract.OwnerId, contract.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            if (variationId is { } v)
            {
                // An open Variation's documents: its owner side, and Legal while it's with Legal.
                var stage = await c.QuerySingleOrDefaultAsync<string>("SELECT Stage FROM contracts.Variation WHERE Id = @v AND ContractId = @id", new { v, id }, tx);
                if (stage is null) return ApiResults.NotFound("We couldn't find that Variation.");
                if (stage is not ("Draft" or "LegalReview" or "Approval") || !(me!.IsContractOwner || (me.IsLegal && stage == "LegalReview")))
                    return ApiResults.NotAllowed("You can't add documents to this Variation at the moment.");
            }
            else if (!CanUpload(me!, contract))
                return ApiResults.NotAllowed(contract.IsLocked ? "This contract is locked. Documents are added through a Variation." : "You can't add documents to this contract at the moment.");

            // Safe name: the uploaded file name is only kept as text, never used as a path.
            var fileName = Path.GetFileName(file!.FileName.Replace('\\', '/'));
            if (fileName.Length > 255) fileName = fileName[^255..];
            var stored = $"{DateTime.UtcNow:yyyy}/{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var path = Path.Combine(root, stored.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string sha;
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
                sha = Convert.ToHexString(hash.GetHashAndReset());
            }

            try
            {
                int version;
                string title;
                if (documentId is { } existing)
                {
                    var doc = await c.QuerySingleOrDefaultAsync<DocRow>("SELECT Id, Kind, Title, CurrentVersion FROM contracts.Document WITH (UPDLOCK) WHERE Id = @existing AND ContractId = @id AND IsRemoved = 0", new { existing, id }, tx);
                    if (doc is null) { File.Delete(path); return ApiResults.NotFound("We couldn't find that document on this contract."); }
                    version = doc.CurrentVersion + 1;
                    title = doc.Title;
                    kind = doc.Kind;
                    await c.ExecuteAsync("UPDATE contracts.Document SET CurrentVersion = @version WHERE Id = @existing", new { version, existing }, tx);
                }
                else
                {
                    version = 1;
                    title = Path.GetFileNameWithoutExtension(fileName);
                    if (title.Length == 0) title = "Document";
                    if (title.Length > 200) title = title[..200];
                    documentId = await c.ExecuteScalarAsync<int>("INSERT INTO contracts.Document (ContractId, VariationId, Kind, Title, CreatedById) OUTPUT INSERTED.Id VALUES (@id, @variationId, @kind, @title, @me)",
                        new { id, variationId, kind, title, me = me!.Id }, tx);
                }
                var versionId = await c.ExecuteScalarAsync<int>("""
                    INSERT INTO contracts.DocumentVersion (DocumentId, VersionNumber, FileName, ContentType, SizeBytes, Sha256, StoredName, UploadedById)
                    OUTPUT INSERTED.Id VALUES (@documentId, @version, @fileName, @type, @size, @sha, @stored, @me)
                    """, new { documentId, version, fileName, type = Types[extension], size = file.Length, sha, stored, me = me!.Id }, tx);
                await audit.WriteAsync(c, tx, me.AsActor(), version == 1 ? "document.added" : "document.version-added", "Document", documentId,
                    version == 1 ? $"{me.Name} added the {Kinds[kind].ToLowerInvariant()} {fileName}." : $"{me.Name} added version {version} of {title} ({fileName}).",
                    new { fileName, kind, version, sizeBytes = file.Length, sha256 = sha }, contractId: id);
                // The contract's row version changes too; it's returned so the page can carry on (for example, attach then hand over).
                var rowVersion = await c.ExecuteScalarAsync<byte[]>(
                    "UPDATE contracts.Contract SET UpdatedUtc = SYSUTCDATETIME(), UpdatedById = @me OUTPUT INSERTED.RowVersion WHERE Id = @id", new { id, me = me.Id }, tx);
                await tx.CommitAsync();
                return Results.Created($"/api/documents/{versionId}/file", new { documentId, versionId, version, rowVersion = Convert.ToBase64String(rowVersion!) });
            }
            catch
            {
                try { File.Delete(path); } catch (Exception ex) { logs.CreateLogger("Documents").LogWarning(ex, "Could not remove an unsaved upload {Stored}.", stored); }
                throw;
            }
        }).DisableAntiforgery();

        app.MapGet("/api/documents/{versionId:int}/file", async (int versionId, CurrentUser current, Db db, AppSettings settings) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            var row = await c.QuerySingleOrDefaultAsync("""
                SELECT v.FileName, v.ContentType, v.StoredName, k.IsConfidential, k.OwnerId, k.BackupOwnerId
                FROM contracts.DocumentVersion v JOIN contracts.Document d ON d.Id = v.DocumentId JOIN contracts.Contract k ON k.Id = d.ContractId
                WHERE v.Id = @versionId
                """, new { versionId });
            if (row is null || !ContractAccess.CanSee(me!, (bool)row.IsConfidential, (int)row.OwnerId, (int?)row.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that document, or you don't have access to it.");
            var path = Path.Combine(settings.DocumentsDirectory ?? "", ((string)row.StoredName).Replace('/', Path.DirectorySeparatorChar));
            if (settings.DocumentsDirectory is null || !File.Exists(path))
                return ApiResults.Problem(StatusCodes.Status410Gone, "file-missing", "The file for this document is missing from the server. Tell your admin.");
            return Results.File(path, (string)row.ContentType, (string)row.FileName);
        });

        app.MapPost("/api/contracts/{id:int}/documents/{documentId:int}/remove", async (int id, int documentId, CurrentUser current, Db db, AuditLog audit) =>
        {
            var (me, refusal) = await ApiResults.RequirePersonAsync(current, db);
            if (refusal is not null) return refusal;
            await using var c = await db.OpenAsync();
            await using var tx = await Db.BeginWriteAsync(c);
            var contract = await ContractEndpoints.LoadForUpdateAsync(c, tx, id);
            if (contract is null || !ContractAccess.CanSee(me!, contract.IsConfidential, contract.OwnerId, contract.BackupOwnerId))
                return ApiResults.NotFound("We couldn't find that contract. It may have been removed, or you may not have access.");
            if (!CanUpload(me!, contract)) return ApiResults.NotAllowed("You can't remove documents from this contract at the moment.");
            var title = await c.QuerySingleOrDefaultAsync<string>("SELECT Title FROM contracts.Document WHERE Id = @documentId AND ContractId = @id AND IsRemoved = 0", new { documentId, id }, tx);
            if (title is null) return ApiResults.NotFound("We couldn't find that document on this contract.");
            await c.ExecuteAsync("UPDATE contracts.Document SET IsRemoved = 1 WHERE Id = @documentId", new { documentId }, tx);
            await audit.WriteAsync(c, tx, me!.AsActor(), "document.removed", "Document", documentId, $"{me.Name} removed {title} from the contract. The file and its history are kept.", contractId: id);
            await tx.CommitAsync();
            return Results.Ok(new { documentId });
        });
    }
}
