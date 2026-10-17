using Ecp.Packs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecp.Configuration;

public sealed record RejectedPackFile(string FileName, IReadOnlyList<PackIssue> Issues);

/// <summary>
/// The packs that ship with the application as read-only <c>.ecpkg</c> files in the shipped directory. They are read and structurally verified once (hashes, manifest) and kept; whether they are trusted and
/// compatible is decided each time they are listed. A file that does not verify is never offered: it is reported (to the readiness check and the Pack Manager) with the reasons.
/// </summary>
public sealed class ShippedPackSource
{
    private readonly PackOptions _options;
    private readonly ILogger<ShippedPackSource>? _logger;
    private readonly object _gate = new();
    private IReadOnlyList<(OpenedPackage Package, string FileName)>? _packs;
    private IReadOnlyList<RejectedPackFile> _rejected = [];
    private bool _directoryFound;

    public ShippedPackSource(IOptions<PackOptions> options, ILogger<ShippedPackSource>? logger = null) { _options = options.Value; _logger = logger; }

    public string Directory => _options.ResolvedShippedDirectory;

    public IReadOnlyList<(OpenedPackage Package, string FileName)> Packs { get { Ensure(); return _packs!; } }
    public IReadOnlyList<RejectedPackFile> Rejected { get { Ensure(); return _rejected; } }
    public bool DirectoryFound { get { Ensure(); return _directoryFound; } }

    public void Refresh() { lock (_gate) { _packs = null; } Ensure(); }

    private void Ensure()
    {
        if (_packs is not null) return;
        lock (_gate)
        {
            if (_packs is not null) return;
            var packs = new List<(OpenedPackage, string)>();
            var rejected = new List<RejectedPackFile>();
            var directory = _options.ResolvedShippedDirectory;
            _directoryFound = System.IO.Directory.Exists(directory);
            if (_directoryFound)
            {
                var seen = new HashSet<(string, string)>();
                foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.ecpkg").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(file);
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > _options.Limits.MaxPackageBytes) { rejected.Add(new(name, [new PackIssue(PackCodes.TooLarge, PackSeverity.Error, "The file is larger than a package may be.", name)])); continue; }
                        var opened = PackReader.Open(File.ReadAllBytes(file), _options.Limits);
                        if (!opened.IsStructurallyValid) { rejected.Add(new(name, opened.Issues.Where(i => i.IsError).ToList())); continue; }
                        if (!seen.Add((opened.Manifest!.PackKey, opened.Manifest.Version)))
                        { rejected.Add(new(name, [new PackIssue(PackCodes.DuplicateDifferent, PackSeverity.Error, "Another shipped file already provides this pack key and version.", name)])); continue; }
                        packs.Add((opened, name));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        rejected.Add(new(name, [new PackIssue(PackCodes.NotAnArchive, PackSeverity.Error, "The file could not be read.", name)]));
                    }
                }
            }
            foreach (var r in rejected) _logger?.LogWarning("Shipped pack file {File} was rejected: {Codes}", r.FileName, string.Join(", ", r.Issues.Select(i => i.Code)));
            _rejected = rejected;
            _packs = packs;
        }
    }
}
