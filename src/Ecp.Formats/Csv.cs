using System.Text;

namespace Ecp.Formats;

/// <summary>Raised for a source file that cannot be read safely. The code is a stable catalogue code (MIGRATION_SUPPLEMENT section 8).</summary>
public sealed class SourceFormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Limits applied while reading tabular input (section 9). Defaults are open pending Q-13 and configurable.</summary>
public sealed record ReadLimits(int MaxRows = 250_000, int MaxColumns = 500, int MaxCellChars = 32_768, long MaxBytes = 500L * 1024 * 1024);

/// <summary>One data row. <see cref="RowNumber"/> is the physical row or line in the source, so an error report can point at it.</summary>
public sealed record SourceRow(int RowNumber, IReadOnlyList<string> Fields, bool Ragged, bool Hidden = false);

/// <summary>A source table as read from CSV or XLSX: headers, rows, and anything the reader noticed that a person should know.</summary>
public sealed record SourceTable(IReadOnlyList<string> Headers, IReadOnlyList<SourceRow> Rows, string Format, string? Encoding, string? Delimiter, string? SheetName,
    IReadOnlyList<string> Notices)
{
    public int Count => Rows.Count;

    /// <summary>A row as a header-keyed map. Missing trailing fields read as empty; extra fields are not addressable (the row is flagged ragged).</summary>
    public Dictionary<string, string> ToMap(SourceRow row)
    {
        var map = new Dictionary<string, string>(Headers.Count, StringComparer.Ordinal);
        for (var i = 0; i < Headers.Count; i++) map[Headers[i]] = i < row.Fields.Count ? row.Fields[i] : "";
        return map;
    }
}

public static class CsvReader
{
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>
    /// Reads RFC 4180 CSV. A byte order mark is stripped; UTF-8 and UTF-16 are detected; Windows-1252 is used only when <paramref name="encodingName"/> asks for it explicitly.
    /// Quoted fields may contain delimiters, doubled quotes and line breaks. Rows whose field count differs from the header are flagged, never padded.
    /// </summary>
    public static SourceTable Read(Stream input, ReadLimits? limits = null, char? delimiter = null, string? encodingName = null)
    {
        limits ??= new ReadLimits();
        var (encoding, label, preamble) = ChooseEncoding(input, encodingName);
        var delim = delimiter ?? DetectDelimiter(input, encoding, preamble);

        input.Position = preamble;
        using var reader = new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 81920, leaveOpen: true);
        var records = new List<(int Line, List<string> Fields)>();
        var text = new StringBuilder();
        var fields = new List<string>();
        var line = 1; var recordLine = 1; long total = 0;
        var buffer = new char[81920]; var count = 0; var pos = 0;

        bool Fill()
        {
            count = reader.Read(buffer, 0, buffer.Length); pos = 0; total += count;
            if (total > limits.MaxBytes) throw new SourceFormatException("MIG-F006", "The file is larger than the permitted size.");
            return count > 0;
        }
        int Peek() => pos < count || Fill() ? buffer[pos] : -1;

        void EndField()
        {
            fields.Add(text.ToString()); text.Clear();
            if (fields.Count > limits.MaxColumns) throw new SourceFormatException("MIG-F006", $"The file has more than {limits.MaxColumns} columns.");
        }
        void EndRecord()
        {
            EndField();
            if (!(fields.Count == 1 && fields[0].Length == 0)) // a completely blank line is not a record
            {
                if (records.Count > limits.MaxRows) throw new SourceFormatException("MIG-F006", $"The file has more than {limits.MaxRows} rows.");
                records.Add((recordLine, fields));
            }
            fields = [];
        }
        void Append(char ch)
        {
            text.Append(ch);
            if (text.Length > limits.MaxCellChars) throw new SourceFormatException("MIG-F006", $"A cell is longer than {limits.MaxCellChars} characters.");
        }

