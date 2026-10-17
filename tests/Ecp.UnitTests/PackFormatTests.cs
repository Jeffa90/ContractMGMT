using System.IO.Compression;
using System.Text;
using Ecp.Packs;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

/// <summary>Package format, safe archive reading, canonical JSON, deterministic building and content safety (S7a/S7b; PKG-004, PKG-006, PKG-011).</summary>
[Trait("Category", "Unit")]
public sealed class PackFormatTests
{
    // ---- canonical JSON --------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Canonical_form_sorts_members_removes_whitespace_and_escapes_minimally()
    {
        var canonical = Encoding.UTF8.GetString(CanonicalJson.Canonicalise(Encoding.UTF8.GetBytes("{ \"b\": 1, \"a\": [ true, null, \"x\\u0041\\n\" ], \"c\": { \"z\": 2, \"y\": 3 } }")));
        Assert.Equal("{\"a\":[true,null,\"xA\\n\"],\"b\":1,\"c\":{\"y\":3,\"z\":2}}", canonical);
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")]          // duplicate member
    [InlineData("{\"a\":1.5}")]                // fraction
    [InlineData("{\"a\":1e3}")]                // exponent
    [InlineData("{\"a\":1,}")]                 // trailing comma
    [InlineData("{/*c*/\"a\":1}")]             // comment
    public void Canonical_form_refuses_what_a_manifest_must_not_contain(string json)
        => Assert.Throws<FormatException>(() => CanonicalJson.Canonicalise(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Two_spellings_of_the_same_document_have_the_same_canonical_bytes()
        => Assert.Equal(CanonicalJson.Canonicalise(Encoding.UTF8.GetBytes("{\"b\":[1,2],\"a\":\"é\"}")), CanonicalJson.Canonicalise(Encoding.UTF8.GetBytes("\uFEFF{\n  \"a\": \"é\",\n  \"b\": [ 1, 2 ]\n}")));

    // ---- paths and versions ----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("../etc/passwd")] [InlineData("/etc/passwd")] [InlineData("content/../x.json")] [InlineData("content\\x.json")] [InlineData("C:/x.json")] [InlineData("content//x.json")]
    [InlineData(".hidden.json")] [InlineData("content/.x.json")] [InlineData("content/a b.json")] [InlineData("content/é.json")] [InlineData("a/b/c/d.json")] [InlineData("content/x.")]
    public void Unsafe_paths_are_refused(string path) => Assert.NotNull(PackPaths.Validate(path, PackLimits.Default).Reason);

    [Theory]
    [InlineData("manifest.json", true)] [InlineData("signature.json", true)] [InlineData("content/solution-pack.json", true)]
    [InlineData("content/x.exe", false)] [InlineData("content/sub/x.json", false)] [InlineData("other/x.json", false)] [InlineData("readme.md", false)]
    public void Only_the_package_layout_is_an_allowed_location(string path, bool allowed) => Assert.Equal(allowed, PackPaths.IsAllowedLocation(path));

    [Fact]
    public void Semantic_versions_order_and_pre_releases_come_before_releases()
    {
        Assert.True(SemVer.Parse("1.2.10") > SemVer.Parse("1.2.9"));
        Assert.True(SemVer.Parse("2.0.0") > SemVer.Parse("1.99.99"));
        Assert.True(SemVer.Parse("1.0.0-rc.1") < SemVer.Parse("1.0.0"));
        Assert.False(SemVer.TryParse("1.0", out _));
        Assert.False(SemVer.TryParse("01.0.0", out _));
        Assert.Equal(0, SemVer.Parse("1.0.0").CompareTo(SemVer.Parse("1.0.0+build7")));
    }

    // ---- building --------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_build_is_deterministic_and_changes_when_content_changes()
    {
        var a = PackTestKit.Build(); var b = PackTestKit.Build();
        Assert.True(a.AsSpan().SequenceEqual(b));
        var c = PackTestKit.Build(version: "1.0.1");
        Assert.False(a.AsSpan().SequenceEqual(c));
        Assert.Equal(PackReader.Open(a).Sha256, PackReader.Open(b).Sha256);
    }

    [Fact]
    public void The_zip_writer_does_not_depend_on_the_order_entries_are_given()
    {
        var one = PackZipWriter.Write(new Dictionary<string, byte[]> { ["b.json"] = [1], ["a.json"] = [2] });
        var two = PackZipWriter.Write(new Dictionary<string, byte[]> { ["a.json"] = [2], ["b.json"] = [1] });
        Assert.True(one.AsSpan().SequenceEqual(two));
    }

    [Fact]
    public void A_built_package_opens_with_a_valid_manifest_and_verified_hashes()
    {
        var opened = PackReader.Open(PackTestKit.Build(packKey: "alpha-pack", version: "2.3.4"));
        Assert.True(opened.IsStructurallyValid, string.Join("; ", opened.Issues.Select(i => i.Message)));
        Assert.Equal("alpha-pack", opened.Manifest!.PackKey);
        Assert.Equal("2.3.4", opened.Manifest.Version);
        Assert.Single(opened.Content);
        Assert.Equal(64, opened.Sha256.Length);
        Assert.NotEqual(opened.Sha256, opened.ContentDigest);
        Assert.Contains("alpha-pack", opened.Document(PackReader.SolutionPackKind)!);
    }

    [Fact]
    public void The_pack_catalogue_entry_must_agree_with_the_manifest_about_key_and_version()
    {
        var dir = PackTestKit.TempDirectory();
        try
        {
            PackTestKit.WriteSource(dir, packKey: "alpha-pack", version: "1.0.0", solutionPackJson: """{ "packId": "other-pack", "name": "x", "version": "9.9.9" }""");
            var result = PackBuilder.Build(dir);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Issues, i => i.Code == PackCodes.ContentMismatch);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_source_file_outside_the_pack_folder_cannot_be_included()
    {
        var dir = PackTestKit.TempDirectory();
        try
        {
            PackTestKit.WriteSource(dir, content: [("../secret.json", "content/leak.json", "WorkflowVariation", "organisation", 1, false)]);
            var result = PackBuilder.Build(dir);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Issues, i => i.Entry == "../secret.json");
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- hostile archives ------------------------------------------------------------------------------------------------------------------------------

    private static byte[] Zip(params (string Name, byte[] Data, Action<ZipArchiveEntry>? Tweak)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (name, data, tweak) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using (var s = entry.Open()) s.Write(data);
                tweak?.Invoke(entry);
            }
        return ms.ToArray();
    }

    private static PackZipResult Read(byte[] bytes, PackLimits? limits = null) => PackZipReader.Read(bytes, limits ?? PackLimits.Default);
    private static void AssertRefused(PackZipResult r, string code) { Assert.False(r.IsSafe); Assert.Contains(r.Issues, i => i.Code == code); }

    [Fact] public void Path_traversal_is_refused() => AssertRefused(Read(Zip(("content/../../evil.json", [1], null))), PackCodes.UnsafePath);
    [Fact] public void An_absolute_path_is_refused() => AssertRefused(Read(Zip(("/etc/passwd", [1], null))), PackCodes.UnsafePath);
    [Fact] public void A_backslash_path_is_refused() => AssertRefused(Read(Zip(("content\\x.json", [1], null))), PackCodes.UnsafePath);
    [Fact] public void A_directory_entry_is_refused() => AssertRefused(Read(Zip(("content/", [], null))), PackCodes.UnsafeFeature);
    [Fact] public void A_duplicate_entry_is_refused() => AssertRefused(Read(Zip(("content/a.json", [1], null), ("content/a.json", [2], null))), PackCodes.DuplicatePath);
    [Fact] public void Names_that_differ_only_by_case_are_duplicates() => AssertRefused(Read(Zip(("content/a.json", [1], null), ("content/A.json", [2], null))), PackCodes.DuplicatePath);

    [Fact]
    public void A_symbolic_link_is_refused()
        => AssertRefused(Read(Zip(("content/link.json", [1], e => e.ExternalAttributes = unchecked((int)0xA1FF0000)))), PackCodes.UnsafeFeature);

    [Fact]
    public void An_encrypted_entry_is_refused()
    {
        var zip = Zip(("content/a.json", [1, 2, 3], null));
        // Set the "encrypted" bit in the central directory header of the only entry.
        var cd = BitConverter.ToUInt32(zip, zip.Length - 22 + 16);
        zip[cd + 8] |= 0x01;
        AssertRefused(Read(zip), PackCodes.UnsafeFeature);
    }

    [Fact]
    public void An_archive_comment_is_refused()
    {
        var zip = Zip(("content/a.json", [1], null)).Concat(new byte[] { 0 }).ToArray();
        Assert.False(Read(zip).IsSafe);
    }

    [Fact]
    public void Too_many_entries_are_refused() => AssertRefused(Read(Zip(("content/a.json", [1], null), ("content/b.json", [1], null), ("content/c.json", [1], null)), new PackLimits { MaxEntries = 2 }), PackCodes.TooManyEntries);

    [Fact]
    public void An_oversized_entry_is_refused_before_it_is_expanded()
    {
        var big = new byte[600_000];
        var zip = Zip(("content/a.json", big, null));
        AssertRefused(Read(zip, new PackLimits { MaxEntryBytes = 100_000 }), PackCodes.EntryTooLarge);
    }

    [Fact]
    public void A_compression_bomb_is_refused_by_its_ratio()
    {
        var zeros = new byte[4_000_000];
        var zip = Zip(("content/a.json", zeros, null));
        Assert.True(zip.Length < 20_000);
        AssertRefused(Read(zip, new PackLimits { MaxEntryBytes = 5_000_000, MaxTotalExpandedBytes = 6_000_000, MaxCompressionRatio = 50, RatioFloorBytes = 1024 }), PackCodes.CompressionRatio);
    }

    [Fact]
    public void A_total_expansion_over_the_limit_is_refused()
        => AssertRefused(Read(Zip(("content/a.json", new byte[1_500_000], null), ("content/b.json", new byte[1_500_000], null)), new PackLimits { MaxTotalExpandedBytes = 2_000_000, MaxCompressionRatio = 100000 }), PackCodes.ExpandedTooLarge);

    [Fact]
    public void An_entry_whose_data_disagrees_with_its_checksum_is_refused()
    {
        var zip = PackZipWriter.Write(new Dictionary<string, byte[]> { ["content/a.json"] = Encoding.ASCII.GetBytes("{\"a\":1}") });
        zip[30 + "content/a.json".Length + 2] ^= 0xFF;     // change a data byte
        AssertRefused(Read(zip), PackCodes.EntryCorrupt);
    }

    [Fact] public void Something_that_is_not_an_archive_is_refused() => AssertRefused(Read(Encoding.ASCII.GetBytes("MZ this is not a zip file at all, just text.")), PackCodes.NotAnArchive);
    [Fact] public void An_oversized_package_is_refused() => AssertRefused(Read(new byte[200], new PackLimits { MaxPackageBytes = 100 }), PackCodes.TooLarge);

    [Fact]
    public void Extra_files_in_unexpected_places_are_refused_even_when_the_archive_is_otherwise_sound()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        entries["content/tool.exe"] = [1, 2, 3];
        entries["docs/readme.md"] = [4];
        var opened = PackReader.Open(PackTestKit.Rezip(entries));
        Assert.Contains(opened.Issues, i => i.Code == PackCodes.DisallowedEntry && i.Entry == "content/tool.exe");
        Assert.Contains(opened.Issues, i => i.Code == PackCodes.DisallowedEntry && i.Entry == "docs/readme.md");
        Assert.False(opened.IsStructurallyValid);
    }

    [Fact]
    public void A_document_the_manifest_does_not_list_is_refused()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        entries["content/extra.json"] = PackTestKit.Utf8("{}");
        Assert.Contains(PackReader.Open(PackTestKit.Rezip(entries)).Issues, i => i.Code == PackCodes.UnlistedEntry);
    }

    [Fact]
    public void A_listed_document_that_is_missing_is_refused()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        entries.Remove("content/solution-pack.json");
        Assert.Contains(PackReader.Open(PackTestKit.Rezip(entries)).Issues, i => i.Code == PackCodes.ContentMissing);
    }

    [Fact]
    public void Changed_content_no_longer_matches_its_hash()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        entries["content/solution-pack.json"] = PackTestKit.Utf8(Encoding.UTF8.GetString(entries["content/solution-pack.json"]).Replace("Sample pack", "Evil pack"));
        var opened = PackReader.Open(PackTestKit.Rezip(entries));
        Assert.Contains(opened.Issues, i => i.Code == PackCodes.HashMismatch);
        Assert.False(opened.IsStructurallyValid);
    }

