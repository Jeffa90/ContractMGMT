using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Ecp.DataMigration.Formats;

/// <summary>Limits for an attachment archive (MIGRATION_SUPPLEMENT section 9). Defaults are open pending Q-13 and come from configuration.</summary>
public sealed class ArchiveLimits
{
    public int MaxEntries { get; set; } = 20_000;
    public long MaxTotalBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public long MaxEntryBytes { get; set; } = 500L * 1024 * 1024;
    public int MaxCompressionRatio { get; set; } = 100;
    /// <summary>Ratio checks apply only above this size; a small, highly compressible file is not a bomb.</summary>
    public long RatioCheckFloorBytes { get; set; } = 10L * 1024 * 1024;
    public ISet<string> AllowedExtensions { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "csv", "rtf", "png", "jpg", "jpeg", "tif", "tiff", "msg", "eml", "odt", "ods", "odp" };
}

/// <summary>Path rules for archive entries and manifest paths: relative, forward slashes, no traversal, no device names (MIG-F001).</summary>
public static partial class ArchivePaths
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>The normalised path, or a rejection (MIG-F001) with the reason.</summary>
    public static (string? Path, string? Reason) Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, "The path is empty.");
        if (raw.Any(char.IsControl)) return (null, "The path contains control characters.");
        var p = raw.Replace('\\', '/');
        if (p.StartsWith("//", StringComparison.Ordinal)) return (null, "A network (UNC) path is not allowed.");
        if (p.StartsWith('/')) return (null, "An absolute path is not allowed.");
        if (DrivePrefix().IsMatch(p)) return (null, "A drive path is not allowed.");
        var segments = new List<string>();
        foreach (var segment in p.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") return (null, "A path that climbs out of its folder (..) is not allowed.");
            if (segment.Contains(':')) return (null, "A path segment contains a colon (alternate data stream or drive).");
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return (null, "A path segment ends with a dot or a space.");
            if (segment.Length > 255) return (null, "A path segment is too long.");
            var stem = segment.Contains('.') ? segment[..segment.IndexOf('.')] : segment;
            if (Reserved.Contains(stem)) return (null, $"'{segment}' is a reserved device name.");
            segments.Add(segment);
        }
        if (segments.Count == 0) return (null, "The path is empty.");
        var joined = string.Join('/', segments);
        return joined.Length > 500 ? (null, "The path is too long.") : (joined, null);
    }

    public static string Extension(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string? ParentFolder(string path)
    {
        var i = path.LastIndexOf('/');
        if (i <= 0) return null;
        var parent = path[..i];
        return parent[(parent.LastIndexOf('/') + 1)..];
    }

    [GeneratedRegex("^[A-Za-z]:", RegexOptions.NonBacktracking)]
    private static partial Regex DrivePrefix();
}

/// <summary>Content-signature checks (MIG-F003): the bytes must look like the type the extension declares.</summary>
public static class FileSignatures
{
    public const int HeaderBytes = 4096;

    public static string MimeFor(string extension) => extension switch
    {
        "pdf" => "application/pdf",
        "doc" => "application/msword",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xls" => "application/vnd.ms-excel",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "ppt" => "application/vnd.ms-powerpoint",
        "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "txt" => "text/plain",
        "csv" => "text/csv",
        "rtf" => "application/rtf",
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "tif" or "tiff" => "image/tiff",
        "msg" => "application/vnd.ms-outlook",
        "eml" => "message/rfc822",
        "odt" => "application/vnd.oasis.opendocument.text",
        "ods" => "application/vnd.oasis.opendocument.spreadsheet",
        "odp" => "application/vnd.oasis.opendocument.presentation",
        _ => "application/octet-stream"
    };

