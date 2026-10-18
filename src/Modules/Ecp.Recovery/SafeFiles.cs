using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ecp.SharedKernel;

namespace Ecp.Recovery;

/// <summary>
/// File handling for anything read from, or written into, a backup directory. A backup is untrusted input at restore time: a path from a manifest is resolved against the backup root, never followed through a link,
/// and refused if it leaves the root (docs/THREAT_MODEL.md, backup and restore).
/// </summary>
public static partial class SafeFiles
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}(/[A-Za-z0-9][A-Za-z0-9._-]{0,127}){0,7}$", RegexOptions.NonBacktracking)] private static partial Regex RelativeName();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.NonBacktracking)] private static partial Regex Sha256Hex();
    [GeneratedRegex("^bk-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$", RegexOptions.NonBacktracking)] private static partial Regex BackupIdPattern();
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$", RegexOptions.NonBacktracking)] private static partial Regex DatabaseNamePattern();

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase) { "master", "model", "msdb", "tempdb", "distribution" };

    public static bool IsSha256(string? value) => value is not null && Sha256Hex().IsMatch(value);
    public static bool IsBackupId(string? value) => value is not null && BackupIdPattern().IsMatch(value);
    public static bool IsRelativeName(string? value) => value is not null && RelativeName().IsMatch(value) && !value.Split('/').Any(s => s is "." or "..");

    /// <summary>A database name this tool is willing to create: letters, digits and underscores, not a system database.</summary>
    public static bool IsTargetDatabaseName(string? value) => value is not null && DatabaseNamePattern().IsMatch(value) && !SystemDatabases.Contains(value);

    public static string NewBackupId(DateTime utcNow)
        => $"bk-{utcNow:yyyyMMdd'T'HHmmss'Z'}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

    /// <summary>The full path of a relative name inside <paramref name="root"/>. Throws if the name is not a plain relative name, or if the path (or any directory on the way) is a link.</summary>
    public static string Resolve(string root, string relative)
    {
        if (!IsRelativeName(relative)) throw new DomainException("unsafe_path", "A backup file name is not a plain relative name.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new DomainException("unsafe_path", "A backup file name resolves outside the backup directory.");
        RejectLinks(fullRoot, full);
        return full;
    }

    /// <summary>The path of a single named directory directly under <paramref name="root"/>.</summary>
    public static string ChildDirectory(string root, string name)
    {
        if (!IsRelativeName(name) || name.Contains('/')) throw new DomainException("unsafe_path", "A backup directory name is not a plain name.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.Combine(fullRoot, name);
        RejectLinks(fullRoot, full + Path.DirectorySeparatorChar + "x");
        return full;
    }

    /// <summary>Throws if <paramref name="path"/>, or any directory between <paramref name="root"/> and it, is a symbolic link or reparse point.</summary>
    public static void RejectLinks(string root, string path)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
        var stop = new DirectoryInfo(root).FullName.TrimEnd(Path.DirectorySeparatorChar);
        while (current is not null && current.FullName.TrimEnd(Path.DirectorySeparatorChar).Length >= stop.Length)
        {
            if (current.Exists && current.LinkTarget is not null) throw new DomainException("unsafe_path", "A backup directory is a link and is not followed.");
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.Ordinal)) break;
            current = current.Parent;
        }
        if (File.Exists(path) && new FileInfo(path).LinkTarget is not null) throw new DomainException("unsafe_path", "A backup file is a link and is not followed.");
    }

    public static async Task<(string Sha256, long Bytes)> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 16];
        long total = 0; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) { sha.AppendData(buffer, 0, read); total += read; }
        return (Convert.ToHexStringLower(sha.GetHashAndReset()), total);
    }

    public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Creates a file that must not exist yet. A backup file is never overwritten.</summary>
    public static FileStream CreateNew(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);

    public static void Private(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            // A backup folder can be entered (not listed) by other accounts: the database server writes into it under its own service account.
            if (Directory.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            else if (File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { /* best effort: the operator's volume permissions are the real control */ }
    }

    /// <summary>A directory that is not inside a path the web server serves. The check is a guard against a misconfiguration, not a substitute for storage permissions.</summary>
    public static bool IsOutsideWebRoot(string directory, IEnumerable<string> servedRoots)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var served in servedRoots)
        {
            var s = Path.GetFullPath(served).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(s, StringComparison.Ordinal) || s.StartsWith(full, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