        var inQuotes = false; var fieldStart = true; var any = false;
        while (Peek() is var c and >= 0)
        {
            pos++; any = true;
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (Peek() == '"') { pos++; Append('"'); }
                    else inQuotes = false;
                }
                else { if (ch == '\n') line++; Append(ch); }
                continue;
            }
            if (ch == '"' && fieldStart) { inQuotes = true; fieldStart = false; continue; }
            if (ch == delim) { EndField(); fieldStart = true; continue; }
            if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && Peek() == '\n') pos++;
                line++; EndRecord(); recordLine = line; fieldStart = true; continue;
            }
            fieldStart = false;
            Append(ch);
        }
        if (inQuotes) throw new SourceFormatException("MIG-F005", "The file ends inside a quoted value, so it is truncated or corrupt.");
        if (any && (text.Length > 0 || fields.Count > 0)) EndRecord();
        if (records.Count == 0) throw new SourceFormatException("MIG-F004", "The file has no header row.");

        var headers = NormaliseHeaders(records[0].Fields);
        var rows = new List<SourceRow>(records.Count - 1);
        for (var r = 1; r < records.Count; r++)
        {
            var (ln, f) = records[r];
            rows.Add(new SourceRow(ln, f, f.Count != headers.Count));
        }
        return new SourceTable(headers, rows, "csv", label, delim == '\t' ? "tab" : delim.ToString(), null, []);
    }

    /// <summary>Chooses the delimiter that occurs most often, outside quotes, in the header line.</summary>
    private static char DetectDelimiter(Stream input, Encoding encoding, int preamble)
    {
        input.Position = preamble;
        using var reader = new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        var counts = new int[Candidates.Length]; var quoted = false;
        int c; var scanned = 0;
        while ((c = reader.Read()) >= 0 && scanned++ < 65536)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && (c == '\n' || c == '\r')) break;
            if (!quoted) { var i = Array.IndexOf(Candidates, (char)c); if (i >= 0) counts[i]++; }
        }
        var best = 0;
        for (var i = 1; i < counts.Length; i++) if (counts[i] > counts[best]) best = i;
        return counts[best] == 0 ? ',' : Candidates[best];
    }

    private static IReadOnlyList<string> NormaliseHeaders(IReadOnlyList<string> raw)
    {
        var headers = new List<string>(raw.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            var h = raw[i].Trim().TrimStart('﻿');
            if (h.Length == 0) h = $"Column{i + 1}";
            if (!seen.Add(h)) throw new SourceFormatException("MIG-E013", $"The header '{h}' appears more than once, so columns cannot be told apart.");
            headers.Add(h);
        }
        return headers;
    }

    internal static (Encoding Encoding, string Label, int Preamble) ChooseEncoding(Stream input, string? requested)
    {
        if (!input.CanSeek) throw new SourceFormatException("MIG-F005", "The file cannot be read twice.");
        input.Position = 0;
        Span<byte> head = stackalloc byte[4];
        var n = input.Read(head);
        input.Position = 0;
        if (requested is not null && requested.Equals("windows-1252", StringComparison.OrdinalIgnoreCase))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return (Encoding.GetEncoding(1252), "windows-1252", 0);
        }
        if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) return (new UTF8Encoding(false, true), "utf-8", 3);
        if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE) return (new UnicodeEncoding(false, false, true), "utf-16le", 2);
        if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF) return (new UnicodeEncoding(true, false, true), "utf-16be", 2);
        if (n >= 4 && head[0] != 0 && head[1] == 0 && head[2] != 0 && head[3] == 0) return (new UnicodeEncoding(false, false, true), "utf-16le", 0);
        if (n >= 4 && head[0] == 0 && head[1] != 0 && head[2] == 0 && head[3] != 0) return (new UnicodeEncoding(true, false, true), "utf-16be", 0);
        // No byte order mark: UTF-8 is the only encoding assumed. Bytes that are not valid UTF-8 are refused rather than guessed at.
        var validated = ValidateUtf8(input);
        input.Position = 0;
        if (!validated) throw new SourceFormatException("MIG-F003", "The file is not valid UTF-8. If it was saved as Windows-1252, choose that encoding explicitly.");
        return (new UTF8Encoding(false, true), "utf-8", 0);
    }

    private static bool ValidateUtf8(Stream input)
    {
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        var bytes = new byte[81920]; var chars = new char[bytes.Length + 4];
        int read;
        try
        {
            while ((read = input.Read(bytes, 0, bytes.Length)) > 0) decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            decoder.GetChars([], 0, 0, chars, 0, flush: true);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }
}

/// <summary>Writes CSV for the error and reconciliation exports. Formula-looking cells are neutralised so a spreadsheet application never executes them (MIG-021).</summary>
public static class CsvWriter
{
    public static byte[] Write(IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var cell = SafeCell.Text(row[i]);
                if (cell.IndexOfAny([',', '"', '\r', '\n']) >= 0) sb.Append('"').Append(cell.Replace("\"", "\"\"")).Append('"');
                else sb.Append(cell);
            }
            sb.Append("\r\n");
        }
        var body = new UTF8Encoding(false).GetBytes(sb.ToString());
        return [0xEF, 0xBB, 0xBF, .. body];
    }
}

/// <summary>Formula-injection defence for exports: a text value that a spreadsheet could read as a formula or command is prefixed so it is shown as text.</summary>
public static class SafeCell
{
    public static string Text(object? value)
    {
        var s = value switch
        {
            null => "",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
        return value is string or null || s.Length == 0 ? Neutralise(s) : s;
    }

    public static string Neutralise(string s)
    {
        if (s.Length == 0) return s;
        var first = s[0];
        if (first is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n') return "'" + s;
        return s;
    }
}
