using System.Security.Cryptography;
using System.Text;
using Ecp.Packs;

namespace Ecp.Tests.Common;

/// <summary>An ephemeral ECDSA P-256 signing key for tests. It exists only in memory (or a temp file the test deletes) and is never committed.</summary>
public sealed class TestSigningKey : IDisposable
{
    public string KeyId { get; }
    public string PrivatePem { get; }
    public byte[] PublicSpki { get; }
    public EcdsaP256Signer Signer { get; }

    public TestSigningKey(string keyId = "test-key-1")
    {
        KeyId = keyId;
        (PrivatePem, PublicSpki) = PackKeys.Generate();
        Signer = EcdsaP256Signer.FromPem(PrivatePem, keyId);
    }

    public TrustedKey AsTrusted(TrustedKeyStatus status = TrustedKeyStatus.Active, DateTime? notBefore = null, DateTime? notAfter = null, string source = "test")
        => new(KeyId, SignatureAlgorithms.Es256, PublicSpki, status, notBefore, notAfter, source, "test key");

    public void Dispose() => Signer.Dispose();
}

/// <summary>Builds small valid packs for tests, in memory, through the real builder. Nothing here is a business: the packs are neutral and synthetic.</summary>
public static class PackTestKit
{
    public const string NeutralLifecycle = """
        { "schemaVersion": 1 }
        """;

    /// <summary>Writes a pack source folder (recipe plus documents) into <paramref name="directory"/> and returns the folder.</summary>
    public static string WriteSource(string directory, string packKey = "sample-pack", string version = "1.0.0", bool synthetic = true, string requirement = "AllowUnsignedSynthetic",
        IReadOnlyDictionary<string, string>? extraDocuments = null, string minPlatform = "1.0.0", string? maxPlatform = null, bool ignoreUnknownOptional = false, string? packageId = null,
        IEnumerable<(string Source, string Path, string Kind, string Scope, int Schema, bool Required)>? content = null, string? solutionPackJson = null,
        string displayName = "Sample pack", string description = "A neutral sample pack for tests.", string publisherName = "Test publisher")
    {
        Directory.CreateDirectory(directory);
        solutionPackJson ??= $$"""
            { "packId": "{{packKey}}", "name": "Sample pack", "version": "{{version}}", "synthetic": {{(synthetic ? "true" : "false")}}, "publisher": "Test publisher",
              "description": "A neutral sample pack for tests.", "features": [], "ownershipModel": "ReadAllEditAssignedCategory", "approvalModel": "None",
              "defaults": { "classification": { "levels": ["Public", "Internal", "Confidential", "Restricted", "Legal Privileged"], "defaultLevel": "Internal", "privilegedLevels": ["Legal Privileged"] }, "contractTypes": [], "categories": [] } }
            """;
        File.WriteAllText(Path.Combine(directory, "pack.json"), solutionPackJson);
        var items = new List<(string, string, string, string, int, bool)> { ("pack.json", "content/solution-pack.json", "SolutionPack", "catalogue", 1, true) };
        if (content is not null) items.AddRange(content);
        foreach (var (name, text) in extraDocuments ?? new Dictionary<string, string>()) File.WriteAllText(Path.Combine(directory, name), text);
        var contentJson = string.Join(",", items.Select(i => $$"""{ "source": "{{i.Item1}}", "path": "{{i.Item2}}", "kind": "{{i.Item3}}", "scope": "{{i.Item4}}", "schemaVersion": {{i.Item5}}, "required": {{(i.Item6 ? "true" : "false")}} }"""));
        File.WriteAllText(Path.Combine(directory, PackBuilder.RecipeFile), $$"""
            {
              "packageId": "{{packageId ?? "test.pack." + packKey}}", "packKey": "{{packKey}}", "version": "{{version}}",
              "displayName": { "en-AU": {{System.Text.Json.JsonSerializer.Serialize(displayName)}} }, "description": { "en-AU": {{System.Text.Json.JsonSerializer.Serialize(description)}} },
              "publisher": { "id": "test.publisher", "name": {{System.Text.Json.JsonSerializer.Serialize(publisherName)}} }, "createdUtc": "2026-10-20T00:00:00Z",
              "compatibility": { "minPlatformVersion": "{{minPlatform}}", "maxPlatformVersion": {{(maxPlatform is null ? "null" : "\"" + maxPlatform + "\"")}}, "ignoreUnknownOptionalKinds": {{(ignoreUnknownOptional ? "true" : "false")}} },
              "signature": { "requirement": "{{requirement}}" }, "synthetic": {{(synthetic ? "true" : "false")}}, "capabilities": [], "dependencies": [],
              "content": [ {{contentJson}} ]
            }
            """);
        return directory;
    }

