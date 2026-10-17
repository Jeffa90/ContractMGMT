using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ecp.Packs;

/// <summary>A package that has been read and checked structurally: safe archive, parsed manifest, every declared hash verified. Trust (the signature) and compatibility are separate questions, answered separately.</summary>
public sealed class OpenedPackage
{
    public required byte[] Bytes { get; init; }
    public required string Sha256 { get; init; }
    public PackManifest? Manifest { get; init; }
    public byte[] CanonicalManifest { get; init; } = [];
    public byte[]? SignatureFile { get; init; }
    public IReadOnlyDictionary<string, byte[]> Content { get; init; } = new Dictionary<string, byte[]>();
    public required IReadOnlyList<PackIssue> Issues { get; init; }

    public bool IsStructurallyValid => Manifest is not null && Issues.All(i => !i.IsError);

    /// <summary>SHA-256 of the canonical manifest. The manifest lists every content hash, so this identifies the package's content independently of its signature.</summary>
    public string ContentDigest => CanonicalManifest.Length == 0 ? "" : Convert.ToHexStringLower(SHA256.HashData(CanonicalManifest));

    public string? Document(string kind)
    {
        var entry = Manifest?.Content.FirstOrDefault(c => c.Kind == kind);
        return entry is not null && Content.TryGetValue(entry.Path, out var bytes) ? new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿') : null;
    }
}

public sealed record PackCompatibilityContext(SemVer PlatformVersion, IReadOnlySet<string> KnownKinds, Func<string, IReadOnlySet<int>>? SupportedSchemas = null);

public static partial class PackReader
{
    /// <summary>The kind every pack carries for its catalogue entry and defaults. It is not a configuration kind: it describes the pack itself.</summary>
    public const string SolutionPackKind = "SolutionPack";

