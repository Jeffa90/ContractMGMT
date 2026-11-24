namespace Ecp.Packs;

public enum TrustedKeyStatus { Active = 0, Revoked = 1 }

/// <summary>A public key a package signature may be checked against, with its lifetime. <c>Source</c> says where it came from (platform configuration, a tenant's trust store, or a test fixture).</summary>
public sealed record TrustedKey(string KeyId, string Algorithm, byte[] PublicKey, TrustedKeyStatus Status, DateTime? NotBeforeUtc, DateTime? NotAfterUtc, string Source, string? Label = null);

/// <summary>Finds the key a signature names. Implementations decide which stores they consult (platform configuration, the current tenant's registered keys). Rotation and revocation happen in the stores, not here.</summary>
public interface IPackKeyResolver
{
    Task<TrustedKey?> FindAsync(string keyId, CancellationToken ct = default);
}

/// <summary>A resolver over a fixed list, for tests, the pack tool and platform configuration.</summary>
public sealed class StaticKeyResolver(IEnumerable<TrustedKey> keys) : IPackKeyResolver
{
    private readonly Dictionary<string, TrustedKey> _keys = keys.GroupBy(k => k.KeyId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    public Task<TrustedKey?> FindAsync(string keyId, CancellationToken ct = default) => Task.FromResult(_keys.TryGetValue(keyId, out var k) ? k : null);
}

/// <summary>A resolver that consults several resolvers in order; the first to know the key wins (platform keys before a tenant's).</summary>
public sealed class CompositeKeyResolver(IEnumerable<IPackKeyResolver> resolvers) : IPackKeyResolver
{
    public async Task<TrustedKey?> FindAsync(string keyId, CancellationToken ct = default)
    {
        foreach (var r in resolvers)
            if (await r.FindAsync(keyId, ct) is { } key) return key;
        return null;
    }
}

public enum TrustStatus
{
    /// <summary>Signed by a trusted, active key, and the signature verifies.</summary>
    Trusted,
    /// <summary>
    /// Not signed, but shipped inside the application's own folder and deployed with the code (ADR-0047). Anyone who can change that folder can already change the
    /// application, so a signature there adds nothing. Only a pack with no signature at all qualifies: a shipped pack whose signature fails is still refused.
    /// Uploaded packs never get this status.
    /// </summary>
    ShippedWithApplication,
    /// <summary>No signature, but the package is marked synthetic and the environment explicitly allows unsigned synthetic packs (never in Production).</summary>
    UnsignedSyntheticAllowed,
    Missing,
    Invalid,
    UnknownKey,
    Revoked,
    Expired,
    NotYetValid,
    UnsupportedAlgorithm,
    Malformed,
    UnsignedNotPermitted
}

public sealed record TrustContext(bool IsProduction, bool AllowUnsignedSynthetic, DateTime NowUtc);

public sealed record TrustResult(TrustStatus Status, string? KeyId, string? Algorithm, string? KeySource, IReadOnlyList<PackIssue> Issues)
{
    /// <summary>Trusted for Production: signed by a trusted key, or shipped with the application (ADR-0047).</summary>
    public bool IsTrusted => Status is TrustStatus.Trusted or TrustStatus.ShippedWithApplication;
    /// <summary>Trusted, or unsigned-but-allowed in a non-Production environment. Everything else is refused.</summary>
    public bool IsAcceptable => IsTrusted || Status == TrustStatus.UnsignedSyntheticAllowed;
}

/// <summary>ADR-0047: the trust rule for packs that ship inside the application's own folder.</summary>
public static class ShippedPackTrust
{
    public const string KeySource = "shipped with the application";

    /// <summary>
    /// In Production, an <b>unsigned</b> shipped pack is trusted as <see cref="TrustStatus.ShippedWithApplication"/> when the setting allows it and the pack folder is
    /// inside the application folder. A signed shipped pack keeps the result of its signature check (a failing signature is still refused); other modes are unchanged.
    /// </summary>
    public static TrustResult Apply(TrustResult evaluated, bool unsigned, bool production, bool trustShippedPacks, bool folderInsideApplication)
        => production && unsigned && trustShippedPacks && folderInsideApplication && evaluated.Status == TrustStatus.Missing
            ? new TrustResult(TrustStatus.ShippedWithApplication, null, null, KeySource, [])
            : evaluated;
}

/// <summary>
/// Decides whether a package's signature is acceptable here and now. Production accepts only a valid signature from a trusted, active, in-date key with a supported algorithm. Demo and Test additionally accept
/// an unsigned package, but only when it is marked synthetic, its manifest allows it, and the environment setting is switched on. A manifest can never weaken the policy.
/// </summary>
public sealed class PackTrustPolicy(IPackKeyResolver keys, IEnumerable<IPackageSignatureVerifier>? verifiers = null)
{
    private readonly Dictionary<string, IPackageSignatureVerifier> _verifiers = (verifiers ?? [new EcdsaP256Verifier()]).ToDictionary(v => v.Algorithm, StringComparer.Ordinal);

