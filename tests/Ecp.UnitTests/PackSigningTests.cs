using System.Text;
using Ecp.Packs;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>ECDSA P-256 package signing, trust policy by environment, and the pack tool (S7b; PKG-004, PKG-005, PKG-011).</summary>
[Trait("Category", "Unit")]
public sealed class PackSigningTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<TrustResult> Evaluate(byte[] package, IEnumerable<TrustedKey> keys, bool production = true, bool allowUnsigned = false, DateTime? now = null)
    {
        var opened = PackReader.Open(package);
        Assert.True(opened.IsStructurallyValid, string.Join("; ", opened.Issues.Select(i => i.Message)));
        return await new PackTrustPolicy(new StaticKeyResolver(keys)).EvaluateAsync(opened.Manifest!, opened.CanonicalManifest, opened.SignatureFile, new TrustContext(production, allowUnsigned, now ?? Now), PackLimits.Default);
    }

    [Fact]
    public async Task A_valid_signature_from_a_trusted_key_is_trusted_in_production()
    {
        using var key = new TestSigningKey();
        var result = await Evaluate(PackTestKit.Sign(PackTestKit.Build(), key), [key.AsTrusted()]);
        Assert.Equal(TrustStatus.Trusted, result.Status);
        Assert.True(result.IsTrusted);
        Assert.Equal("test-key-1", result.KeyId);
        Assert.Equal(SignatureAlgorithms.Es256, result.Algorithm);
    }

    [Fact]
    public async Task A_signed_package_stays_valid_when_only_the_signature_is_added()
    {
        using var key = new TestSigningKey();
        var unsigned = PackTestKit.Build();
        var signed = PackTestKit.Sign(unsigned, key);
        Assert.Equal(PackReader.Open(unsigned).ContentDigest, PackReader.Open(signed).ContentDigest);
        Assert.True(PackBuilder.Unsigned(PackReader.Open(signed)).AsSpan().SequenceEqual(unsigned));
        Assert.Equal(TrustStatus.Trusted, (await Evaluate(signed, [key.AsTrusted()])).Status);
    }

    [Fact]
    public async Task A_changed_signature_value_is_invalid()
    {
        using var key = new TestSigningKey();
        var entries = PackTestKit.Entries(PackTestKit.Sign(PackTestKit.Build(), key));
        var text = Encoding.UTF8.GetString(entries["signature.json"]);
        var (sig, _) = PackSignatureDocument.Parse(entries["signature.json"], PackLimits.Default);
        var flipped = sig!.Value.ToArray(); flipped[10] ^= 0x01;
        entries["signature.json"] = PackSignatureDocument.Serialize(sig with { Value = flipped });
        Assert.NotEqual(text, Encoding.UTF8.GetString(entries["signature.json"]));
        var result = await Evaluate(PackTestKit.Rezip(entries), [key.AsTrusted()]);
        Assert.Equal(TrustStatus.Invalid, result.Status);
        Assert.Contains(result.Issues, i => i.Code == PackCodes.SignatureInvalid);
    }

    [Fact]
    public async Task A_tampered_manifest_invalidates_the_signature_even_when_the_content_still_matches()
    {
        using var key = new TestSigningKey();
        var entries = PackTestKit.Entries(PackTestKit.Sign(PackTestKit.Build(), key));
        entries["manifest.json"] = PackTestKit.Utf8(Encoding.UTF8.GetString(entries["manifest.json"]).Replace("Test publisher", "Another publisher"));
        Assert.Equal(TrustStatus.Invalid, (await Evaluate(PackTestKit.Rezip(entries), [key.AsTrusted()])).Status);
    }

    [Fact]
    public async Task Tampered_content_is_caught_by_the_hash_before_trust_is_even_asked()
    {
        using var key = new TestSigningKey();
        var entries = PackTestKit.Entries(PackTestKit.Sign(PackTestKit.Build(), key));
        entries["content/solution-pack.json"] = PackTestKit.Utf8(Encoding.UTF8.GetString(entries["content/solution-pack.json"]).Replace("Sample pack", "Evil pack"));
        var opened = PackReader.Open(PackTestKit.Rezip(entries));
        Assert.False(opened.IsStructurallyValid);
        Assert.Contains(opened.Issues, i => i.Code == PackCodes.HashMismatch);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_wrong_public_key_under_the_same_key_id_does_not_verify()
    {
        using var signer = new TestSigningKey("shared-id"); using var other = new TestSigningKey("shared-id");
        var result = await Evaluate(PackTestKit.Sign(PackTestKit.Build(), signer), [other.AsTrusted()]);
        Assert.Equal(TrustStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task A_key_that_is_not_in_the_trust_store_is_unknown()
    {
        using var key = new TestSigningKey(); using var other = new TestSigningKey("someone-else");
        var result = await Evaluate(PackTestKit.Sign(PackTestKit.Build(), key), [other.AsTrusted()]);
        Assert.Equal(TrustStatus.UnknownKey, result.Status);
        Assert.Contains(result.Issues, i => i.Code == PackCodes.KeyUnknown);
    }

    [Fact]
    public async Task A_revoked_key_is_refused()
    {
        using var key = new TestSigningKey();
        var result = await Evaluate(PackTestKit.Sign(PackTestKit.Build(), key), [key.AsTrusted(TrustedKeyStatus.Revoked)]);
        Assert.Equal(TrustStatus.Revoked, result.Status);
        Assert.Contains(result.Issues, i => i.Code == PackCodes.KeyRevoked);
    }

    [Fact]
    public async Task An_expired_key_and_a_key_that_is_not_yet_valid_are_refused()
    {
        using var key = new TestSigningKey();
        var signed = PackTestKit.Sign(PackTestKit.Build(), key);
        Assert.Equal(TrustStatus.Expired, (await Evaluate(signed, [key.AsTrusted(notAfter: Now.AddDays(-1))])).Status);
        Assert.Equal(TrustStatus.NotYetValid, (await Evaluate(signed, [key.AsTrusted(notBefore: Now.AddDays(1))])).Status);
        Assert.Equal(TrustStatus.Trusted, (await Evaluate(signed, [key.AsTrusted(notBefore: Now.AddDays(-1), notAfter: Now.AddDays(1))])).Status);
    }

    [Fact]
    public async Task An_unsupported_algorithm_is_refused()
    {
        using var key = new TestSigningKey();
        var entries = PackTestKit.Entries(PackTestKit.Sign(PackTestKit.Build(), key));
        var (sig, _) = PackSignatureDocument.Parse(entries["signature.json"], PackLimits.Default);
        entries["signature.json"] = PackSignatureDocument.Serialize(sig! with { Algorithm = "RS256" });
        var result = await Evaluate(PackTestKit.Rezip(entries), [key.AsTrusted()]);
        Assert.Equal(TrustStatus.UnsupportedAlgorithm, result.Status);
        Assert.Contains(result.Issues, i => i.Code == PackCodes.AlgorithmUnsupported);
    }

    [Fact]
    public async Task A_malformed_signature_file_is_refused()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        entries["signature.json"] = PackTestKit.Utf8("{\"algorithm\":\"ES256\"}");
        Assert.Equal(TrustStatus.Malformed, (await Evaluate(PackTestKit.Rezip(entries), [])).Status);
    }

    [Fact]
    public async Task Production_refuses_an_unsigned_package_even_when_the_setting_and_the_manifest_allow_it()
    {
        var unsigned = PackTestKit.Build();
        var result = await Evaluate(unsigned, [], production: true, allowUnsigned: true);
        Assert.Equal(TrustStatus.Missing, result.Status);
        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Issues, i => i.Code == PackCodes.SignatureMissing);
    }

    [Fact]
    public async Task Demo_accepts_an_unsigned_synthetic_package_only_with_the_explicit_setting_and_warns()
    {
        var unsigned = PackTestKit.Build();
        var allowed = await Evaluate(unsigned, [], production: false, allowUnsigned: true);
        Assert.Equal(TrustStatus.UnsignedSyntheticAllowed, allowed.Status);
        Assert.True(allowed.IsAcceptable); Assert.False(allowed.IsTrusted);
        Assert.Contains(allowed.Issues, i => i.Code == PackCodes.UnsignedSyntheticAllowed && i.Severity == PackSeverity.Warning);
        var refused = await Evaluate(unsigned, [], production: false, allowUnsigned: false);
        Assert.Equal(TrustStatus.UnsignedNotPermitted, refused.Status);
        Assert.Contains(refused.Issues, i => i.Code == PackCodes.UnsignedNotPermitted);
    }

    [Fact]
    public async Task An_unsigned_package_is_refused_when_it_is_not_synthetic_or_its_manifest_requires_a_signature()
    {
        Assert.False((await Evaluate(PackTestKit.Build(synthetic: false), [], production: false, allowUnsigned: true)).IsAcceptable);
        Assert.False((await Evaluate(PackTestKit.Build(requirement: "Required"), [], production: false, allowUnsigned: true)).IsAcceptable);
    }

    [Fact]
    public async Task A_signed_package_from_an_untrusted_key_is_not_rescued_by_the_unsigned_setting()
    {
        using var key = new TestSigningKey();
        var result = await Evaluate(PackTestKit.Sign(PackTestKit.Build(), key), [], production: false, allowUnsigned: true);
        Assert.Equal(TrustStatus.UnknownKey, result.Status);
    }

    [Fact]
    public void Signing_refuses_a_package_that_is_not_valid_and_a_key_that_is_not_p256()
    {
        using var key = new TestSigningKey();
        var entries = PackTestKit.Entries(PackTestKit.Build());
        entries.Remove("content/solution-pack.json");
        Assert.Throws<InvalidOperationException>(() => PackBuilder.Sign(PackReader.Open(PackTestKit.Rezip(entries)), key.Signer, Now));
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => new EcdsaP256Signer(p384, "k"));
    }

    [Fact]
    public void A_public_key_that_is_not_p256_or_is_not_a_key_is_rejected_without_echoing_it()
    {
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        var pem = p384.ExportSubjectPublicKeyInfoPem();
        var ex = Assert.Throws<System.Security.Cryptography.CryptographicException>(() => PackKeys.PublicKeyFromPem(pem));
        Assert.DoesNotContain("BEGIN", ex.Message, StringComparison.Ordinal);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => PackKeys.PublicKeyFromPem("not a key"));
    }

    [Fact]
    public void The_signing_input_is_domain_separated_from_the_raw_manifest()
    {
        var canonical = Encoding.UTF8.GetBytes("{}");
        Assert.NotEqual(canonical, SigningInput.For(canonical));
        Assert.StartsWith("ECP-PACK-MANIFEST-V1", Encoding.ASCII.GetString(SigningInput.For(canonical)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_composite_resolver_prefers_the_first_store_that_knows_the_key()
    {
        using var a = new TestSigningKey("k1"); using var b = new TestSigningKey("k1");
        var composite = new CompositeKeyResolver([new StaticKeyResolver([a.AsTrusted(source: "platform")]), new StaticKeyResolver([b.AsTrusted(source: "tenant")])]);
        Assert.Equal("platform", (await composite.FindAsync("k1"))!.Source);
        Assert.Null(await composite.FindAsync("missing"));
    }

    // ---- the pack tool ---------------------------------------------------------------------------------------------------------------------------------

    private static (int Code, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = PackCli.Run(args, o, e, clock: () => Now);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void The_tool_builds_signs_verifies_inspects_and_compares_a_pack_and_never_prints_key_material()
    {
        var dir = PackTestKit.TempDirectory();
        Directory.CreateDirectory(dir);
        try
        {
            var source = PackTestKit.WriteSource(Path.Combine(dir, "src"));
            var unsigned = Path.Combine(dir, "a.ecpkg"); var again = Path.Combine(dir, "b.ecpkg"); var signed = Path.Combine(dir, "signed.ecpkg");
            var priv = Path.Combine(dir, "p.pem"); var pub = Path.Combine(dir, "pub.pem");

            Assert.Equal(0, Cli("build", "--source", source, "--out", unsigned).Code);
            Assert.Equal(0, Cli("build", "--source", source, "--out", again).Code);
            Assert.True(File.ReadAllBytes(unsigned).AsSpan().SequenceEqual(File.ReadAllBytes(again)));
            Assert.Equal(0, Cli("validate", "--package", unsigned).Code);

            var keygen = Cli("keygen", "--out-private", priv, "--out-public", pub);
            Assert.Equal(0, keygen.Code);
            Assert.DoesNotContain("PRIVATE", keygen.Out + keygen.Err, StringComparison.Ordinal);
            Assert.Equal(1, Cli("keygen", "--out-private", priv, "--out-public", pub).Code);       // will not overwrite a key

            var sign = Cli("sign", "--package", unsigned, "--out", signed, "--key-file", priv, "--key-id", "tool-key");
            Assert.Equal(0, sign.Code);
            Assert.DoesNotContain("PRIVATE", sign.Out + sign.Err, StringComparison.Ordinal);
            Assert.Equal(0, Cli("verify", "--package", signed, "--public-key", pub, "--key-id", "tool-key", "--production").Code);
            Assert.Equal(1, Cli("verify", "--package", signed, "--public-key", pub, "--key-id", "other-key", "--production").Code);
            Assert.Equal(1, Cli("verify", "--package", unsigned, "--public-key", pub, "--key-id", "tool-key", "--production").Code);
            Assert.Equal(0, Cli("verify", "--package", unsigned, "--public-key", pub, "--key-id", "tool-key", "--allow-unsigned-synthetic").Code);
            Assert.Equal(0, Cli("compare", "--a", unsigned, "--b", signed).Code);

            var inspect = Cli("inspect", "--package", signed, "--public-key", pub, "--key-id", "tool-key");
            Assert.Equal(0, inspect.Code);
            Assert.Contains("sample-pack", inspect.Out, StringComparison.Ordinal);
            Assert.Contains("valid, key tool-key", inspect.Out, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(PackSignatureDocument.Parse(PackTestKit.Entries(File.ReadAllBytes(signed))["signature.json"], PackLimits.Default).Signature!.Value), inspect.Out, StringComparison.Ordinal);

            var hash = Cli("hash", "--package", signed);
            Assert.Equal(0, hash.Code);
            Assert.Contains(PackReader.Open(File.ReadAllBytes(signed)).Sha256, hash.Out, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_tool_signs_with_a_key_from_an_environment_variable_and_reports_usage_errors()
    {
        var dir = PackTestKit.TempDirectory();
        Directory.CreateDirectory(dir);
        try
        {
            var unsigned = Path.Combine(dir, "a.ecpkg"); File.WriteAllBytes(unsigned, PackTestKit.Build());
            var (pem, spki) = PackKeys.Generate();
            var o = new StringWriter(); var e = new StringWriter();
            var code = PackCli.Run(["sign", "--package", unsigned, "--out", Path.Combine(dir, "s.ecpkg"), "--key-env", "ECP_TEST_KEY", "--key-id", "env-key"], o, e, name => name == "ECP_TEST_KEY" ? pem : null, () => Now);
            Assert.Equal(0, code);
            Assert.Equal(2, Cli("sign", "--package", unsigned, "--out", "x", "--key-id", "k").Code);                      // neither --key-file nor --key-env
            Assert.Equal(2, Cli("nonsense").Code);
            Assert.Equal(3, Cli("validate", "--package", Path.Combine(dir, "missing.ecpkg")).Code);              // a file that cannot be read is the environment's fault, not an unacceptable package
            Assert.NotEmpty(spki);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_trust_file_holds_public_keys_only()
    {
        var (_, spki) = PackKeys.Generate();
        var good = $"{{\"keys\":[{{\"keyId\":\"k1\",\"publicKeyPem\":{System.Text.Json.JsonSerializer.Serialize(PackKeys.ToPublicPem(spki))},\"revoked\":true}}]}}";
        var keys = TrustFile.Read(good);
        Assert.Equal(TrustedKeyStatus.Revoked, Assert.Single(keys).Status);
        var (privatePem, _) = PackKeys.Generate();
        Assert.Throws<FormatException>(() => TrustFile.Read($"{{\"keys\":[{{\"keyId\":\"k2\",\"publicKeyPem\":{System.Text.Json.JsonSerializer.Serialize(privatePem)}}}]}}"));
    }
}