    [Fact]
    public void A_package_without_a_manifest_is_refused()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        entries.Remove("manifest.json");
        Assert.Contains(PackReader.Open(PackTestKit.Rezip(entries)).Issues, i => i.Code == PackCodes.ManifestMissing);
    }

    [Fact]
    public void A_manifest_with_an_unknown_field_or_a_bad_value_is_refused()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        var manifest = Encoding.UTF8.GetString(entries["manifest.json"]);
        entries["manifest.json"] = PackTestKit.Utf8(manifest.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"run\":\"calc.exe\"").Replace("\"version\":\"1.0.0\"", "\"version\":\"one\""));
        var opened = PackReader.Open(PackTestKit.Rezip(entries));
        Assert.Contains(opened.Issues, i => i.Code == PackCodes.ManifestField && i.Message.Contains("run", StringComparison.Ordinal));
        Assert.Contains(opened.Issues, i => i.Code == PackCodes.ManifestField && i.Message.Contains("version", StringComparison.Ordinal));
        Assert.Null(opened.Manifest);
    }

    [Fact]
    public void An_unsupported_format_version_is_refused_with_its_own_code()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        entries["manifest.json"] = PackTestKit.Utf8(Encoding.UTF8.GetString(entries["manifest.json"]).Replace("\"formatVersion\":1", "\"formatVersion\":2"));
        Assert.Contains(PackReader.Open(PackTestKit.Rezip(entries)).Issues, i => i.Code == PackCodes.FormatUnsupported);
    }

    [Fact]
    public void A_manifest_that_is_not_in_canonical_form_is_accepted_and_noted_because_signatures_use_the_canonical_form()
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        var canonical = entries["manifest.json"];
        using var doc = System.Text.Json.JsonDocument.Parse(canonical);
        entries["manifest.json"] = PackTestKit.Utf8(System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var opened = PackReader.Open(PackTestKit.Rezip(entries));
        Assert.True(opened.IsStructurallyValid);
        Assert.Contains(opened.Issues, i => i.Code == PackCodes.ManifestNotCanonical);
        Assert.True(opened.CanonicalManifest.AsSpan().SequenceEqual(canonical));
    }

    // ---- content safety --------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("{\"$type\":\"System.Diagnostics.Process, System\"}")]
    [InlineData("{\"text\":\"<script>alert(1)</script>\"}")]
    [InlineData("{\"text\":\"JavaScript:alert(1)\"}")]
    [InlineData("{\"link\":\"https://example.com/payload\"}")]
    [InlineData("{\"type\":\"MyApp.Evil.Handler, MyApp.Evil, Version=1.0.0.0\"}")]
    [InlineData("{\"onload\":\"<img src=x onerror=alert(1)>\"}")]
    public void Content_that_could_carry_code_or_reach_a_host_is_refused(string document)
    {
        var entries = PackTestKit.Entries(PackTestKit.Build());
        var dir = PackTestKit.TempDirectory();
        try
        {
            PackTestKit.WriteSource(dir, extraDocuments: new Dictionary<string, string> { ["lifecycle.json"] = document },
                content: [("lifecycle.json", "content/contract-lifecycle.json", "WorkflowContractLifecycle", "organisation", 1, false)]);
            var result = PackBuilder.Build(dir);
            Assert.False(result.Succeeded);
            Assert.Contains(result.Issues, i => i.Code == PackCodes.ContentUnsafe);
        }
        finally { Directory.Delete(dir, true); }
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void Ordinary_text_with_angle_brackets_and_words_like_script_is_not_mistaken_for_active_content()
    {
        var findings = PackReader.ScanDocument("content/x.json", Encoding.UTF8.GetBytes("{\"body\":\"Use {{link}} to open it. The Scripture reading is on page <3>.\"}"), PackLimits.Default);
        Assert.Empty(findings);
    }

    // ---- compatibility ---------------------------------------------------------------------------------------------------------------------------------

    private static PackCompatibilityContext Context(string platform = "1.0.0", params string[] kinds) => new(SemVer.Parse(platform), kinds.ToHashSet(StringComparer.Ordinal));

    [Fact]
    public void The_platform_version_must_be_inside_the_declared_range()
    {
        var needsNewer = PackReader.Open(PackTestKit.Build(minPlatform: "2.0.0"));
        Assert.Contains(PackReader.CheckCompatibility(needsNewer, Context("1.5.0")), i => i.Code == PackCodes.PlatformTooOld);
        var tooNew = PackReader.Open(PackTestKit.Build(maxPlatform: "1.9.9"));
        Assert.Contains(PackReader.CheckCompatibility(tooNew, Context("2.0.0")), i => i.Code == PackCodes.PlatformTooNew);
        Assert.DoesNotContain(PackReader.CheckCompatibility(tooNew, Context("1.5.0")), i => i.IsError);
    }

    private static OpenedPackage WithKind(bool required, bool ignoreUnknownOptional)
        => PackReader.Open(PackTestKit.Build(ignoreUnknownOptional: ignoreUnknownOptional, extraDocuments: new Dictionary<string, string> { ["future.json"] = "{\"schemaVersion\":1}" },
            content: [("future.json", "content/future.json", "FutureKind", "organisation", 1, required)]));

    [Fact] public void An_unknown_required_kind_is_an_error() => Assert.Contains(PackReader.CheckCompatibility(WithKind(true, false), Context(kinds: "WorkflowVariation")), i => i.Code == PackCodes.KindUnknownRequired && i.IsError);
    [Fact] public void An_unknown_optional_kind_is_ignored_with_a_warning_only_when_the_manifest_permits_it()
    {
        var permitted = PackReader.CheckCompatibility(WithKind(false, true), Context(kinds: "WorkflowVariation"));
        Assert.Contains(permitted, i => i.Code == PackCodes.KindUnknownOptional && i.Severity == PackSeverity.Warning);
        Assert.DoesNotContain(permitted, i => i.IsError);
        Assert.Contains(PackReader.CheckCompatibility(WithKind(false, false), Context(kinds: "WorkflowVariation")), i => i.Code == PackCodes.KindUnknownOptional && i.IsError);
    }

    [Fact]
    public void An_unsupported_schema_version_is_an_error()
    {
        var pack = PackReader.Open(PackTestKit.Build(extraDocuments: new Dictionary<string, string> { ["v.json"] = "{\"schemaVersion\":7}" }, content: [("v.json", "content/variation-workflow.json", "WorkflowVariation", "organisation", 7, true)]));
        Assert.Contains(PackReader.CheckCompatibility(pack, Context(kinds: "WorkflowVariation")), i => i.Code == PackCodes.SchemaUnsupported);
    }

    [Fact]
    public void A_document_whose_own_schema_version_disagrees_with_the_manifest_is_an_error()
    {
        var pack = PackReader.Open(PackTestKit.Build(extraDocuments: new Dictionary<string, string> { ["v.json"] = "{\"schemaVersion\":2}" }, content: [("v.json", "content/variation-workflow.json", "WorkflowVariation", "organisation", 1, true)]));
        Assert.Contains(PackReader.CheckCompatibility(pack, Context(kinds: "WorkflowVariation")), i => i.Code == PackCodes.SchemaUnsupported && i.Message.Contains("document says", StringComparison.Ordinal));
    }
}