    public async Task<TrustResult> EvaluateAsync(PackManifest manifest, byte[] canonicalManifest, byte[]? signatureFile, TrustContext context, PackLimits limits, CancellationToken ct = default)
    {
        TrustResult Refuse(TrustStatus status, string code, string message, string remediation, PackSignature? sig = null, TrustedKey? key = null)
            => new(status, sig?.KeyId, sig?.Algorithm, key?.Source, [new PackIssue(code, PackSeverity.Error, message, PackPaths.Signature, remediation)]);

        if (signatureFile is null)
        {
            if (!context.IsProduction && context.AllowUnsignedSynthetic && manifest.Synthetic && manifest.SignatureRequirement == SignatureRequirements.AllowUnsignedSynthetic)
                return new TrustResult(TrustStatus.UnsignedSyntheticAllowed, null, null, null,
                    [new PackIssue(PackCodes.UnsignedSyntheticAllowed, PackSeverity.Warning, "This package is NOT signed. It is accepted only because this is a Demo or Test environment that allows unsigned synthetic packs. Production would refuse it.", PackPaths.Signature, "Sign the package before using it anywhere real.")]);
            if (context.IsProduction)
                return Refuse(TrustStatus.Missing, PackCodes.SignatureMissing, "The package is not signed. Production accepts only packages signed by a trusted publisher.", "Obtain a signed package from the publisher, or sign it with your own trusted key (docs/PACK_SIGNING.md).");
            return Refuse(TrustStatus.UnsignedNotPermitted, PackCodes.UnsignedNotPermitted,
                "The package is not signed, and this environment does not accept unsigned packages (the package must be marked synthetic, its manifest must allow unsigned use, and the environment setting must be on).",
                "Sign the package, or in a Demo or Test environment enable the 'allow unsigned synthetic packs' setting for a synthetic package.");
        }

        var (signature, malformed) = PackSignatureDocument.Parse(signatureFile, limits);
        if (signature is null) return new TrustResult(TrustStatus.Malformed, null, null, null, [malformed!]);
        if (!SignatureAlgorithms.IsSupported(signature.Algorithm) || !_verifiers.TryGetValue(signature.Algorithm, out var verifier))
            return Refuse(TrustStatus.UnsupportedAlgorithm, PackCodes.AlgorithmUnsupported, $"The signature algorithm '{signature.Algorithm}' is not supported (supported: {string.Join(", ", SignatureAlgorithms.Supported)}).", "Sign the package with a supported algorithm.", signature);

        var key = await keys.FindAsync(signature.KeyId, ct);
        if (key is null)
            return Refuse(TrustStatus.UnknownKey, PackCodes.KeyUnknown, $"The package is signed by the key '{signature.KeyId}', which is not in the trust store.", "Ask a trust-key administrator to register the publisher's public key, if you trust the publisher.", signature);
        if (key.Status == TrustedKeyStatus.Revoked)
            return Refuse(TrustStatus.Revoked, PackCodes.KeyRevoked, $"The signing key '{signature.KeyId}' has been revoked.", "Obtain a package signed with a current key.", signature, key);
        if (key.NotBeforeUtc is { } from && context.NowUtc < from)
            return Refuse(TrustStatus.NotYetValid, PackCodes.KeyNotYetValid, $"The signing key '{signature.KeyId}' is not valid yet.", "Check the key's validity dates.", signature, key);
        if (key.NotAfterUtc is { } to && context.NowUtc > to)
            return Refuse(TrustStatus.Expired, PackCodes.KeyExpired, $"The signing key '{signature.KeyId}' has expired.", "Obtain a package signed with a current key, or ask a trust-key administrator to review the key's validity.", signature, key);
        if (!string.Equals(key.Algorithm, signature.Algorithm, StringComparison.Ordinal))
            return Refuse(TrustStatus.UnsupportedAlgorithm, PackCodes.AlgorithmUnsupported, "The signature algorithm does not match the algorithm of the trusted key.", "Sign the package with the key's algorithm.", signature, key);

        if (!verifier.Verify(SigningInput.For(canonicalManifest), signature.Value, key.PublicKey))
            return Refuse(TrustStatus.Invalid, PackCodes.SignatureInvalid, "The signature does not match the package. It may have been changed after it was signed, or signed with a different key.", "Do not use this package; obtain it again from the publisher.", signature, key);
        return new TrustResult(TrustStatus.Trusted, signature.KeyId, signature.Algorithm, key.Source, []);
    }
}