    public static bool Matches(string extension, ReadOnlySpan<byte> head)
    {
        static bool Starts(ReadOnlySpan<byte> h, params byte[] prefix) => h.Length >= prefix.Length && h[..prefix.Length].SequenceEqual(prefix);
        switch (extension)
        {
            case "pdf":
                return h(head, "%PDF-");
            case "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp":
                return Starts(head, 0x50, 0x4B, 0x03, 0x04);
            case "doc" or "xls" or "ppt" or "msg":
                return Starts(head, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1);
            case "png":
                return Starts(head, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
            case "jpg" or "jpeg":
                return Starts(head, 0xFF, 0xD8, 0xFF);
            case "tif" or "tiff":
                return Starts(head, 0x49, 0x49, 0x2A, 0x00) || Starts(head, 0x4D, 0x4D, 0x00, 0x2A);
            case "rtf":
                return h(head, "{\\rtf");
            case "txt" or "csv" or "eml":
                return !head.Contains((byte)0); // text: a NUL byte means it is binary
            default:
                return false;
        }

        static bool h(ReadOnlySpan<byte> head, string text)
        {
            var prefix = System.Text.Encoding.ASCII.GetBytes(text);
            var window = head.Length > 1024 ? head[..1024] : head; // a PDF may have a little leading data
            return window.IndexOf(prefix) >= 0 && (text != "{\\rtf" || head.StartsWith(prefix));
        }
    }
}

/// <summary>An entry in an archive, checked but not yet read. Reading is always through <see cref="Open"/>, which is bounded.</summary>
public sealed class ArchiveEntry
{
    private readonly ZipArchiveEntry? _entry;
    private readonly ByteBudget _budget;
    private readonly ArchiveLimits _limits;

    internal ArchiveEntry(ZipArchiveEntry? entry, string rawName, string? path, long length, long compressed, string? rejectCode, string? rejectMessage, ByteBudget budget, ArchiveLimits limits)
    {
        _entry = entry; RawName = rawName; Path = path; Length = length; CompressedLength = compressed; RejectCode = rejectCode; RejectMessage = rejectMessage; _budget = budget; _limits = limits;
    }

    public string RawName { get; }
    /// <summary>Normalised path; null when the entry's name was rejected.</summary>
    public string? Path { get; }
    public string FileName => Path is null ? RawName : ArchivePaths.FileName(Path);
    public string Extension => Path is null ? "" : ArchivePaths.Extension(Path);
    public long Length { get; }
    public long CompressedLength { get; }
    public string? RejectCode { get; }
    public string? RejectMessage { get; }
    public bool IsRejected => RejectCode is not null;