    [GeneratedRegex(@"<\s*(script|iframe|object|embed)\b|javascript\s*:|vbscript\s*:|data\s*:\s*text/html|\bon(error|load|click)\s*=", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex ActiveContent();
    [GeneratedRegex(@"\b(https?|ftp|file)://", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Url();
    [GeneratedRegex(@"^[A-Za-z0-9_.+`]+\s*,\s*[A-Za-z0-9_.]+\s*(,\s*Version\s*=|$)", RegexOptions.NonBacktracking)]
    private static partial Regex AssemblyQualifiedName();

    public static OpenedPackage Open(byte[] bytes, PackLimits? limits = null)
    {
        limits ??= PackLimits.Default;
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var archive = PackZipReader.Read(bytes, limits);
        var issues = new List<PackIssue>(archive.Issues);
        OpenedPackage Result(PackManifest? manifest = null, byte[]? canonical = null, byte[]? signature = null, Dictionary<string, byte[]>? content = null)
            => new() { Bytes = bytes, Sha256 = sha, Manifest = manifest, CanonicalManifest = canonical ?? [], SignatureFile = signature, Content = content ?? [], Issues = issues };
        if (!archive.IsSafe) return Result();

        var entries = archive.Entries;
        foreach (var name in entries.Keys)
            if (!PackPaths.IsAllowedLocation(name))
                issues.Add(new PackIssue(PackCodes.DisallowedEntry, PackSeverity.Error, "The archive contains a file in a place packages do not use.", name, "A package holds manifest.json, signature.json and JSON documents under content/ only."));

        if (!entries.TryGetValue(PackPaths.Manifest, out var manifestBytes))
        {
            issues.Add(new PackIssue(PackCodes.ManifestMissing, PackSeverity.Error, "The package has no manifest.json.", PackPaths.Manifest, "Build the package with the pack tool."));
            return Result();
        }
        var (manifest, manifestIssues) = PackManifestParser.Parse(manifestBytes, limits);
        issues.AddRange(manifestIssues);
        if (manifest is null) return Result();

        byte[] canonical;
        try { canonical = CanonicalJson.Canonicalise(manifestBytes, limits.MaxJsonDepth); }
        catch (FormatException ex)
        {
            issues.Add(new PackIssue(PackCodes.ManifestInvalid, PackSeverity.Error, "The manifest cannot be put into canonical form. " + ex.Message, PackPaths.Manifest, "Build the package with the pack tool."));
            return Result();
        }
        if (!manifestBytes.AsSpan().SequenceEqual(canonical))
            issues.Add(new PackIssue(PackCodes.ManifestNotCanonical, PackSeverity.Info, "The manifest is not written in canonical form. Its signature is checked against the canonical form, so this is harmless.", PackPaths.Manifest));

        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var declared = manifest.Content.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var name in entries.Keys)
            if (name.StartsWith(PackPaths.ContentFolder, StringComparison.Ordinal) && PackPaths.IsAllowedLocation(name) && !declared.Contains(name))
                issues.Add(new PackIssue(PackCodes.UnlistedEntry, PackSeverity.Error, "The archive contains a document the manifest does not list.", name, "Every content document must be listed in the manifest with its hash."));
        foreach (var entry in manifest.Content)
        {
            if (!entries.TryGetValue(entry.Path, out var data))
            {
                issues.Add(new PackIssue(PackCodes.ContentMissing, PackSeverity.Error, "The manifest lists a document that is not in the archive.", entry.Path, "Obtain the package again or rebuild it."));
                continue;
            }
            var actual = Convert.ToHexStringLower(SHA256.HashData(data));
            if (!string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
            {
                issues.Add(new PackIssue(PackCodes.HashMismatch, PackSeverity.Error, "A document's content does not match the hash in the manifest. The package has been changed.", entry.Path, "Do not use this package; obtain it again from the publisher."));
                continue;
            }
            content[entry.Path] = data;
            issues.AddRange(ScanDocument(entry.Path, data, limits));
        }
        entries.TryGetValue(PackPaths.Signature, out var signatureFile);
        return Result(manifest, canonical, signatureFile, content);
    }

    /// <summary>Checks a content document is strict JSON of bounded depth and holds no active content, script, link to a host, or reference to a .NET type. Content is data only.</summary>
    public static IEnumerable<PackIssue> ScanDocument(string path, ReadOnlySpan<byte> data, PackLimits limits)
    {
        var found = new List<PackIssue>();
        try
        {
            using var doc = CanonicalJson.Parse(data, limits.MaxJsonDepth);
            Walk(doc.RootElement, path, found, new HashSet<string>(StringComparer.Ordinal));
        }
        catch (FormatException ex)
        {
            found.Add(new PackIssue(PackCodes.ContentInvalid, PackSeverity.Error, "A content document is not valid JSON. " + ex.Message, path, "Correct the document in the pack source and rebuild the package."));
        }
        return found;
    }

    private static void Walk(JsonElement e, string path, List<PackIssue> found, HashSet<string> reported)
    {
        void Unsafe(string what)
        {
            if (reported.Add(what)) found.Add(new PackIssue(PackCodes.ContentUnsafe, PackSeverity.Error, what, path, "Packs contain configuration data only. Remove it from the pack source."));
        }
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name.StartsWith('$') || p.Name is "__proto__" or "constructor") Unsafe($"The document uses a member name ('{(p.Name.Length > 30 ? p.Name[..30] : p.Name)}') that can select a code type.");
                    Walk(p.Value, path, found, reported);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Walk(item, path, found, reported);
                break;
            case JsonValueKind.String:
                var s = e.GetString() ?? "";
                if (ActiveContent().IsMatch(s)) Unsafe("The document contains script or active content in a text value.");
                else if (Url().IsMatch(s)) Unsafe("The document contains a web address. Packs do not reference external hosts.");
                else if (s.Length > 8 && AssemblyQualifiedName().IsMatch(s)) Unsafe("The document refers to a .NET type by name.");
                break;
        }
    }

    /// <summary>Format, platform range, kinds and schema versions. Kinds the host does not know are errors when required and warnings (or errors, if the manifest says so) when optional.</summary>
    public static IReadOnlyList<PackIssue> CheckCompatibility(OpenedPackage package, PackCompatibilityContext context)
    {
        var issues = new List<PackIssue>();
        if (package.Manifest is not { } m) return issues;
        var c = m.Compatibility;
        var min = SemVer.Parse(c.MinPlatformVersion);
        if (context.PlatformVersion < min)
            issues.Add(new PackIssue(PackCodes.PlatformTooOld, PackSeverity.Error, $"This package needs platform version {min} or later; this platform is {context.PlatformVersion}.", PackPaths.Manifest, "Upgrade the platform or use an older version of the pack."));
        if (c.MaxPlatformVersion is { } maxText && SemVer.TryParse(maxText, out var max) && context.PlatformVersion > max)
            issues.Add(new PackIssue(PackCodes.PlatformTooNew, PackSeverity.Error, $"This package supports platform versions up to {max}; this platform is {context.PlatformVersion}.", PackPaths.Manifest, "Use a newer version of the pack."));
        foreach (var entry in m.Content)
        {
            var known = entry.Kind == SolutionPackKind || context.KnownKinds.Contains(entry.Kind);
            if (!known)
            {
                if (entry.Required) issues.Add(new PackIssue(PackCodes.KindUnknownRequired, PackSeverity.Error, $"The package requires a kind of configuration ('{entry.Kind}') this platform does not support.", entry.Path, "Use a package built for this platform version."));
                else if (c.IgnoreUnknownOptionalKinds) issues.Add(new PackIssue(PackCodes.KindUnknownOptional, PackSeverity.Warning, $"The optional content of kind '{entry.Kind}' is not supported by this platform and will be ignored.", entry.Path));
                else issues.Add(new PackIssue(PackCodes.KindUnknownOptional, PackSeverity.Error, $"The package contains content of an unsupported kind ('{entry.Kind}') and does not permit it to be ignored.", entry.Path, "Use a package built for this platform version."));
                continue;
            }
            var supported = context.SupportedSchemas?.Invoke(entry.Kind) ?? new HashSet<int> { 1 };
            if (!supported.Contains(entry.SchemaVersion))
                issues.Add(new PackIssue(PackCodes.SchemaUnsupported, PackSeverity.Error, $"The schema version {entry.SchemaVersion} of '{entry.Kind}' is not supported (supported: {string.Join(", ", supported.OrderBy(x => x))}).", entry.Path, "Use a package built for this platform version."));
            else if (package.Content.TryGetValue(entry.Path, out var bytes))
            {
                try
                {
                    using var doc = CanonicalJson.Parse(bytes);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var declared) && declared != entry.SchemaVersion)
                        issues.Add(new PackIssue(PackCodes.SchemaUnsupported, PackSeverity.Error, $"The manifest says schema version {entry.SchemaVersion} but the document says {declared}.", entry.Path, "Rebuild the package so the manifest matches the documents."));
                }
                catch (FormatException) { /* reported by the document scan */ }
            }
        }
        return issues;
    }
}
