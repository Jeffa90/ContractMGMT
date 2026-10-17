using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ecp.Packs;

/// <summary>The signature algorithms a package may name. Version 1 supports ECDSA over P-256 with SHA-256 (JOSE name <c>ES256</c>). The name is stored in <c>signature.json</c> so another approved algorithm can be added later.</summary>
public static class SignatureAlgorithms
{
    public const string Es256 = "ES256";
    public static readonly IReadOnlyList<string> Supported = [Es256];
    public static bool IsSupported(string? algorithm) => algorithm is not null && Supported.Contains(algorithm, StringComparer.Ordinal);
}

/// <summary>A detached signature over the canonical manifest.</summary>
public sealed record PackSignature(int FormatVersion, string Algorithm, string KeyId, DateTime SignedUtc, byte[] Value);

public static partial class PackSignatureDocument
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{1,79}$", RegexOptions.NonBacktracking)] public static partial Regex KeyIdPattern();

    public static (PackSignature? Signature, PackIssue? Issue) Parse(ReadOnlySpan<byte> bytes, PackLimits limits)
    {
        PackIssue Malformed(string why) => new(PackCodes.SignatureMalformed, PackSeverity.Error, "The signature file cannot be read: " + why, PackPaths.Signature, "Sign the package again with the pack tool.");
        try
        {
            using var doc = CanonicalJson.Parse(bytes, limits.MaxJsonDepth);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, Malformed("it is not an object."));
            string[] allowed = ["formatVersion", "algorithm", "keyId", "signedUtc", "signature"];
            foreach (var p in root.EnumerateObject()) if (!allowed.Contains(p.Name)) return (null, Malformed($"'{p.Name}' is not a recognised field."));
            string? Text(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (!root.TryGetProperty("formatVersion", out var fv) || !fv.TryGetInt32(out var format) || format != 1) return (null, Malformed("the signature format version is not supported."));
            var algorithm = Text("algorithm"); var keyId = Text("keyId"); var signed = Text("signedUtc"); var value = Text("signature");
            if (algorithm is null || algorithm.Length > 20) return (null, Malformed("the algorithm is missing."));
            if (keyId is null || !KeyIdPattern().IsMatch(keyId)) return (null, Malformed("the key identifier is missing or not valid."));
            if (signed is null || !signed.EndsWith('Z') || !DateTime.TryParse(signed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var signedUtc))
                return (null, Malformed("the signing time is missing or not a UTC timestamp."));
            if (value is null || value.Length > 2048) return (null, Malformed("the signature value is missing."));
            byte[] raw;
            try { raw = Convert.FromBase64String(value); } catch (FormatException) { return (null, Malformed("the signature value is not valid base64.")); }
            return (new PackSignature(format, algorithm, keyId, signedUtc, raw), null);
        }
        catch (FormatException ex) { return (null, Malformed(ex.Message)); }
    }

    public static byte[] Serialize(PackSignature s)
    {
        var json = $"{{\"algorithm\":{JsonSerializer.Serialize(s.Algorithm)},\"formatVersion\":{s.FormatVersion},\"keyId\":{JsonSerializer.Serialize(s.KeyId)},\"signature\":\"{Convert.ToBase64String(s.Value)}\",\"signedUtc\":\"{s.SignedUtc.ToUniversalTime():yyyy-MM-dd'T'HH:mm:ss'Z'}\"}}";
        return CanonicalJson.Canonicalise(Encoding.UTF8.GetBytes(json));
    }
}

/// <summary>What is actually signed: a domain-separation prefix followed by the canonical manifest bytes. The prefix stops a signature made for a package from being valid for anything else.</summary>
public static class SigningInput
{
    private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("ECP-PACK-MANIFEST-V1\n");
    public static byte[] For(byte[] canonicalManifest) => [.. Prefix, .. canonicalManifest];
}

public interface IPackageSigner
{
    string Algorithm { get; }
    string KeyId { get; }
    /// <summary>Signs the signing input. Implementations take the private key from a file, a secret reference or an in-memory test fixture; they never log it.</summary>
    byte[] Sign(byte[] signingInput);
}

public interface IPackageSignatureVerifier
{
    string Algorithm { get; }
    bool Verify(byte[] signingInput, byte[] signature, byte[] publicKeySpki);
}

public sealed class EcdsaP256Signer : IPackageSigner, IDisposable
{
    private readonly ECDsa _key;
    public EcdsaP256Signer(ECDsa key, string keyId)
    {
        if (!PackKeys.IsP256(key)) throw new CryptographicException("The signing key must be an ECDSA P-256 key.");
        if (!PackSignatureDocument.KeyIdPattern().IsMatch(keyId)) throw new ArgumentException("The key identifier is not valid.", nameof(keyId));
        _key = key; KeyId = keyId;
    }

    public string Algorithm => SignatureAlgorithms.Es256;
    public string KeyId { get; }

    public byte[] Sign(byte[] signingInput) => _key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static EcdsaP256Signer FromPem(string privateKeyPem, string keyId)
    {
        var key = ECDsa.Create();
        try { key.ImportFromPem(privateKeyPem); }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException) { key.Dispose(); throw new CryptographicException("The private key could not be read (expected a PKCS#8 or SEC1 PEM for an ECDSA P-256 key)."); }
        return new EcdsaP256Signer(key, keyId);
    }

    public void Dispose() => _key.Dispose();
}

public sealed class EcdsaP256Verifier : IPackageSignatureVerifier
{
    public string Algorithm => SignatureAlgorithms.Es256;

    public bool Verify(byte[] signingInput, byte[] signature, byte[] publicKeySpki)
    {
        if (signature.Length != 64) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (!PackKeys.IsP256(key)) return false;
            return key.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Key helpers: generating a key pair (tests, the tool's keygen command), reading a public key from PEM, and a short fingerprint for display. Private keys are never held longer than needed and never returned in messages.</summary>
public static class PackKeys
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    public static bool IsP256(ECDsa key)
    {
        try { return key.ExportParameters(false).Curve.Oid?.Value == P256Oid; }
        catch (CryptographicException) { return false; }
    }

    /// <summary>A fresh P-256 key pair: the private key as PKCS#8 PEM, the public key as SubjectPublicKeyInfo DER.</summary>
    public static (string PrivatePem, byte[] PublicSpki) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfo());
    }

    public static string ToPublicPem(byte[] spki) => PemEncoding.WriteString("PUBLIC KEY", spki);

    /// <summary>Reads a public key (PEM "PUBLIC KEY") and checks it is an ECDSA P-256 key. Throws <see cref="CryptographicException"/> with a safe message otherwise.</summary>
    public static byte[] PublicKeyFromPem(string pem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            if (!IsP256(key)) throw new CryptographicException("The public key must be an ECDSA P-256 key.");
            return key.ExportSubjectPublicKeyInfo();
        }
        catch (ArgumentException) { throw new CryptographicException("The public key could not be read (expected a PEM public key)."); }
    }

    /// <summary>The first 16 hexadecimal characters of the SHA-256 of the public key, for showing which key is meant.</summary>
    public static string Fingerprint(byte[] spki) => Convert.ToHexStringLower(SHA256.HashData(spki))[..16];
}
