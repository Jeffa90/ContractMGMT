using Ecp.Configuration.Domain;
using Ecp.Configuration.Persistence;
using Ecp.Packs;
using Ecp.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ecp.Configuration;

/// <summary>Keys supplied by host configuration: the platform operator's trust anchors. Read once; invalid entries are reported (not trusted) so a typing mistake never widens trust.</summary>
public sealed class PlatformKeyResolver : IPackKeyResolver
{
    private readonly Dictionary<string, TrustedKey> _keys = new(StringComparer.Ordinal);
    public IReadOnlyList<string> Problems { get; }

    public PlatformKeyResolver(IOptions<PackOptions> options, ILogger<PlatformKeyResolver>? logger = null)
    {
        var problems = new List<string>();
        var o = options.Value;
        foreach (var k in o.TrustedKeys)
        {
            try
            {
                var pem = k.PublicKeyPem ?? (k.PublicKeyFile is null ? null : File.ReadAllText(k.PublicKeyFile));
                if (pem is null || k.KeyId is null || !PackSignatureDocument.KeyIdPattern().IsMatch(k.KeyId)) { problems.Add($"Trusted key '{k.KeyId}' needs a valid key id and a public key."); continue; }
                if (pem.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase)) { problems.Add($"Trusted key '{k.KeyId}' looks like a private key and was ignored. Configure public keys only."); continue; }
                _keys[k.KeyId] = new TrustedKey(k.KeyId, k.Algorithm, PackKeys.PublicKeyFromPem(pem), k.Revoked ? TrustedKeyStatus.Revoked : TrustedKeyStatus.Active, k.NotBeforeUtc, k.NotAfterUtc, "platform configuration", k.Label);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException or ArgumentException)
            {
                problems.Add($"Trusted key '{k.KeyId}' could not be read.");
            }
        }
        if (!string.IsNullOrWhiteSpace(o.TrustFile))
        {
            try { foreach (var key in TrustFile.Read(File.ReadAllText(o.TrustFile), "platform trust file")) _keys[key.KeyId] = key; }
            catch (Exception ex) when (ex is IOException or FormatException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
            { problems.Add("The platform trust file could not be read."); }
        }
        Problems = problems;
        foreach (var p in problems) logger?.LogWarning("Pack trust configuration: {Problem}", p);
    }

    public IReadOnlyCollection<TrustedKey> All => _keys.Values;
    public Task<TrustedKey?> FindAsync(string keyId, CancellationToken ct = default) => Task.FromResult(_keys.TryGetValue(keyId, out var k) ? k : null);
}

/// <summary>The current tenant's registered publisher keys. The tenant filter on the entity is what keeps one tenant's trust decisions from reaching another.</summary>
internal sealed class TenantKeyResolver(ConfigurationDbContext db, ITenantContext tenant) : IPackKeyResolver
{
    public async Task<TrustedKey?> FindAsync(string keyId, CancellationToken ct = default)
    {
        if (tenant.TenantId is null) return null;
        var row = await db.TrustedKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyId == keyId, ct);
        return row is null ? null : ToKey(row);
    }

    public static TrustedKey ToKey(TrustedPublisherKey row)
        => new(row.KeyId, row.Algorithm, row.PublicKey, row.Status == TrustedPublisherKey.Revoked ? TrustedKeyStatus.Revoked : TrustedKeyStatus.Active, row.NotBeforeUtc, row.NotAfterUtc, "tenant trust store", row.Label);
}
