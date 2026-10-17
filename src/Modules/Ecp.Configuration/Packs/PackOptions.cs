using Ecp.Packs;
using Ecp.SharedKernel;

namespace Ecp.Configuration;

/// <summary>A trusted public key supplied through host configuration (<c>Ecp:Packs:TrustedKeys</c>). Public keys only; a private key is never configured here.</summary>
public sealed class ConfiguredTrustKey
{
    public string KeyId { get; set; } = "";
    public string Algorithm { get; set; } = SignatureAlgorithms.Es256;
    /// <summary>The public key as PEM text, or the name of a PEM file in <see cref="PublicKeyFile"/>.</summary>
    public string? PublicKeyPem { get; set; }
    public string? PublicKeyFile { get; set; }
    public DateTime? NotBeforeUtc { get; set; }
    public DateTime? NotAfterUtc { get; set; }
    public bool Revoked { get; set; }
    public string? Label { get; set; }
}

/// <summary>Host configuration for solution packs (<c>Ecp:Packs</c>).</summary>
public sealed class PackOptions
{
    /// <summary>The folder of shipped, read-only <c>.ecpkg</c> files. Defaults to <c>packs</c> next to the application.</summary>
    public string? ShippedDirectory { get; set; }
    /// <summary>Where tenant-installed packages and the upload quarantine live. Must be outside any publicly served path; the host points it at the data directory.</summary>
    public string? StorageDirectory { get; set; }
    /// <summary>Demo and Test only: accept an unsigned package that is marked synthetic. Production ignores this setting and refuses unsigned packages.</summary>
    public bool AllowUnsignedSyntheticPacks { get; set; }
    /// <summary>The installation mode. The host sets it; the default is the safest.</summary>
    public EcpMode Mode { get; set; } = EcpMode.Production;
    public List<ConfiguredTrustKey> TrustedKeys { get; set; } = [];
    /// <summary>Optional JSON file of public keys (see docs/PACK_SIGNING.md).</summary>
    public string? TrustFile { get; set; }
    public long MaxUploadBytes { get; set; } = 8L * 1024 * 1024;
    public TimeSpan QuarantineLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public PackLimits Limits { get; set; } = PackLimits.Default;

    public bool IsProduction => Mode == EcpMode.Production;
    public string ResolvedShippedDirectory => string.IsNullOrWhiteSpace(ShippedDirectory) ? Path.Combine(AppContext.BaseDirectory, "packs") : ShippedDirectory;
    public string ResolvedStorageDirectory => string.IsNullOrWhiteSpace(StorageDirectory) ? Path.Combine(Path.GetTempPath(), "ecp-pack-store") : StorageDirectory;
}
