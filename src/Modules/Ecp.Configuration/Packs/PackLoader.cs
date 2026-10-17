using System.Text.Json;
using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Packs;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ecp.Configuration;

/// <summary>The configuration kinds this host can install from a pack: the platform's packable kinds that a module has actually registered.</summary>
internal sealed class PackKindRegistry(IServiceProvider services)
{
    // Resolved on first use, not at construction: the solution-pack kind itself depends on the loader, which depends on this registry.
    private IReadOnlyList<IConfigurationKind>? _kinds;
    private IReadOnlyList<IConfigurationKind> Kinds => _kinds ??= services.GetServices<IConfigurationKind>().ToList();
    private IReadOnlySet<string>? _known;
    public IReadOnlySet<string> Known => _known ??= PackKinds.Packable.Where(k => Kinds.Any(x => x.Kind == k)).ToHashSet(StringComparer.Ordinal);
    public IConfigurationKind? Find(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind);
}

/// <summary>Builds the trust policy for the current tenant: platform keys first, then the tenant's own registered keys.</summary>
internal sealed class PackTrustEvaluator(PlatformKeyResolver platform, TenantKeyResolver tenantKeys, IOptions<PackOptions> options, IClock clock)
{
    private readonly PackTrustPolicy _policy = new(new CompositeKeyResolver([platform, tenantKeys]));
    public IPackKeyResolver Keys { get; } = new CompositeKeyResolver([platform, tenantKeys]);

    public Task<TrustResult> EvaluateAsync(OpenedPackage package, CancellationToken ct)
        => _policy.EvaluateAsync(package.Manifest!, package.CanonicalManifest, package.SignatureFile,
            new TrustContext(options.Value.IsProduction, options.Value.AllowUnsignedSyntheticPacks, clock.UtcNow), options.Value.Limits, ct);
}

