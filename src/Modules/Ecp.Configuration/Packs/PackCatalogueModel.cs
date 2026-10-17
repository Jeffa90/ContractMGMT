using Ecp.Packs;

namespace Ecp.Configuration;

public sealed record PackContentSummary(string Kind, string Scope, bool Required, int SchemaVersion, string Path);

/// <summary>
/// One pack version as the platform sees it here and now: what it is, where it came from, whether it is trusted and compatible, and whether it can be used. Trust is judged again each time, because keys
/// can be revoked and clocks move on. Nothing here is a business: names, descriptions and capabilities are data read from the package.
/// </summary>
public sealed record PackCatalogueEntry(
    string PackKey, string Version, string PackageId, LocalisedText DisplayName, LocalisedText Description, string PublisherId, string PublisherName, string Source,
    string PackageSha256, string ContentDigest, TrustResult Trust, IReadOnlyList<PackIssue> CompatibilityIssues, bool Synthetic, IReadOnlyList<string> Capabilities,
    IReadOnlyList<PackContentSummary> Content, SolutionPackInfo? Info, bool Retired, string? UnavailableReason, DateTime? InstalledUtc, string? UploadedBy, string? ShippedFileName = null)
{
    public bool IsCompatible => CompatibilityIssues.All(i => !i.IsError);
    public bool Unavailable => UnavailableReason is not null;
    /// <summary>Can be offered for a new assignment: not retired, readable, compatible, and acceptable under the trust policy of this environment.</summary>
    public bool IsUsable => !Retired && !Unavailable && IsCompatible && Trust.IsAcceptable;
    /// <summary>Signed by a trusted key, compatible and readable: what Production requires.</summary>
    public bool ProductionEligible => !Unavailable && IsCompatible && Trust.IsTrusted;
    public bool IsShipped => Source == Domain.PackSourceTypes.Shipped;
    public string Name(string? culture = null) => DisplayName.Resolve(culture);
    public string DescriptionText(string? culture = null) => Description.Resolve(culture);
    public IReadOnlyList<PackIssue> AllIssues => [.. CompatibilityIssues, .. Trust.Issues];
}

/// <summary>A pack document chosen as an organisation's starting point, with the provenance to record.</summary>
public sealed record PackDocument(string Json, string PackKey, string Version, string PackageSha256, string Source);

/// <summary>The unified, product-neutral pack loader: it finds packs by key and version, whichever source they come from, and returns their documents by configuration kind.</summary>
public interface IPackLoader
{
    /// <summary>Every pack visible to the current tenant (shipped and installed), usable or not, with its trust and compatibility. Use <see cref="PackCatalogueEntry.IsUsable"/> to filter.</summary>
    Task<IReadOnlyList<PackCatalogueEntry>> ListAsync(CancellationToken ct = default);
    /// <summary>The pack version named, or the latest usable version when <paramref name="version"/> is null. Null when there is none.</summary>
    Task<PackCatalogueEntry?> FindAsync(string packKey, string? version = null, bool usableOnly = true, CancellationToken ct = default);
    /// <summary>The JSON document of this configuration kind in the pack version named (or the latest usable one). Null when the pack does not carry that kind.</summary>
    Task<string?> GetDocumentAsync(string packKey, string? version, string kind, CancellationToken ct = default);
    /// <summary>The package itself (verified), for previews and assignment.</summary>
    Task<(PackCatalogueEntry Entry, OpenedPackage Package)?> OpenAsync(string packKey, string version, CancellationToken ct = default);
}
