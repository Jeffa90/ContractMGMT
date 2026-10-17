using System.IO.Compression;
using System.Text;

namespace Ecp.Packs;

/// <summary>
/// Writes a package as a ZIP whose bytes depend only on its contents: entries in ordinal path order, stored (not compressed), a fixed timestamp, fixed attributes, no extra fields and no comment. The same
/// inputs therefore produce the same bytes on any machine and operating system, which is what makes a build reproducible and its hash meaningful.
/// </summary>
public static class PackZipWriter
{
    private const ushort VersionNeeded = 20;
    private const ushort VersionMadeBy = (3 << 8) | 20;      // Unix, ZIP 2.0
    private const ushort DosTime = 0;                        // 00:00:00
    private const ushort DosDate = (0 << 9) | (1 << 5) | 1;  // 1980-01-01
    private const uint RegularFile0644 = 0x81A40000;

    public static byte[] Write(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        var central = new List<(string Name, uint Crc, uint Size, uint Offset)>();
        foreach (var (name, data) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var nameBytes = Encoding.ASCII.GetBytes(name);
            var crc = Crc32.Of(data);
            central.Add((name, crc, (uint)data.Length, (uint)ms.Position));
            w.Write(0x04034b50u); w.Write(VersionNeeded); w.Write((ushort)0); w.Write((ushort)0); w.Write(DosTime); w.Write(DosDate);
            w.Write(crc); w.Write((uint)data.Length); w.Write((uint)data.Length); w.Write((ushort)nameBytes.Length); w.Write((ushort)0);
            w.Write(nameBytes); w.Write(data);
        }
        var directoryStart = (uint)ms.Position;
        foreach (var (name, crc, size, offset) in central)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name);
            w.Write(0x02014b50u); w.Write(VersionMadeBy); w.Write(VersionNeeded); w.Write((ushort)0); w.Write((ushort)0); w.Write(DosTime); w.Write(DosDate);
            w.Write(crc); w.Write(size); w.Write(size); w.Write((ushort)nameBytes.Length); w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)0); w.Write((ushort)0); w.Write(RegularFile0644); w.Write(offset); w.Write(nameBytes);
        }
        var directorySize = (uint)ms.Position - directoryStart;
        w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)central.Count); w.Write((ushort)central.Count);
        w.Write(directorySize); w.Write(directoryStart); w.Write((ushort)0);
        w.Flush();
        return ms.ToArray();
    }
}

public sealed record PackZipResult(IReadOnlyDictionary<string, byte[]> Entries, IReadOnlyList<PackIssue> Issues)
{
    public bool IsSafe => Entries.Count > 0 && Issues.All(i => !i.IsError);
}

/// <summary>
/// Reads a package archive defensively. The central directory is the only index trusted; every entry is checked against it and against its local header; the archive is refused (not repaired) if it uses
/// anything a package does not need: encryption, ZIP64, multi-disk, links, directory entries, unknown compression, overlapping entries, duplicate or unsafe names, or sizes that disagree with the data.
/// Nothing is written to disk. Expansion is bounded by <see cref="PackLimits"/> while reading, not just by the sizes the archive declares.
/// </summary>
public static class PackZipReader
{
    private sealed record Entry(string Name, ushort Method, uint Crc, uint CompressedSize, uint Size, uint LocalOffset, ushort Flags);