/// <summary>
/// The one place packs are read. It knows two sources, the shipped read-only files and the tenant's installed packages, and treats them alike: it verifies, judges trust and compatibility afresh, and returns
/// documents by pack key, version and configuration kind. It has no knowledge of any particular business or pack.
/// </summary>
internal sealed class PackLoader(ShippedPackSource shipped, PackPackageCache cache, IPackageStorage storage, ConfigurationDbContext db, ITenantContext tenant, PackKindRegistry kinds,
    PackTrustEvaluator trust, IOptions<PackOptions> options) : IPackLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PackCatalogueEntry>> ListAsync(CancellationToken ct = default)
    {
        var entries = new List<PackCatalogueEntry>();
        var shippedKeys = new HashSet<(string, string)>();
        foreach (var (package, fileName) in shipped.Packs)
        {
            shippedKeys.Add((package.Manifest!.PackKey, package.Manifest.Version));
            entries.Add(await DescribeAsync(package, PackSourceTypes.Shipped, retired: false, unavailable: null, installedUtc: null, uploadedBy: null, fileName, ct));
        }
        if (tenant.TenantId is { } tenantId)
        {
            var rows = await db.InstalledPacks.AsNoTracking().OrderBy(r => r.PackKey).ThenBy(r => r.Version).ToListAsync(ct);
            foreach (var row in rows)
            {
                if (shippedKeys.Contains((row.PackKey, row.Version))) continue;      // a shipped pack of the same key and version wins; the duplicate is reported by diagnostics
                var (package, problem) = await LoadTenantPackageAsync(tenantId, row, ct);
                if (package is null) entries.Add(Unavailable(row, problem!));
                else entries.Add(await DescribeAsync(package, PackSourceTypes.TenantUploaded, row.IsRetired, null, row.InstalledUtc, row.UploadedBy, null, ct));
            }
        }
        return entries;
    }

    public async Task<PackCatalogueEntry?> FindAsync(string packKey, string? version = null, bool usableOnly = true, CancellationToken ct = default)
    {
        var matches = (await ListAsync(ct)).Where(e => string.Equals(e.PackKey, packKey, StringComparison.Ordinal)).ToList();
        if (version is not null) return matches.FirstOrDefault(e => e.Version == version && (!usableOnly || e.IsUsable));
        var candidates = usableOnly ? matches.Where(e => e.IsUsable) : matches;
        return candidates.OrderByDescending(e => SemVer.TryParse(e.Version, out var v) ? v : new SemVer(0, 0, 0, null)).FirstOrDefault();
    }

    public async Task<string?> GetDocumentAsync(string packKey, string? version, string kind, CancellationToken ct = default)
    {
        var entry = await FindAsync(packKey, version, usableOnly: version is null, ct);
        if (entry is null) return null;
        var opened = await OpenAsync(entry.PackKey, entry.Version, ct);
        return opened?.Package.Document(kind);
    }

    public async Task<(PackCatalogueEntry Entry, OpenedPackage Package)?> OpenAsync(string packKey, string version, CancellationToken ct = default)
    {
        var entry = await FindAsync(packKey, version, usableOnly: false, ct);
        if (entry is null || entry.Unavailable) return null;
        if (entry.IsShipped)
        {
            var package = shipped.Packs.FirstOrDefault(p => p.Package.Sha256 == entry.PackageSha256).Package;
            return package is null ? null : (entry, package);
        }
        if (tenant.TenantId is not { } tenantId) return null;
        var row = await db.InstalledPacks.AsNoTracking().FirstOrDefaultAsync(r => r.PackKey == packKey && r.Version == version, ct);
        if (row is null) return null;
        var (loaded, _) = await LoadTenantPackageAsync(tenantId, row, ct);
        return loaded is null ? null : (entry, loaded);
    }

    private async Task<(OpenedPackage? Package, string? Problem)> LoadTenantPackageAsync(Guid tenantId, InstalledPack row, CancellationToken ct)
    {
        if (cache.TryGet(tenantId, row.PackageSha256, out var cached)) return (cached, null);
        byte[]? bytes;
        try { bytes = await storage.ReadAsync(tenantId, row.StorageReference, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "The stored package could not be read."); }
        if (bytes is null) return (null, "The stored package file is missing.");
        // The stored bytes must still be the bytes that were verified when the pack was installed.
        var opened = PackReader.Open(bytes, options.Value.Limits);
        if (!string.Equals(opened.Sha256, row.PackageSha256, StringComparison.Ordinal)) return (null, "The stored package no longer matches its recorded hash and will not be used.");
        if (!opened.IsStructurallyValid) return (null, "The stored package no longer verifies and will not be used.");
        cache.Set(tenantId, row.PackageSha256, opened);
        return (opened, null);
    }

    private PackCatalogueEntry Unavailable(InstalledPack row, string reason)
    {
        var empty = new TrustResult(TrustStatus.Invalid, row.TrustKeyId, row.SignatureAlgorithm, null, []);
        return new PackCatalogueEntry(row.PackKey, row.Version, row.PackageId, new LocalisedText(new Dictionary<string, string> { ["en"] = row.DisplayName }), new LocalisedText(new Dictionary<string, string> { ["en"] = "" }),
            row.PublisherId, row.PublisherName, PackSourceTypes.TenantUploaded, row.PackageSha256, row.ContentDigest, empty, [], row.Synthetic, [], [], null, row.IsRetired, reason, row.InstalledUtc, row.UploadedBy);
    }

    private async Task<PackCatalogueEntry> DescribeAsync(OpenedPackage package, string source, bool retired, string? unavailable, DateTime? installedUtc, string? uploadedBy, string? fileName, CancellationToken ct)
    {
        var m = package.Manifest!;
        var trustResult = await trust.EvaluateAsync(package, ct);
        var compat = PackReader.CheckCompatibility(package, new PackCompatibilityContext(PlatformVersion.Current, kinds.Known));
        SolutionPackInfo? info = null;
        try
        {
            var json = package.Document(PackReader.SolutionPackKind);
            info = json is null ? null : JsonSerializer.Deserialize<SolutionPackInfo>(json, Json);
            if (info?.Defaults is null) info = null;
        }
        catch (JsonException) { info = null; }
        var content = m.Content.Select(c => new PackContentSummary(c.Kind, c.Scope, c.Required, c.SchemaVersion, c.Path)).ToList();
        return new PackCatalogueEntry(m.PackKey, m.Version, m.PackageId, m.DisplayName, m.Description, m.Publisher.Id, m.Publisher.Name, source, package.Sha256, package.ContentDigest, trustResult,
            compat, m.Synthetic, m.Capabilities, content, info, retired, unavailable, installedUtc, uploadedBy, fileName);
    }
}

