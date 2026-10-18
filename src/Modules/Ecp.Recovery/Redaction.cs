using System.Text.RegularExpressions;

namespace Ecp.Recovery;

/// <summary>
/// Removes secrets and personal data from text that leaves the platform in an evidence report or support bundle, and detects them before a manifest is written. Redaction is a safety net: the reports are built from
/// fields that never hold secrets, and every finished report is scanned again (<see cref="ContainsSecret"/>) and refused if the scan finds one.
/// </summary>
public static partial class Redaction
{
    public const string Mask = "[redacted]";

    [GeneratedRegex(@"(?i)\b(password|pwd|secret|client[_ ]?secret|api[_-]?key|access[_-]?key|account[_-]?key|sharedaccesssignature|sas[_-]?token|token|authorization)\s*[=:]\s*(""[^""]*""|'[^']*'|[^;,\s""']+)", RegexOptions.NonBacktracking)]
    private static partial Regex KeyValueSecret();
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.NonBacktracking)] private static partial Regex BearerToken();
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.NonBacktracking)] private static partial Regex PrivateKeyHeader();
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.None, matchTimeoutMilliseconds: 1000)] private static partial Regex PrivateKeyBlock();
    [GeneratedRegex(@"(?i)\b(server|data source|host)\s*=\s*[^;]+;[^""\r\n]*", RegexOptions.NonBacktracking)] private static partial Regex ConnectionString();
    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.NonBacktracking)] private static partial Regex EmailAddress();
    [GeneratedRegex(@"[\r\n\t]+", RegexOptions.NonBacktracking)] private static partial Regex LineBreaks();
    [GeneratedRegex(@"(?i)://[^/\s:@]+:[^/\s@]+@", RegexOptions.NonBacktracking)] private static partial Regex UrlCredentials();

    /// <summary>Text that is safe to put in a report: connection strings, key/value secrets, bearer tokens, private keys, credentials in URLs and email addresses are masked, and line breaks are flattened (log injection).</summary>
    public static string Clean(string? text, bool keepEmail = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = PrivateKeyBlock().Replace(text, Mask);
        s = ConnectionString().Replace(s, Mask);
        s = KeyValueSecret().Replace(s, m => m.Groups[1].Value + "=" + Mask);
        s = BearerToken().Replace(s, "Bearer " + Mask);
        s = UrlCredentials().Replace(s, "://" + Mask + "@");
        if (!keepEmail) s = EmailAddress().Replace(s, Mask);
        return LineBreaks().Replace(s, " ");
    }

    /// <summary>True if the text still looks like it holds a secret (used as a final gate on a finished document).</summary>
    public static bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (PrivateKeyHeader().IsMatch(text) || BearerToken().IsMatch(text) || UrlCredentials().IsMatch(text)) return true;
        foreach (Match m in KeyValueSecret().Matches(text))
            if (!m.Groups[2].Value.Contains(Mask, StringComparison.Ordinal) && m.Groups[2].Value.Trim('"', '\'').Length > 0 && !IsHarmlessValue(m.Groups[2].Value)) return true;
        foreach (Match m in ConnectionString().Matches(text))
            if (!m.Value.Contains(Mask, StringComparison.Ordinal)) return true;
        return false;
    }

    // "token": "n/a" style words that describe a field without holding a value.
    // A secret reference ("secret:env/NAME", "secret:file/path") names where a secret lives; it is not the secret.
    private static bool IsHarmlessValue(string v) => v.Trim('"', '\'', ' ') is "" or "n/a" or "none" or "false" or "true" or "null" or "not set" || v.StartsWith("secret:", StringComparison.OrdinalIgnoreCase)
        || v.StartsWith("env/", StringComparison.Ordinal) || v.StartsWith("file/", StringComparison.Ordinal);
}
