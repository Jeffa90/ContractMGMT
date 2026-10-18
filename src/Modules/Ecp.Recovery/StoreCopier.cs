using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecp.Documents;
using Ecp.Documents.Storage;
using Ecp.SharedKernel;

namespace Ecp.Recovery;

/// <summary>One line of the document index inside a backup: where an object was, how large it was, and the SHA-256 of the bytes that were copied.</summary>
public sealed record DocumentIndexLine(string Key, long Bytes, string Sha256, bool HashMatchesKey);

public sealed record DocumentCopyResult(IReadOnlyList<DocumentIndexLine> Lines, long Skipped, IReadOnlyList<string> SkippedNames)
{
    public long ByteTotal => Lines.Sum(l => l.Bytes);
}

/// <summary>
/// Copies the document store into a backup through the storage abstraction: it asks the store for its objects (it never walks the store's directories itself), streams each one to the backup while hashing it, and
/// writes an index (one JSON line per object) that restore verifies object by object. A link or an unexpected name in the store is skipped and reported, never followed.
/// </summary>
public static class DocumentStoreCopier
{
    public static readonly JsonSerializerOptions LineOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static async Task<DocumentCopyResult> CopyAsync(IDocumentStoreInventory inventory, IDocumentStorageProvider provider, string destinationRoot, CancellationToken ct)
    {
        var lines = new List<DocumentIndexLine>();
        var skipped = new List<string>();
        Directory.CreateDirectory(destinationRoot);
        await foreach (var item in inventory.EnumerateAsync(skipped.Add, ct))
        {
            var target = SafeFiles.Resolve(Path.GetDirectoryName(destinationRoot)!, ManifestFiles.DocumentsDirectory + "/" + item.StorageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string hash; long bytes = 0;
            await using (var input = await provider.OpenReadAsync(item.TenantId, item.StorageKey, ct))
            await using (var output = SafeFiles.CreateNew(target))
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16]; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0) { sha.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct); bytes += read; }
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            lines.Add(new DocumentIndexLine(item.StorageKey, bytes, hash, item.StorageKey.EndsWith("/" + hash, StringComparison.Ordinal)));
        }
        return new DocumentCopyResult(lines, skipped.Count, skipped.Take(20).ToList());
    }

    public static byte[] Serialise(IEnumerable<DocumentIndexLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(JsonSerializer.Serialize(l, LineOptions)).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static IEnumerable<DocumentIndexLine> ReadIndex(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        string? line;
        var count = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (line.Length > 4096 || ++count > 50_000_000) throw new DomainException("index_invalid", "The document index has a line that is too long or too many lines.");
            DocumentIndexLine? l;
            try { l = JsonSerializer.Deserialize<DocumentIndexLine>(line, LineOptions); }
            catch (JsonException) { throw new DomainException("index_invalid", "The document index has a line that is not valid."); }
            if (l is null || l.Bytes < 0 || !SafeFiles.IsSha256(l.Sha256) || !IsStoreKey(l.Key)) throw new DomainException("index_invalid", "The document index has an entry that is not valid.");
            yield return l;
        }
    }

    /// <summary>A key in the content-addressed layout: t/{tenant}/{aa}/{sha256}. Anything else in an index is refused.</summary>
    public static bool IsStoreKey(string key)
    {
        var parts = key.Split('/');
        return parts.Length == 4 && parts[0] == "t" && parts[1].Length == 32 && Guid.TryParseExact(parts[1], "N", out _) && parts[2].Length == 2 && SafeFiles.IsSha256(parts[3]) && parts[3].StartsWith(parts[2], StringComparison.Ordinal);
    }

    public static Guid TenantOf(string key) => Guid.ParseExact(key.Split('/')[1], "N");
}

/// <summary>One file copied from the pack storage directory.</summary>
public sealed record PackFileCopy(string RelativePath, Guid TenantId, string Sha256, long Bytes, bool HashMatchesName);

/// <summary>Copies the installed package bytes (<c>tenants/{tenant}/packages/{sha256}.ecpkg</c>) and nothing else: quarantined uploads are not part of a backup.</summary>
public static class PackStoreCopier
{
    public static async Task<IReadOnlyList<PackFileCopy>> CopyAsync(string packStorageRoot, string backupRoot, CancellationToken ct)
    {
        var result = new List<PackFileCopy>();
        var tenantsRoot = Path.Combine(Path.GetFullPath(packStorageRoot), "tenants");
        if (!Directory.Exists(tenantsRoot)) return result;
        foreach (var tenantDirectory in new DirectoryInfo(tenantsRoot).EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (tenantDirectory.LinkTarget is not null || !Guid.TryParseExact(tenantDirectory.Name, "N", out var tenantId)) continue;
            var packages = new DirectoryInfo(Path.Combine(tenantDirectory.FullName, "packages"));
            if (!packages.Exists || packages.LinkTarget is not null) continue;
            foreach (var file in packages.EnumerateFiles("*.ecpkg").OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var stem = Path.GetFileNameWithoutExtension(file.Name);
                if (file.LinkTarget is not null || !SafeFiles.IsSha256(stem)) continue;
                var relative = $"{ManifestFiles.PacksDirectory}/tenants/{tenantDirectory.Name}/packages/{file.Name}";
                var target = SafeFiles.Resolve(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file.FullName, target, overwrite: false);
                var (hash, bytes) = await SafeFiles.HashAsync(target, ct);
                result.Add(new PackFileCopy(relative, tenantId, hash, bytes, hash == stem));
            }
        }
        return result;
    }
}
