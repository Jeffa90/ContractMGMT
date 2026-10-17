using System.Text;
using Ecp.Configuration;
using Ecp.Packs;
using Ecp.SharedKernel;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>S4: terminology as a pack document. It is covered by the content hash and the signature like every other document, a pack without one stays compatible, and hostile or incomplete documents are refused.</summary>
[Trait("Category", "Unit")]
public sealed class PackTerminologyTests
{
    private static byte[] Pack(string terminologyJson, string key = "term-pack", string version = "1.0.0")
        => PackTestKit.Build(key, version, content: [PackTestKit.TerminologyEntry()], extraDocuments: new Dictionary<string, string> { ["terminology.json"] = terminologyJson });

    private static string Doc(params (string Key, string S, string P)[] overrides)
    {
        var map = overrides.ToDictionary(o => o.Key, o => (o.S, o.P));
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = 1, mode = "complete",
            terms = TermCatalogue.All.Select(d => new { key = d.Key, locale = "en", singular = map.TryGetValue(d.Key, out var o) ? o.S : d.Singular, plural = map.TryGetValue(d.Key, out var o2) ? o2.P : d.Plural })
        });
    }

    [Fact]
    public void A_pack_carries_a_terminology_document_listed_in_the_manifest_and_hashed()
    {
        var opened = PackReader.Open(Pack(Doc(("supplier", "Vendor", "Vendors"))));
        Assert.True(opened.IsStructurallyValid, string.Join("; ", opened.Issues.Select(i => i.Message)));
        var entry = opened.Manifest!.Content.Single(c => c.Kind == "Terminology");
        Assert.Equal("organisation", entry.Scope);
        Assert.Contains("\"Vendor\"", opened.Document("Terminology"));
        Assert.Contains("Terminology", PackKinds.Packable);
    }

    [Fact]
    public void The_content_digest_and_package_hash_cover_the_terminology_so_a_changed_label_is_a_different_pack()
    {
        var a = PackReader.Open(Pack(Doc(("supplier", "Vendor", "Vendors"))));
        var b = PackReader.Open(Pack(Doc(("supplier", "Partner", "Partners"))));
        Assert.NotEqual(a.ContentDigest, b.ContentDigest);
        Assert.NotEqual(a.Sha256, b.Sha256);
        // The same document built twice is byte for byte the same package.
        Assert.Equal(a.Sha256, PackReader.Open(Pack(Doc(("supplier", "Vendor", "Vendors")))).Sha256);
    }

    [Fact]
    public void Changing_the_terminology_after_the_pack_was_built_is_detected()
    {
        var entries = PackTestKit.Entries(Pack(Doc(("supplier", "Vendor", "Vendors"))));
        entries["content/terminology.json"] = PackTestKit.Utf8(Encoding.UTF8.GetString(entries["content/terminology.json"]).Replace("Vendor", "Hacker"));
        var opened = PackReader.Open(PackTestKit.Rezip(entries));
        Assert.False(opened.IsStructurallyValid);
        Assert.Contains(opened.Issues, i => i.Code == PackCodes.HashMismatch && i.Entry == "content/terminology.json");
    }

    [Fact]
    public async Task The_signature_covers_the_terminology_so_a_tampered_signed_pack_is_not_trusted()
    {
        using var key = new TestSigningKey();
        var signed = PackTestKit.Sign(Pack(Doc(("supplier", "Vendor", "Vendors"))), key);
        var good = PackReader.Open(signed);
        var trust = new PackTrustPolicy(new StaticKeyResolver([key.AsTrusted()]));
        var context = new TrustContext(true, false, new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(TrustStatus.Trusted, (await trust.EvaluateAsync(good.Manifest!, good.CanonicalManifest, good.SignatureFile, context, PackLimits.Default)).Status);

        // Edit the terminology and keep the manifest (and so the signature) as they were: the hash no longer matches.
        var entries = PackTestKit.Entries(signed);
        entries["content/terminology.json"] = PackTestKit.Utf8(Encoding.UTF8.GetString(entries["content/terminology.json"]).Replace("Vendor", "Hacker"));
        Assert.Contains(PackReader.Open(PackTestKit.Rezip(entries)).Issues, i => i.Code == PackCodes.HashMismatch);

        // Edit the terminology AND repair the manifest hash: the signature over the manifest no longer verifies.
        var manifest = Encoding.UTF8.GetString(entries["manifest.json"]);
        var oldHash = good.Manifest!.Content.Single(c => c.Kind == "Terminology").Sha256;
        var newHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(entries["content/terminology.json"])).ToLowerInvariant();
        entries["manifest.json"] = PackTestKit.Utf8(manifest.Replace(oldHash, newHash));
        var repaired = PackReader.Open(PackTestKit.Rezip(entries));
        Assert.True(repaired.IsStructurallyValid, string.Join("; ", repaired.Issues.Select(i => i.Message)));
        Assert.Equal(TrustStatus.Invalid, (await trust.EvaluateAsync(repaired.Manifest!, repaired.CanonicalManifest, repaired.SignatureFile, context, PackLimits.Default)).Status);
    }

    [Fact]
    public void A_pack_with_no_terminology_document_is_unchanged_and_still_valid()
    {
        var opened = PackReader.Open(PackTestKit.Build("no-terms", "1.0.0"));
        Assert.True(opened.IsStructurallyValid);
        Assert.DoesNotContain(opened.Manifest!.Content, c => c.Kind == "Terminology");
        Assert.Null(opened.Document("Terminology"));
    }

    [Fact]
    public void Script_in_a_label_is_refused_by_the_pack_builder_and_by_the_reader()
    {
        var hostile = Doc(("supplier", "<script>alert(1)</script>", "Vendors"));
        // The builder refuses to produce it ...
        var refused = Assert.Throws<InvalidOperationException>(() => Pack(hostile));
        Assert.Contains(PackCodes.ContentUnsafe, refused.Message);
        // ... and a package assembled by hand is refused on opening.
        var entries = PackTestKit.Entries(Pack(Doc(("supplier", "Vendor", "Vendors"))));
        entries["content/terminology.json"] = PackTestKit.Utf8(hostile);
        Assert.Contains(PackReader.Open(PackTestKit.Rezip(entries)).Issues, i => i.Code is PackCodes.ContentUnsafe or PackCodes.HashMismatch);
    }

    [Fact]
    public void The_third_pack_words_validate_and_differ_from_both_shipped_packs()
    {
        var third = TerminologyDocument.Parse(PackTestKit.ThirdPackTerminologyJson());
        Assert.DoesNotContain(third.Check(), m => m.Severity == ValidationSeverity.Error);
        var set = TermSet.Build("en", null, third.Terms, null, null);
        Assert.Equal("Agreement", set.Singular("contract"));
        Assert.Equal("Counterparty", set.Singular("supplier"));
        Assert.Equal("Portfolio", set.Singular("category"));
        Assert.Equal("Agreement Manager", set.Singular("contract.owner"));
        Assert.Equal("Formal Review", set.Singular("legal.review"));
        Assert.Equal("Activation Approval", set.Singular("registration.approval"));
        foreach (var other in new[] { "blank", "jbs-procurement" })
        {
            var json = File.ReadAllText(Path.Combine(RepoRoot.Find(), "templates", "packs", other, "terminology", "terminology.json"));
            var otherSet = TermSet.Build("en", null, TerminologyDocument.Parse(json).Terms, null, null);
            Assert.NotEqual(otherSet.Singular("contract.owner"), set.Singular("contract.owner"));
            Assert.NotEqual(otherSet.Singular("legal.review"), set.Singular("legal.review"));
        }
    }

    [Fact]
    public void The_preview_rows_show_the_pack_words_beside_the_neutral_wording()
    {
        var rows = TerminologyPreview.Rows(TerminologyDocument.Parse(Doc(("supplier", "Vendor", "Vendors"))), TermSet.Neutral());
        var supplier = rows.Single(r => r.Key == "supplier");
        Assert.Equal("Vendor", supplier.Singular);
        Assert.Equal("Counterparty", supplier.NeutralSingular);
        Assert.True(supplier.DiffersFromNeutral);
        Assert.False(rows.Single(r => r.Key == "contract").DiffersFromNeutral);
        Assert.Equal("Counterparty", supplier.Current);
        Assert.Equal(TermCatalogue.All.Count, rows.Count);
    }
}