    public static PackZipResult Read(ReadOnlySpan<byte> bytes, PackLimits limits)
    {
        var issues = new List<PackIssue>();
        PackZipResult Fail(string code, string message, string? remediation = null, string? entry = null)
        {
            issues.Add(new PackIssue(code, PackSeverity.Error, message, entry, remediation));
            return new PackZipResult(new Dictionary<string, byte[]>(), issues);
        }

        if (bytes.Length > limits.MaxPackageBytes) return Fail(PackCodes.TooLarge, $"The package is larger than the {limits.MaxPackageBytes / (1024 * 1024)} MB limit.", "Check that this is a solution pack and not another kind of file.");
        if (bytes.Length < 22) return Fail(PackCodes.NotAnArchive, "The file is not a package archive.", "Upload a .ecpkg file built with the pack tool.");

        // End of central directory: the last 22 bytes, with no archive comment (a comment can hide data).
        var eocd = bytes.Length - 22;
        if (BitConverter.ToUInt32(bytes[eocd..]) != 0x06054b50u) return Fail(PackCodes.NotAnArchive, "The file is not a package archive (no end-of-archive record, or it has a comment).", "Rebuild the package with the pack tool.");
        var disk = BitConverter.ToUInt16(bytes[(eocd + 4)..]);
        var dirDisk = BitConverter.ToUInt16(bytes[(eocd + 6)..]);
        var entriesOnDisk = BitConverter.ToUInt16(bytes[(eocd + 8)..]);
        var total = BitConverter.ToUInt16(bytes[(eocd + 10)..]);
        var dirSize = BitConverter.ToUInt32(bytes[(eocd + 12)..]);
        var dirOffset = BitConverter.ToUInt32(bytes[(eocd + 16)..]);
        var commentLength = BitConverter.ToUInt16(bytes[(eocd + 20)..]);
        if (disk != 0 || dirDisk != 0 || entriesOnDisk != total) return Fail(PackCodes.UnsafeFeature, "Multi-part archives are not allowed.", "Build a single-file package with the pack tool.");
        if (commentLength != 0) return Fail(PackCodes.UnsafeFeature, "An archive comment is not allowed.", "Rebuild the package with the pack tool.");
        if (total == 0xFFFF || dirSize == 0xFFFFFFFFu || dirOffset == 0xFFFFFFFFu) return Fail(PackCodes.UnsafeFeature, "ZIP64 archives are not allowed.", "A package is small; rebuild it with the pack tool.");
        if (total == 0) return Fail(PackCodes.NotAnArchive, "The archive is empty.", "Rebuild the package with the pack tool.");
        if (total > limits.MaxEntries) return Fail(PackCodes.TooManyEntries, $"The archive has more than {limits.MaxEntries} entries.", "A package holds a manifest, a signature and a few content documents.");
        if ((long)dirOffset + dirSize != eocd) return Fail(PackCodes.NotAnArchive, "The archive directory is inconsistent.", "Rebuild the package with the pack tool.");

        // Central directory.
        var entries = new List<Entry>();
        var pos = (int)dirOffset;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < total; i++)
        {
            if (pos + 46 > eocd || BitConverter.ToUInt32(bytes[pos..]) != 0x02014b50u) return Fail(PackCodes.NotAnArchive, "The archive directory is damaged.", "Rebuild the package with the pack tool.");
            var madeBy = BitConverter.ToUInt16(bytes[(pos + 4)..]);
            var flags = BitConverter.ToUInt16(bytes[(pos + 8)..]);
            var method = BitConverter.ToUInt16(bytes[(pos + 10)..]);
            var crc = BitConverter.ToUInt32(bytes[(pos + 16)..]);
            var csize = BitConverter.ToUInt32(bytes[(pos + 20)..]);
            var usize = BitConverter.ToUInt32(bytes[(pos + 24)..]);
            var nameLength = BitConverter.ToUInt16(bytes[(pos + 28)..]);
            var extraLength = BitConverter.ToUInt16(bytes[(pos + 30)..]);
            var commentLen = BitConverter.ToUInt16(bytes[(pos + 32)..]);
            var diskStart = BitConverter.ToUInt16(bytes[(pos + 34)..]);
            var attributes = BitConverter.ToUInt32(bytes[(pos + 38)..]);
            var localOffset = BitConverter.ToUInt32(bytes[(pos + 42)..]);
            if (pos + 46 + nameLength + extraLength + commentLen > eocd) return Fail(PackCodes.NotAnArchive, "The archive directory is damaged.", "Rebuild the package with the pack tool.");
            var nameBytes = bytes.Slice(pos + 46, nameLength);
            pos += 46 + nameLength + extraLength + commentLen;

            foreach (var b in nameBytes) if (b is < 0x21 or > 0x7E) return Fail(PackCodes.UnsafePath, "An entry name contains a character that is not allowed.", "Entry names use letters, digits, '.', '_', '-' and '/' only.");
            var name = Encoding.ASCII.GetString(nameBytes);
            if (name.EndsWith('/')) return Fail(PackCodes.UnsafeFeature, "Directory entries are not allowed.", "Build the package with the pack tool, which writes files only.", name);
            var (_, reason) = PackPaths.Validate(name, limits);
            if (reason is not null) return Fail(PackCodes.UnsafePath, $"The entry name is not allowed: {reason}", "Rename or remove the entry.", name.Length > 60 ? name[..60] : name);
            if (!names.Add(name)) return Fail(PackCodes.DuplicatePath, "The archive contains the same entry name more than once.", "Rebuild the package with the pack tool.", name);
            if ((flags & 0x0001) != 0 || (flags & 0x0040) != 0) return Fail(PackCodes.UnsafeFeature, "Encrypted entries are not allowed.", "Packages are signed, not encrypted.", name);
            if (method is not (0 or 8)) return Fail(PackCodes.UnsafeFeature, "The entry uses a compression method that is not allowed.", "Rebuild the package with the pack tool.", name);
            if (csize == 0xFFFFFFFFu || usize == 0xFFFFFFFFu || localOffset == 0xFFFFFFFFu || diskStart != 0) return Fail(PackCodes.UnsafeFeature, "ZIP64 or multi-part entries are not allowed.", "Rebuild the package with the pack tool.", name);
            var platform = madeBy >> 8;
            if (platform == 3)
            {
                var mode = (attributes >> 16) & 0xF000;
                if (mode != 0 && mode != 0x8000) return Fail(PackCodes.UnsafeFeature, "Links and special files are not allowed.", "Packages contain regular files only.", name);
            }
            if ((attributes & 0x10) != 0) return Fail(PackCodes.UnsafeFeature, "Directory entries are not allowed.", "Packages contain regular files only.", name);
            if (method == 0 && csize != usize) return Fail(PackCodes.EntryCorrupt, "An entry's declared sizes disagree.", "The package is damaged; obtain it again.", name);
            if (usize > limits.MaxEntryBytes) return Fail(PackCodes.EntryTooLarge, $"An entry is larger than the {limits.MaxEntryBytes / (1024 * 1024)} MB limit.", "A package holds small JSON documents.", name);
            entries.Add(new Entry(name, method, crc, csize, usize, localOffset, flags));
        }
        if (pos != eocd) return Fail(PackCodes.NotAnArchive, "The archive directory is inconsistent.", "Rebuild the package with the pack tool.");