    /// <summary>Opens the entry for reading, never yielding more than it declared nor more than the archive's remaining budget (decompression-bomb defence while streaming).</summary>
    public Stream Open()
    {
        if (_entry is null) throw new InvalidOperationException("A rejected entry cannot be opened.");
        Stream raw;
        try { raw = _entry.Open(); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        { throw new SourceFormatException("MIG-F005", "The entry is encrypted or corrupt and could not be opened."); }
        return new BoundedStream(raw, Math.Min(Length, _limits.MaxEntryBytes), _budget);
    }
}

internal sealed class ByteBudget(long total)
{
    private long _remaining = total;
    public void Take(int count)
    {
        if (Interlocked.Add(ref _remaining, -count) < 0) throw new SourceFormatException("MIG-F006", "The archive expands to more than the permitted total size.");
    }
}

/// <summary>Counts what is really produced, so an archive that lies about its sizes still cannot exceed the limits.</summary>
internal sealed class BoundedStream(Stream inner, long maxBytes, ByteBudget budget) : Stream
{
    private long _read;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => Account(inner.ReadSafe(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n;
        try { n = await inner.ReadAsync(buffer, ct); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { throw new SourceFormatException("MIG-F005", "The entry is encrypted or corrupt."); }
        return Account(n);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private int Account(int n)
    {
        _read += n;
        if (_read > maxBytes) throw new SourceFormatException("MIG-F006", "An archive entry produced more data than it declared or than the entry limit allows.");
        budget.Take(n);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}

internal static class StreamExtensions
{
    public static int ReadSafe(this Stream s, byte[] buffer, int offset, int count)
    {
        try { return s.Read(buffer, offset, count); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { throw new SourceFormatException("MIG-F005", "The entry is encrypted or corrupt."); }
    }
}

/// <summary>An opened archive with every entry checked against the path, count, size and extension rules. Nothing is extracted to disk.</summary>
public sealed class ArchiveListing : IDisposable
{
    private readonly ZipArchive _zip;
    internal ArchiveListing(ZipArchive zip, IReadOnlyList<ArchiveEntry> entries, int directories) { _zip = zip; Entries = entries; Directories = directories; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }
    public int Directories { get; }
    public void Dispose() => _zip.Dispose();
}

public static class ZipIntake
{
    /// <summary>
    /// Opens an archive and checks every entry. Archive-level violations (too many entries, too large, a decompression bomb, not a ZIP) stop the intake with MIG-F006 or
    /// MIG-F005. Entry-level violations (unsafe name MIG-F001, extension MIG-F002, empty MIG-F004) reject only that entry.
    /// </summary>
    public static ArchiveListing Open(Stream zipStream, ArchiveLimits limits)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new SourceFormatException("MIG-F005", "The archive is corrupt or is not a ZIP file."); }
        try
        {
            if (zip.Entries.Count > limits.MaxEntries) throw new SourceFormatException("MIG-F006", $"The archive has more than {limits.MaxEntries} entries.");
            long declared = 0;
            foreach (var e in zip.Entries)
            {
                declared += e.Length;
                if (declared > limits.MaxTotalBytes) throw new SourceFormatException("MIG-F006", "The archive expands to more than the permitted total size.");
                if (e.Length > limits.RatioCheckFloorBytes && e.CompressedLength > 0 && e.Length / e.CompressedLength > limits.MaxCompressionRatio)
                    throw new SourceFormatException("MIG-F006", "The archive contains an entry with an unsafe compression ratio (decompression bomb) and was refused.");
            }

            var budget = new ByteBudget(limits.MaxTotalBytes);
            var entries = new List<ArchiveEntry>(); var directories = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries.OrderBy(x => x.FullName, StringComparer.Ordinal))
            {
                var raw = e.FullName;
                if (raw.EndsWith('/') || raw.EndsWith('\\')) { directories++; continue; }
                if (IsSymbolicLink(e)) { entries.Add(new ArchiveEntry(null, raw, null, e.Length, e.CompressedLength, "MIG-F001", "A symbolic link is not allowed in an archive.", budget, limits)); continue; }
                var (path, reason) = ArchivePaths.Normalise(raw);
                if (path is null) { entries.Add(new ArchiveEntry(null, raw, null, e.Length, e.CompressedLength, "MIG-F001", reason, budget, limits)); continue; }
                if (!seen.Add(path)) { entries.Add(new ArchiveEntry(null, raw, null, e.Length, e.CompressedLength, "MIG-F018", $"The archive holds more than one entry named '{path}'.", budget, limits)); continue; }
                var ext = ArchivePaths.Extension(path);
                if (ext.Length == 0 || !limits.AllowedExtensions.Contains(ext))
                { entries.Add(new ArchiveEntry(null, raw, path, e.Length, e.CompressedLength, "MIG-F002", $"The file type '.{ext}' is not on the allowed list.", budget, limits)); continue; }
                if (e.Length == 0) { entries.Add(new ArchiveEntry(null, raw, path, 0, e.CompressedLength, "MIG-F004", "The file is empty.", budget, limits)); continue; }
                if (e.Length > limits.MaxEntryBytes) { entries.Add(new ArchiveEntry(null, raw, path, e.Length, e.CompressedLength, "MIG-F006", "The file is larger than the permitted size.", budget, limits)); continue; }
                entries.Add(new ArchiveEntry(e, raw, path, e.Length, e.CompressedLength, null, null, budget, limits));
            }
            return new ArchiveListing(zip, entries, directories);
        }
        catch { zip.Dispose(); throw; }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry e)
    {
        // Unix mode bits live in the high 16 bits of the external attributes when the archive was made on a Unix host (S_IFLNK = 0xA000).
        var mode = (e.ExternalAttributes >> 16) & 0xF000;
        return mode == 0xA000;
    }
}