/// <summary>Keeps the older, synchronous catalogue interface alive over the shipped packs for the first-run setup wizard, which runs before any tenant has installed anything.</summary>
internal sealed class ShippedSolutionPackCatalogue(ShippedPackSource shipped, PlatformKeyResolver platformKeys, IOptions<PackOptions> options, IClock clock) : ISolutionPackCatalogue
{
    private IReadOnlyList<PackCatalogueEntry> Entries()
    {
        var policy = new PackTrustPolicy(platformKeys);
        var knownKinds = PackKinds.Packable.ToHashSet(StringComparer.Ordinal);
        var list = new List<PackCatalogueEntry>();
        foreach (var (package, fileName) in shipped.Packs)
        {
            var m = package.Manifest!;
            var trust = policy.EvaluateAsync(m, package.CanonicalManifest, package.SignatureFile, new TrustContext(options.Value.IsProduction, options.Value.AllowUnsignedSyntheticPacks, clock.UtcNow), options.Value.Limits).GetAwaiter().GetResult();
            var compat = PackReader.CheckCompatibility(package, new PackCompatibilityContext(PlatformVersion.Current, knownKinds));
            SolutionPackInfo? info = null;
            try { info = package.Document(PackReader.SolutionPackKind) is { } json ? JsonSerializer.Deserialize<SolutionPackInfo>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null; } catch (JsonException) { }
            if (info?.Defaults is null) continue;
            list.Add(new PackCatalogueEntry(m.PackKey, m.Version, m.PackageId, m.DisplayName, m.Description, m.Publisher.Id, m.Publisher.Name, Domain.PackSourceTypes.Shipped, package.Sha256, package.ContentDigest, trust, compat,
                m.Synthetic, m.Capabilities, m.Content.Select(c => new PackContentSummary(c.Kind, c.Scope, c.Required, c.SchemaVersion, c.Path)).ToList(), info, false, null, null, null, fileName));
        }
        return list;
    }

    public IReadOnlyList<PackCatalogueEntry> ListEntries()
        => Entries().Where(e => e.IsUsable).GroupBy(e => e.PackKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(e => SemVer.TryParse(e.Version, out var v) ? v : new SemVer(0, 0, 0, null)).First()).OrderBy(e => e.PackKey, StringComparer.Ordinal).ToList();
    public string? TerminologyOf(string packKey)
    {
        var entry = ListEntries().FirstOrDefault(e => e.PackKey == packKey);
        return entry is null ? null : shipped.Packs.FirstOrDefault(p => p.Package.Sha256 == entry.PackageSha256).Package?.Document(TerminologyKinds.Name);
    }
    public string? CapabilitiesOf(string packKey)
    {
        var entry = ListEntries().FirstOrDefault(e => e.PackKey == packKey);
        return entry is null ? null : shipped.Packs.FirstOrDefault(p => p.Package.Sha256 == entry.PackageSha256).Package?.Document(CapabilityKinds.Name);
    }
    public string? RegionalOf(string packKey)
    {
        var entry = ListEntries().FirstOrDefault(e => e.PackKey == packKey);
        return entry is null ? null : shipped.Packs.FirstOrDefault(p => p.Package.Sha256 == entry.PackageSha256).Package?.Document(RegionalKinds.Name);
    }
    public IReadOnlyList<SolutionPackInfo> List() => ListEntries().Select(e => e.Info!).ToList();
    public SolutionPackInfo? Find(string packId) => ListEntries().Where(e => e.PackKey == packId).OrderByDescending(e => SemVer.TryParse(e.Version, out var v) ? v : new SemVer(0, 0, 0, null)).Select(e => e.Info).FirstOrDefault();
    public IReadOnlyList<PackCatalogueEntry> Rejected() => [];
}
