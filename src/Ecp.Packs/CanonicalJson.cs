using System.Text;
using System.Text.Json;

namespace Ecp.Packs;

/// <summary>
/// The canonical form of a JSON document, used for the manifest before it is signed or hashed: UTF-8 without a byte-order mark, object members in ordinal order, no insignificant whitespace, integers only,
/// strings escaped the minimal way (quote, backslash and control characters). Two documents with the same meaning have the same canonical bytes. Duplicate member names, comments, trailing commas, fractions,
/// exponents and excessive nesting are refused.
/// </summary>
public static class CanonicalJson
{
    public static byte[] Canonicalise(ReadOnlySpan<byte> json, int maxDepth = 32)
    {
        using var doc = Parse(json, maxDepth);
        return Canonicalise(doc.RootElement);
    }

    public static JsonDocument Parse(ReadOnlySpan<byte> json, int maxDepth = 32)
    {
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) json = json[3..];
        try
        {
            return JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = maxDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex) { throw new FormatException("The JSON is not valid: " + ex.Message.Split('\n')[0]); }
    }

    public static byte[] Canonicalise(JsonElement element)
    {
        var sb = new StringBuilder();
        Write(element, sb);
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static void Write(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var members = e.EnumerateObject().ToList();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in members) if (!names.Add(m.Name)) throw new FormatException($"The member name '{m.Name}' appears more than once.");
                sb.Append('{');
                var first = true;
                foreach (var m in members.OrderBy(m => m.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(m.Name, sb); sb.Append(':'); Write(m.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!firstItem) sb.Append(',');
                    firstItem = false;
                    Write(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String: WriteString(e.GetString()!, sb); break;
            case JsonValueKind.Number:
                if (!e.TryGetInt64(out var n) || e.GetRawText() != n.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new FormatException("Only whole numbers in plain decimal form are allowed in a manifest.");
                sb.Append(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
            default: throw new FormatException("Unsupported JSON value.");
        }
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