    public static string TempDirectory() => Path.Combine(Path.GetTempPath(), "ecp-pack-" + Guid.NewGuid().ToString("N"));

    /// <summary>Builds an unsigned package from a freshly written neutral source folder. The folder is removed afterwards.</summary>
    public static byte[] Build(string packKey = "sample-pack", string version = "1.0.0", bool synthetic = true, string requirement = "AllowUnsignedSynthetic",
        IReadOnlyDictionary<string, string>? extraDocuments = null, string minPlatform = "1.0.0", string? maxPlatform = null, bool ignoreUnknownOptional = false,
        IEnumerable<(string Source, string Path, string Kind, string Scope, int Schema, bool Required)>? content = null, string? solutionPackJson = null,
        string displayName = "Sample pack", string description = "A neutral sample pack for tests.", string publisherName = "Test publisher")
    {
        var dir = TempDirectory();
        try
        {
            WriteSource(dir, packKey, version, synthetic, requirement, extraDocuments, minPlatform, maxPlatform, ignoreUnknownOptional, null, content, solutionPackJson, displayName, description, publisherName);
            var result = PackBuilder.Build(dir);
            if (!result.Succeeded) throw new InvalidOperationException("Test pack did not build: " + string.Join("; ", result.Issues.Select(i => i.Code + " " + i.Message)));
            return result.Package!;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    public static byte[] Sign(byte[] unsignedPackage, TestSigningKey key, DateTime? signedUtc = null)
        => PackBuilder.Sign(PackReader.Open(unsignedPackage), key.Signer, signedUtc ?? new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>Rebuilds a package from raw entries (to craft tampered or hostile packages).</summary>
    public static byte[] Rezip(IReadOnlyDictionary<string, byte[]> entries) => PackZipWriter.Write(entries);

    public static Dictionary<string, byte[]> Entries(byte[] package) => PackZipReader.Read(package, new PackLimits { MaxEntries = 1000 }).Entries.ToDictionary(e => e.Key, e => e.Value);

    public static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);
}

/// <summary>
/// The shipped packs for test hosts: built once per test process from the pack sources under templates/packs, exactly as the build builds them for the application. Two folders: the plain build (unsigned, for Demo
/// and Test hosts that allow unsigned synthetic packs) and a copy signed by a throw-away key (for Production hosts, which trust only that key). The key exists only in this process and is never written to the repository.
/// </summary>
public static class TestShippedPacks
{
    private static readonly Lazy<(string Unsigned, string Signed, TestSigningKey Key, string PublicPem)> Built = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string UnsignedDirectory => Built.Value.Unsigned;
    public static string SignedDirectory => Built.Value.Signed;
    public static TestSigningKey SigningKey => Built.Value.Key;
    public static string PublicKeyPem => Built.Value.PublicPem;
    public const string KeyId = "test-process-key";

    private static (string, string, TestSigningKey, string) Build()
    {
        var root = Path.Combine(Path.GetTempPath(), "ecp-test-packs-" + Guid.NewGuid().ToString("N"));
        var plain = Path.Combine(root, "unsigned"); var signed = Path.Combine(root, "signed");
        Directory.CreateDirectory(plain); Directory.CreateDirectory(signed);
        var key = new TestSigningKey(KeyId);
        foreach (var source in Directory.EnumerateDirectories(Path.Combine(RepoRoot.Find(), "templates", "packs")).Where(d => File.Exists(Path.Combine(d, PackBuilder.RecipeFile))))
        {
            var result = PackBuilder.Build(source);
            if (!result.Succeeded) throw new InvalidOperationException($"Shipped pack source '{Path.GetFileName(source)}' did not build: " + string.Join("; ", result.Issues.Select(i => i.Code + " " + i.Message)));
            var m = result.Opened!.Manifest!;
            var name = $"{m.PackKey}-{m.Version}.ecpkg";
            File.WriteAllBytes(Path.Combine(plain, name), result.Package!);
            File.WriteAllBytes(Path.Combine(signed, name), PackBuilder.Sign(result.Opened, key.Signer, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc)));
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(root, true); } catch { } };
        return (plain, signed, key, PackKeys.ToPublicPem(key.PublicSpki));
    }
}