        long expanded = 0;
        foreach (var e in entries) expanded += e.Size;
        if (expanded > limits.MaxTotalExpandedBytes) return Fail(PackCodes.ExpandedTooLarge, $"The package would expand to more than {limits.MaxTotalExpandedBytes / (1024 * 1024)} MB.", "A package holds small JSON documents.");
        foreach (var e in entries)
            if (e.Size > limits.RatioFloorBytes && e.CompressedSize > 0 && e.Size / (double)e.CompressedSize > limits.MaxCompressionRatio)
                return Fail(PackCodes.CompressionRatio, "An entry is compressed far more than a real document would be.", "This looks like a compression bomb; do not install it.", e.Name);

        // Local entries: positions, no overlap, header agrees with the directory, then the data.
        var ranges = new List<(long Start, long End)>();
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var start = (long)e.LocalOffset;
            if (start + 30 > dirOffset || BitConverter.ToUInt32(bytes[(int)start..]) != 0x04034b50u) return Fail(PackCodes.NotAnArchive, "An entry's header is damaged.", "Rebuild the package with the pack tool.", e.Name);
            var localMethod = BitConverter.ToUInt16(bytes[(int)(start + 8)..]);
            var localNameLength = BitConverter.ToUInt16(bytes[(int)(start + 26)..]);
            var localExtraLength = BitConverter.ToUInt16(bytes[(int)(start + 28)..]);
            var dataStart = start + 30 + localNameLength + localExtraLength;
            var dataEnd = dataStart + e.CompressedSize;
            if (dataEnd > dirOffset) return Fail(PackCodes.NotAnArchive, "An entry runs past the end of the archive.", "Rebuild the package with the pack tool.", e.Name);
            if (localMethod != e.Method || Encoding.ASCII.GetString(bytes.Slice((int)start + 30, localNameLength)) != e.Name)
                return Fail(PackCodes.EntryCorrupt, "An entry's header disagrees with the archive directory.", "The package is damaged or has been tampered with.", e.Name);
            ranges.Add((start, dataEnd));

            byte[] data;
            var raw = bytes.Slice((int)dataStart, (int)e.CompressedSize);
            if (e.Method == 0) data = raw.ToArray();
            else
            {
                using var source = new MemoryStream(raw.ToArray());
                using var inflate = new DeflateStream(source, CompressionMode.Decompress);
                using var sink = new MemoryStream();
                var buffer = new byte[8192];
                long read = 0;
                try
                {
                    int n;
                    while ((n = inflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        read += n;
                        if (read > e.Size || read > limits.MaxEntryBytes) return Fail(PackCodes.EntryCorrupt, "An entry expands to more than it declares.", "The package is damaged or hostile; do not install it.", e.Name);
                        sink.Write(buffer, 0, n);
                    }
                }
                catch (InvalidDataException) { return Fail(PackCodes.EntryCorrupt, "An entry cannot be decompressed.", "The package is damaged; obtain it again.", e.Name); }
                data = sink.ToArray();
            }
            if (data.Length != e.Size) return Fail(PackCodes.EntryCorrupt, "An entry's size differs from the archive directory.", "The package is damaged or has been tampered with.", e.Name);
            if (Crc32.Of(data) != e.Crc) return Fail(PackCodes.EntryCorrupt, "An entry's checksum differs from the archive directory.", "The package is damaged or has been tampered with.", e.Name);
            result[e.Name] = data;
        }
        ranges.Sort();
        for (var i = 1; i < ranges.Count; i++)
            if (ranges[i].Start < ranges[i - 1].End) return Fail(PackCodes.UnsafeFeature, "Entries overlap inside the archive.", "This is a malformed archive; do not install it.");
        return new PackZipResult(result, issues);
    }
}
