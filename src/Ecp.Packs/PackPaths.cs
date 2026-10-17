namespace Ecp.Packs;

/// <summary>
/// The only places a package may put a file, and the only characters a path may use. A package is a closed structure: <c>manifest.json</c>, <c>signature.json</c> and JSON documents under
/// <c>content/</c>. Anything else is refused, whatever the archive says about it.
/// </summary>
public static class PackPaths
{
    public const string Manifest = "manifest.json";
    public const string Signature = "signature.json";
    public const string ContentFolder = "content/";

    /// <summary>The path as stored, or a reason it is refused. Paths are ASCII letters, digits, '.', '_', '-' and '/', relative, with no empty, '.' or '..' segments.</summary>
    public static (string? Path, string? Reason) Validate(string? raw, PackLimits limits)
    {
        if (string.IsNullOrEmpty(raw)) return (null, "The path is empty.");
        if (raw.Length > limits.MaxPathLength) return (null, "The path is too long.");
        if (raw.StartsWith('/') || raw.StartsWith('\\')) return (null, "An absolute path is not allowed.");
        if (raw.Contains('\\')) return (null, "Backslashes are not allowed; use forward slashes.");
        foreach (var c in raw)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '/';
            if (!ok) return (null, "The path contains a character that is not allowed (letters, digits, '.', '_', '-' and '/' only).");
        }
        var segments = raw.Split('/');
        if (segments.Length > limits.MaxPathDepth) return (null, "The path is nested too deeply.");
        foreach (var s in segments)
        {
            if (s.Length == 0) return (null, "The path has an empty segment.");
            if (s is "." or "..") return (null, "A path that climbs out of its folder is not allowed.");
            if (s[0] == '.') return (null, "A hidden file name is not allowed.");
            if (s.EndsWith('.')) return (null, "A path segment cannot end with a dot.");
        }
        return (raw, null);
    }

    /// <summary>Whether a (valid) path is somewhere a package is allowed to put a file.</summary>
    public static bool IsAllowedLocation(string path)
        => path is Manifest or Signature || (path.StartsWith(ContentFolder, StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal) && path.Count(c => c == '/') == 1);
}
