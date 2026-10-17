using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Ecp.Configuration;

/// <summary>
/// Where verified package bytes and uploads under review are kept. Implementations store bytes only; they never interpret them. A reference is a relative name chosen by the storage (never by the user), and the
/// storage must keep tenants apart.
/// </summary>
public interface IPackageStorage
{
    /// <summary>Stores verified, installed package bytes under their SHA-256. Idempotent: storing the same bytes again returns the same reference. Never overwrites different bytes.</summary>
    Task<string> SaveAsync(Guid tenantId, string sha256, byte[] bytes, CancellationToken ct = default);
    Task<byte[]?> ReadAsync(Guid tenantId, string reference, CancellationToken ct = default);
    Task SaveQuarantineAsync(Guid tenantId, Guid token, string extension, byte[] bytes, CancellationToken ct = default);
    Task<byte[]?> ReadQuarantineAsync(Guid tenantId, Guid token, string extension, CancellationToken ct = default);
    Task DeleteQuarantineAsync(Guid tenantId, Guid token, CancellationToken ct = default);
    /// <summary>Removes quarantined files older than the lifetime. Returns how many were removed.</summary>
    Task<int> PurgeQuarantineAsync(Guid tenantId, TimeSpan olderThan, CancellationToken ct = default);
}

/// <summary>File-system package storage under <see cref="PackOptions.StorageDirectory"/>, outside any served path. File names are derived from a validated hash or a GUID, never from user input.</summary>
public sealed partial class FileSystemPackageStorage(IOptions<PackOptions> options) : IPackageStorage
{
    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.NonBacktracking)] private static partial Regex Sha();
    [GeneratedRegex("^packages/[a-f0-9]{64}\\.ecpkg$", RegexOptions.NonBacktracking)] private static partial Regex Reference();

    private string TenantRoot(Guid tenantId) => Path.Combine(options.Value.ResolvedStorageDirectory, "tenants", tenantId.ToString("N"));

    public async Task<string> SaveAsync(Guid tenantId, string sha256, byte[] bytes, CancellationToken ct = default)
    {
        if (!Sha().IsMatch(sha256) || Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha256) throw new InvalidOperationException("The package bytes do not match the hash they are stored under.");
        var directory = Path.Combine(TenantRoot(tenantId), "packages");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, sha256 + ".ecpkg");
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct);
            if (!existing.AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("A different file is already stored under this hash.");
            return $"packages/{sha256}.ecpkg";
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        try { File.Move(temp, path, overwrite: false); }
        catch (IOException) { File.Delete(temp); if (!File.Exists(path)) throw; }       // a concurrent identical save won the race
        return $"packages/{sha256}.ecpkg";
    }

    public async Task<byte[]?> ReadAsync(Guid tenantId, string reference, CancellationToken ct = default)
    {
        if (!Reference().IsMatch(reference)) return null;
        var path = Path.Combine(TenantRoot(tenantId), reference.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    private string QuarantinePath(Guid tenantId, Guid token, string extension)
        => Path.Combine(TenantRoot(tenantId), "quarantine", token.ToString("N") + (extension is ".ecpkg" or ".report.json" ? extension : throw new ArgumentException("Unsupported quarantine file type.", nameof(extension))));

    public async Task SaveQuarantineAsync(Guid tenantId, Guid token, string extension, byte[] bytes, CancellationToken ct = default)
    {
        var path = QuarantinePath(tenantId, token, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    public async Task<byte[]?> ReadQuarantineAsync(Guid tenantId, Guid token, string extension, CancellationToken ct = default)
    {
        var path = QuarantinePath(tenantId, token, extension);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public Task DeleteQuarantineAsync(Guid tenantId, Guid token, CancellationToken ct = default)
    {
        foreach (var ext in new[] { ".ecpkg", ".report.json" }) { var path = QuarantinePath(tenantId, token, ext); if (File.Exists(path)) File.Delete(path); }
        return Task.CompletedTask;
    }

    public Task<int> PurgeQuarantineAsync(Guid tenantId, TimeSpan olderThan, CancellationToken ct = default)
    {
        var directory = Path.Combine(TenantRoot(tenantId), "quarantine");
        if (!Directory.Exists(directory)) return Task.FromResult(0);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > olderThan) { File.Delete(file); removed++; }
        return Task.FromResult(removed);
    }
}
