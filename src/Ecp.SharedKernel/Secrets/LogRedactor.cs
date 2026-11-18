using System.Text.RegularExpressions;

namespace Ecp.SharedKernel.Secrets;

/// <summary>
/// Removes secret values from a finished log line (message, properties and exception text) before it is written anywhere. It works on text that may be JSON, a
/// connection string or a header dump, and never changes anything else, so a line stays parseable. A value is replaced; the key stays, so a reader can see that
/// something was there. This is a safety net: the platform's own code does not log secrets in the first place.
/// </summary>
public static partial class LogRedactor
{
    public const string Mask = "***";

    // key=value (connection strings, query strings, header dumps): the value runs to the next ; , quote, whitespace or closing brace.
    [GeneratedRegex(@"(?<key>(?:password|pwd|passphrase|secret|client[_ -]?secret|token|access[_ -]?token|refresh[_ -]?token|id[_ -]?token|api[_ -]?key|account[_ -]?key|shared[_ -]?access[_ -]?signature|sig|authorization)\s*=\s*)(?<value>[^;,""'\s}&]+)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex KeyValue();

    // JSON "key":"value" and "key": value
    [GeneratedRegex(@"(?<key>""(?:[A-Za-z0-9_.-]*(?:password|pwd|passphrase|secret|token|apikey|api_key|accountkey|authorization|connectionstring|cookie)[A-Za-z0-9_.-]*)""\s*:\s*)(?<value>""(?:[^""\\]|\\.)*""|[^,}\s""]+)", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex JsonKeyValue();

    // Authorization: Bearer xxx / Basic xxx
    [GeneratedRegex(@"(?<scheme>\b(?:Bearer|Basic)\s+)(?<value>[A-Za-z0-9._~+/=-]{8,})", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking)]
    private static partial Regex Scheme();

    // Three dot-separated base64url segments starting eyJ: a JSON Web Token.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.NonBacktracking)]
    private static partial Regex Jwt();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = Jwt().Replace(text, Mask);
        result = Scheme().Replace(result, m => m.Groups["scheme"].Value + Mask);
        result = JsonKeyValue().Replace(result, m => m.Groups["key"].Value + (m.Groups["value"].Value.StartsWith('"') ? "\"" + Mask + "\"" : Mask));
        result = KeyValue().Replace(result, m => m.Groups["key"].Value + Mask);
        return result;
    }
}
