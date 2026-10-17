using System.Security.Cryptography;
using System.Text;
using Ecp.Packs;
using Ecp.SharedKernel;

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

    /// <summary>
    /// The terminology of the synthetic third pack: deliberately different words from the neutral wording and from the reference pack, so a page that still shows a fixed word is caught. Everything not listed
    /// here keeps its neutral wording (a complete document is required, so the rest is written out).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Singular, string Plural)> ThirdPackWords = new Dictionary<string, (string, string)>
    {
        ["application.name"] = ("Agreement Hub", "Agreement Hub"),
        ["contract"] = ("Agreement", "Agreements"),
        ["agreement"] = ("Agreement", "Agreements"),
        ["contract.register"] = ("Agreement Ledger", "Agreement Ledgers"),
        ["contract.reference"] = ("Agreement Number", "Agreement Numbers"),
        ["contract.type"] = ("Agreement Kind", "Agreement Kinds"),
        ["supplier"] = ("Counterparty", "Counterparties"),
        ["category"] = ("Portfolio", "Portfolios"),
        ["subcategory"] = ("Sub-portfolio", "Sub-portfolios"),
        ["contract.owner"] = ("Agreement Manager", "Agreement Managers"),
        ["contract.administrator"] = ("Agreement Steward", "Agreement Stewards"),
        ["reviewer"] = ("Formal Reviewer", "Formal Reviewers"),
        ["review.team"] = ("Formal Review Panel", "Formal Review Panels"),
        ["legal.review"] = ("Formal Review", "Formal Reviews"),
        ["registration.approval"] = ("Activation Approval", "Activation Approvals"),
        ["primary.approver"] = ("Activation Approver", "Activation Approvers"),
        ["registered.date"] = ("Activation Date", "Activation Dates"),
        ["executed.document"] = ("Signed Document", "Signed Documents"),
        ["organisation"] = ("Business", "Businesses"),
        ["site"] = ("Location", "Locations"),
    };

    /// <summary>A complete terminology document (every platform key) with the third pack's words over the neutral wording.</summary>
    public static string ThirdPackTerminologyJson()
    {
        var terms = TermCatalogue.All.Select(d =>
        {
            var (singular, plural) = ThirdPackWords.TryGetValue(d.Key, out var w) ? w : (d.Singular, d.Plural);
            return new { key = d.Key, locale = "en", singular, plural };
        });
        // The third pack also names its approver group and runs a business with no divisions or sites, so it hides that administrator role from pickers (it removes no access).
        var groups = new[] { new { key = "Approvers", locale = "en", name = "Activation Approvers", description = "The people who approve activation of an executed agreement." } };
        var roles = new[] { new { code = "DivisionSiteAdministrator", offered = false } };
        return System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, mode = "complete", terms, groups, roles });
    }

    /// <summary>
    /// The content of the synthetic third pack: the Blank pack's neutral workflows with the first state renamed to one that exists nowhere in the platform or the shipped packs, and the third pack's own terminology.
    /// A test builds a pack from it at run time, so a pack the repository has never seen is installed with no source change.
    /// </summary>
    public static IEnumerable<(string Source, string Path, string Kind, string Scope, int Schema, bool Required)> ThirdPackContent(out Dictionary<string, string> documents)
    {
        var root = Path.Combine(RepoRoot.Find(), "templates", "packs", "blank");
        var lifecycle = File.ReadAllText(Path.Combine(root, "workflows", "contract-lifecycle.json"));
        lifecycle = System.Text.RegularExpressions.Regex.Replace(lifecycle, "\"code\": \"Open\", \"display\": \"Open\"", "\"code\": \"Intake\", \"display\": \"Intake\"");
        lifecycle = System.Text.RegularExpressions.Regex.Replace(lifecycle, "\"from\": \\[ \"Open\" \\]", "\"from\": [ \"Intake\" ]");
        if (!lifecycle.Contains("\"Intake\"")) throw new InvalidOperationException("The Blank lifecycle no longer has the expected initial state.");
        documents = new Dictionary<string, string>
        {
            ["lifecycle.json"] = lifecycle,
            ["variation.json"] = File.ReadAllText(Path.Combine(root, "workflows", "variation.json")),
            ["notifications.json"] = File.ReadAllText(Path.Combine(root, "notifications", "notification-policy.json")),
            ["terminology.json"] = ThirdPackTerminologyJson(),
        };
        return
        [
            ("lifecycle.json", "content/contract-lifecycle.json", "WorkflowContractLifecycle", "organisation", 1, true),
            ("variation.json", "content/variation-workflow.json", "WorkflowVariation", "organisation", 1, true),
            ("notifications.json", "content/notification-policy.json", "NotificationPolicy", "organisation", 1, true),
            TerminologyEntry(),
        ];
    }

    /// <summary>The content entry that carries a terminology document in a pack.</summary>
    public static (string Source, string Path, string Kind, string Scope, int Schema, bool Required) TerminologyEntry(string source = "terminology.json")
        => (source, "content/terminology.json", "Terminology", "organisation", 1, false);
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
