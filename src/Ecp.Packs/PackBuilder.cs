using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecp.Packs;

public sealed record PackBuildResult(byte[]? Package, OpenedPackage? Opened, IReadOnlyList<PackIssue> Issues)
{
    public bool Succeeded => Package is not null && Issues.All(i => !i.IsError);
}

/// <summary>
/// Builds a package from a pack source folder. The folder holds human-readable documents and one recipe, <c>pack-source.json</c> (not part of the package), that names the package, its publisher and version, and
/// which source file becomes which content document. The build is deterministic: the same folder always gives the same bytes, so a build can be repeated and compared.
/// </summary>
public static class PackBuilder
{
    public const string RecipeFile = "pack-source.json";

    public static PackBuildResult Build(string sourceDirectory, PackLimits? limits = null)
    {
        limits ??= PackLimits.Default;
        var issues = new List<PackIssue>();
        void Fail(string message, string? entry = null, string? remediation = null) => issues.Add(new PackIssue(PackCodes.ManifestField, PackSeverity.Error, message, entry, remediation ?? "Correct the pack source."));
        PackBuildResult Failed() => new(null, null, issues);

        var root = Path.GetFullPath(sourceDirectory);
        var recipePath = Path.Combine(root, RecipeFile);
        if (!File.Exists(recipePath)) { Fail($"The pack source folder has no {RecipeFile}.", RecipeFile, "Create the recipe (see docs/PACK_AUTHORING.md)."); return Failed(); }

        JsonDocument recipe;
        try { recipe = CanonicalJson.Parse(File.ReadAllBytes(recipePath), limits.MaxJsonDepth); }
        catch (FormatException ex) { Fail("The recipe is not valid JSON. " + ex.Message, RecipeFile); return Failed(); }

        using (recipe)
        {
            var r = recipe.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { Fail("The recipe must be a JSON object.", RecipeFile); return Failed(); }
            string[] allowed = ["formatVersion", "packageId", "packKey", "version", "displayName", "description", "publisher", "createdUtc", "compatibility", "signature", "synthetic", "capabilities", "dependencies", "content"];
            foreach (var p in r.EnumerateObject()) if (!allowed.Contains(p.Name)) Fail($"The recipe field '{p.Name}' is not recognised.", RecipeFile);
            if (issues.Count > 0) return Failed();

            if (!r.TryGetProperty("content", out var contentList) || contentList.ValueKind != JsonValueKind.Array || contentList.GetArrayLength() == 0) { Fail("The recipe must list the content documents.", RecipeFile); return Failed(); }

            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var contentNodes = new JsonArray();
            var required = new SortedSet<string>(StringComparer.Ordinal);
            var optional = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in contentList.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { Fail("Each content item must be an object.", RecipeFile); continue; }
                string? Text(string n) => item.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var source = Text("source"); var path = Text("path"); var kind = Text("kind"); var scope = Text("scope");
                var schema = item.TryGetProperty("schemaVersion", out var sv) && sv.TryGetInt32(out var s) ? s : (int?)null;
                var isRequired = item.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True;
                if (source is null || path is null || kind is null || scope is null || schema is null) { Fail("A content item needs source, path, kind, scope and schemaVersion.", RecipeFile); continue; }
                var (okSource, sourceReason) = PackPaths.Validate(source, limits);
                if (okSource is null) { Fail($"The source path '{source}' is not allowed: {sourceReason}", source); continue; }
                var full = Path.GetFullPath(Path.Combine(root, source));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) { Fail($"The source file '{source}' was not found in the pack source folder.", source); continue; }
                var data = File.ReadAllBytes(full);
                if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) data = data[3..];
                if (entries.ContainsKey(path)) { Fail($"The content path '{path}' is used twice.", path); continue; }
                entries[path] = NormaliseNewlines(data);
                contentNodes.Add(new JsonObject
                {
                    ["path"] = path, ["kind"] = kind, ["scope"] = scope, ["schemaVersion"] = schema.Value, ["required"] = isRequired,
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(entries[path]))
                });
                (isRequired ? required : optional).Add(kind);
            }
            if (issues.Count > 0) return Failed();

            var manifest = new JsonObject
            {
                ["formatVersion"] = PackManifest.CurrentFormatVersion,
                ["packageId"] = Copy(r, "packageId"), ["packKey"] = Copy(r, "packKey"), ["version"] = Copy(r, "version"),
                ["displayName"] = Copy(r, "displayName"), ["description"] = Copy(r, "description"), ["publisher"] = Copy(r, "publisher"),
                ["createdUtc"] = Copy(r, "createdUtc"), ["compatibility"] = Copy(r, "compatibility"), ["signature"] = Copy(r, "signature"),
                ["synthetic"] = Copy(r, "synthetic"),
                ["capabilities"] = Copy(r, "capabilities") ?? new JsonArray(), ["dependencies"] = Copy(r, "dependencies") ?? new JsonArray(),
                ["requiredKinds"] = new JsonArray(required.Select(k => (JsonNode)k).ToArray()), ["optionalKinds"] = new JsonArray(optional.Select(k => (JsonNode)k).ToArray()),
                ["content"] = contentNodes
            };
            byte[] canonical;
            try { canonical = CanonicalJson.Canonicalise(Encoding.UTF8.GetBytes(manifest.ToJsonString()), limits.MaxJsonDepth); }
            catch (FormatException ex) { Fail("The recipe cannot be turned into a manifest. " + ex.Message, RecipeFile); return Failed(); }
            entries[PackPaths.Manifest] = canonical;

            var package = PackZipWriter.Write(entries);
            // Self-check: the package must pass the same reading and hashing a consumer applies, and the version must agree with the pack's own catalogue entry.
            var opened = PackReader.Open(package, limits);
            issues.AddRange(opened.Issues.Where(i => i.IsError));
            if (opened.Manifest is { } m) issues.AddRange(CrossCheckSolutionPack(opened, m));
            return issues.Any(i => i.IsError) ? new PackBuildResult(null, opened, issues) : new PackBuildResult(package, opened, issues);
        }
    }

    private static JsonNode? Copy(JsonElement parent, string name) => parent.TryGetProperty(name, out var v) ? JsonNode.Parse(v.GetRawText()) : null;

    /// <summary>Line endings are normalised to LF so a pack source checked out on Windows builds the same bytes as on Linux.</summary>
    private static byte[] NormaliseNewlines(byte[] data)
    {
        if (!data.Contains((byte)'\r')) return data;
        var list = new List<byte>(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == (byte)'\r') { if (i + 1 < data.Length && data[i + 1] == (byte)'\n') continue; list.Add((byte)'\n'); }
            else list.Add(data[i]);
        }
        return [.. list];
    }

    /// <summary>The pack's own catalogue entry (the SolutionPack document) must agree with the manifest about key and version, so the two cannot drift.</summary>
    public static IEnumerable<PackIssue> CrossCheckSolutionPack(OpenedPackage package, PackManifest manifest)
    {
        var json = package.Document(PackReader.SolutionPackKind);
        if (json is null) { yield return new PackIssue(PackCodes.RequiredKindMissing, PackSeverity.Error, "The package has no SolutionPack document (the pack's catalogue entry).", null, "Add content/solution-pack.json."); yield break; }
        string? key = null, version = null; PackIssue? problem = null;
        try
        {
            using var doc = CanonicalJson.Parse(Encoding.UTF8.GetBytes(json));
            key = doc.RootElement.TryGetProperty("packId", out var k) ? k.GetString() : null;
            version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (FormatException ex) { problem = new PackIssue(PackCodes.ContentInvalid, PackSeverity.Error, "The SolutionPack document is not valid JSON. " + ex.Message, null); }
        if (problem is not null) { yield return problem; yield break; }
        if (!string.Equals(key, manifest.PackKey, StringComparison.Ordinal))
            yield return new PackIssue(PackCodes.ContentMismatch, PackSeverity.Error, $"The SolutionPack document names the pack '{key}' but the manifest says '{manifest.PackKey}'.", null, "Make the pack key the same in both.");
        if (!string.Equals(version, manifest.Version, StringComparison.Ordinal))
            yield return new PackIssue(PackCodes.ContentMismatch, PackSeverity.Error, $"The SolutionPack document says version '{version}' but the manifest says '{manifest.Version}'.", null, "Make the version the same in both.");
    }

    /// <summary>Adds (or replaces) the detached signature over the canonical manifest. The rest of the package is unchanged byte for byte.</summary>
    public static byte[] Sign(OpenedPackage package, IPackageSigner signer, DateTime signedUtc)
    {
        if (!package.IsStructurallyValid) throw new InvalidOperationException("The package is not valid, so it will not be signed.");
        var signature = new PackSignature(1, signer.Algorithm, signer.KeyId, signedUtc, signer.Sign(SigningInput.For(package.CanonicalManifest)));
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PackPaths.Manifest] = package.Bytes.Length == 0 ? [] : ManifestBytes(package) };
        foreach (var (path, data) in package.Content) entries[path] = data;
        entries[PackPaths.Signature] = PackSignatureDocument.Serialize(signature);
        return PackZipWriter.Write(entries);
    }

    private static byte[] ManifestBytes(OpenedPackage package)
    {
        // Re-read the stored manifest bytes from the archive so signing never rewrites them.
        var archive = PackZipReader.Read(package.Bytes, PackLimits.Default);
        return archive.Entries[PackPaths.Manifest];
    }

    /// <summary>The package without its signature: what a reproducible build produces, and what two builds are compared on.</summary>
    public static byte[] Unsigned(OpenedPackage package)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [PackPaths.Manifest] = ManifestBytes(package) };
        foreach (var (path, data) in package.Content) entries[path] = data;
        return PackZipWriter.Write(entries);
    }
}
