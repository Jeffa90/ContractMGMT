using System.IO.Compression;
using System.Text;

namespace Ecp.Tests.Common;

/// <summary>The synthetic samples that ship in the repository (migration-samples/). Tests read the real files so the documented demonstration and the tests cannot drift apart.</summary>
public static class MigrationSamples
{
    public static string Directory => Path.Combine(RepoRoot.Find(), "migration-samples");
    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(Directory, name));
    public static Stream Open(string name) => new MemoryStream(Bytes(name));

    public static byte[] ContractsCsv => Bytes("contracts-import.csv");
    public static byte[] ManifestCsv => Bytes("attachment-manifest.csv");
    public static byte[] FieldMappings => Bytes("field-mapping-profile-complete.csv");
    public static byte[] StatusLookups => Bytes("status-lookup-map.csv");

    /// <summary>The attachment ZIP built from the fixture builder: the three manifest files plus the manifest itself at the root, as the sample README describes.</summary>
    public static byte[] AttachmentZip(bool includeManifest = true)
    {
        var zip = new ZipBuilder();
        if (includeManifest) zip.Add("attachment-manifest.csv", ManifestCsv);
        zip.Add("DEMO-AGR-0001/DEMO-AGR-0001_executed.pdf", Placeholders.Pdf("DEMO-AGR-0001 executed"));
        zip.Add("DEMO-AGR-0001/DEMO-AGR-0001_schedule.docx", Placeholders.Docx("DEMO-AGR-0001 schedule"));
        zip.Add("DEMO-AGR-0002/DEMO-AGR-0002_executed.pdf", Placeholders.Pdf("DEMO-AGR-0002 executed"));
        return zip.Build();
    }
}

/// <summary>Builds ZIP archives for tests, including hostile ones. Nothing hostile is stored in the repository: it is made here, in memory.</summary>
public sealed class ZipBuilder
{
    private readonly List<(string Name, byte[] Content, int? UnixMode)> _entries = [];

    public ZipBuilder Add(string name, byte[] content) { _entries.Add((name, content, null)); return this; }
    public ZipBuilder Add(string name, string text) => Add(name, Encoding.UTF8.GetBytes(text));
    /// <summary>An entry flagged as a symbolic link in its Unix mode bits.</summary>
    public ZipBuilder AddSymlink(string name, string target) { _entries.Add((name, Encoding.UTF8.GetBytes(target), 0xA1FF)); return this; }

    public byte[] Build(CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content, mode) in _entries)
            {
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // reproducible
                if (mode is { } m) entry.ExternalAttributes = m << 16;
                using var s = entry.Open();
                s.Write(content);
            }
        return ms.ToArray();
    }
}

/// <summary>Tiny, valid, obviously synthetic files whose content signatures pass the platform's checks.</summary>
public static class Placeholders
{
    public static byte[] Pdf(string label) => Encoding.ASCII.GetBytes($"%PDF-1.4\n% Synthetic placeholder: {label}\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

    public static byte[] Docx(string label)
    {
        var zip = new ZipBuilder()
            .Add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>")
            .Add("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><document><body><p>Synthetic placeholder: {label}</p></body></document>");
        return zip.Build();
    }

    public static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
    public static byte[] Eicar() => Encoding.ASCII.GetBytes("X5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
}
