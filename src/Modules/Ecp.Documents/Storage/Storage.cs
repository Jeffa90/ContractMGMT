using System.Security.Cryptography;
using Ecp.Documents.Domain;
using Ecp.SharedKernel;

namespace Ecp.Documents.Storage;

public sealed record StoredObject(string StorageKey, string Sha256, long SizeBytes, bool AlreadyExisted);

/// <summary>Replaceable document store (ADR-0012). Every operation is tenant-bound; keys outside the tenant prefix are refused.</summary>
public interface IDocumentStorageProvider
{
    string ProviderKey { get; }
    Task<StoredObject> PutAsync(Guid tenantId, Stream content, long maxBytes, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(Guid tenantId, string storageKey, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid tenantId, string storageKey, CancellationToken ct = default);
    Task DeleteAsync(Guid tenantId, string storageKey, CancellationToken ct = default);
}

/// <summary>One stored object as the store itself sees it: whose it is, where it is, how large it is. Nothing about its content.</summary>
public sealed record StoredObjectRef(Guid TenantId, string StorageKey, long SizeBytes);

/// <summary>
/// What a document store must offer for a file-level backup (docs/BACKUP_AND_RESTORE.md, section on the document store): list every object. A provider that cannot list its objects (or whose objects another system backs up)
/// does not implement this, and the backup says plainly that the document content was not backed up by the platform. Only the local file system provider implements it today; SharePoint and Azure Blob providers do not exist.
/// </summary>
public interface IDocumentStoreInventory
{
    string ProviderKey { get; }
    /// <summary>Every object in the store. Anything that is not a regular file in the expected layout (a link, an unexpected name) is passed to <paramref name="onSkipped"/> by relative name and is never read.</summary>
    IAsyncEnumerable<StoredObjectRef> EnumerateAsync(Action<string>? onSkipped = null, CancellationToken ct = default);
}

public sealed class LocalFileSystemOptions
{
    /// <summary>Root directory. Must be on persistent storage in Production (readiness R-ST-01).</summary>
    public string RootPath { get; set; } = "";
}

/// <summary>Filesystem provider. Content is streamed to a temporary file while hashing, then moved to its content-addressed location.</summary>
public sealed class LocalFileSystemStorageProvider : IDocumentStorageProvider, IDocumentStoreInventory
{
    private readonly string _root;

    public LocalFileSystemStorageProvider(LocalFileSystemOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath)) throw new InvalidOperationException("Documents:RootPath is not configured.");
        _root = Path.GetFullPath(options.RootPath);
        Directory.CreateDirectory(_root);
    }

    public string ProviderKey => "local-fs";

    public async Task<StoredObject> PutAsync(Guid tenantId, Stream content, long maxBytes, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) throw new TenantScopeMissingException();
        var incoming = Path.Combine(_root, "_incoming");
        Directory.CreateDirectory(incoming);
        var temp = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string hash; long total = 0;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes) throw new DomainException("file_too_large", $"The file exceeds the maximum size of {maxBytes} bytes.");
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (total == 0) throw new DomainException("empty_file", "The file is empty.");

            var key = StorageKey.For(tenantId, hash);
            var target = ResolvePath(tenantId, key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) return new StoredObject(key, hash, total, AlreadyExisted: true); // dedupe within the tenant only
            File.Move(temp, target);
            return new StoredObject(key, hash, total, AlreadyExisted: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task<Stream> OpenReadAsync(Guid tenantId, string storageKey, CancellationToken ct = default)
    {
        var path = ResolvePath(tenantId, storageKey);
        if (!File.Exists(path)) throw new FileNotFoundException("The document content was not found.");
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task<bool> ExistsAsync(Guid tenantId, string storageKey, CancellationToken ct = default)
        => Task.FromResult(File.Exists(ResolvePath(tenantId, storageKey)));

    public Task DeleteAsync(Guid tenantId, string storageKey, CancellationToken ct = default)
    {
        var path = ResolvePath(tenantId, storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredObjectRef> EnumerateAsync(Action<string>? onSkipped = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var top = Path.Combine(_root, "t");
        if (!Directory.Exists(top)) yield break;
        foreach (var tenantDirectory in new DirectoryInfo(top).EnumerateFileSystemInfos().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (tenantDirectory is not DirectoryInfo td || td.LinkTarget is not null || !Guid.TryParseExact(td.Name, "N", out var tenantId)) { onSkipped?.Invoke("t/" + tenantDirectory.Name); continue; }
            foreach (var prefixDirectory in td.EnumerateFileSystemInfos().OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                if (prefixDirectory is not DirectoryInfo pd || pd.LinkTarget is not null) { onSkipped?.Invoke($"t/{td.Name}/{prefixDirectory.Name}"); continue; }
                foreach (var entry in pd.EnumerateFileSystemInfos().OrderBy(f => f.Name, StringComparer.Ordinal))
                {
                    var key = $"t/{td.Name}/{pd.Name}/{entry.Name}";
                    if (entry is not FileInfo file || file.LinkTarget is not null || !StorageKey.IsValidFor(tenantId, key) || !key.StartsWith($"t/{td.Name}/{entry.Name[..2]}/", StringComparison.Ordinal)) { onSkipped?.Invoke(key); continue; }
                    yield return new StoredObjectRef(tenantId, key, file.Length);
                }
            }
        }
    }

    /// <summary>Validates the key against the tenant and the root; any key that is not exactly a tenant content address is refused.</summary>
    private string ResolvePath(Guid tenantId, string storageKey)
    {
        if (tenantId == Guid.Empty) throw new TenantScopeMissingException();
        if (!StorageKey.IsValidFor(tenantId, storageKey))
            throw new DomainException("invalid_storage_key", "The storage key is not valid for this tenant.");
        var path = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new DomainException("invalid_storage_key", "The storage key resolves outside the [[document|lower]] store.");
        return path;
    }
}
