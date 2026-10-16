using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Ecp.DataMigration.Formats;

/// <summary>
/// Reads the first (or a named) sheet of an .xlsx workbook with no third-party library (RISK-10): only the parts it needs are opened, formulas are read as
/// their cached values, hidden sheets and rows are flagged, macro-enabled workbooks are refused, external links are never followed, and XML is parsed with
/// DTDs and external entities disabled.
/// </summary>
public static class XlsxReader
{
    private const long MaxPartBytes = 200L * 1024 * 1024;
    private const long MaxTotalBytes = 400L * 1024 * 1024;
    private const int MaxRatio = 100;

    public static SourceTable Read(Stream input, ReadLimits? limits = null, string? sheetName = null)
    {
        limits ??= new ReadLimits();
        var notices = new List<string>();
        ZipArchive zip;
        try { zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new SourceFormatException("MIG-F005", "The workbook is corrupt or is not an .xlsx file."); }
        using (zip)
        {
            var entries = zip.Entries.ToDictionary(e => e.FullName.Replace('\\', '/'), e => e, StringComparer.OrdinalIgnoreCase);
            if (entries.Keys.Any(k => k.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)) || ContentTypesAreMacroEnabled(entries))
                throw new SourceFormatException("MIG-F002", "Macro-enabled workbooks (.xlsm) are not accepted. Save the workbook as .xlsx.");
            long total = 0;
            foreach (var e in zip.Entries)
            {
                total += e.Length;
                if (e.Length > MaxPartBytes || total > MaxTotalBytes || (e.CompressedLength > 0 && e.Length / Math.Max(1, e.CompressedLength) > MaxRatio && e.Length > 1024 * 1024))
                    throw new SourceFormatException("MIG-F006", "The workbook expands to an unsafe size and was refused.");
            }
            if (entries.Keys.Any(k => k.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase)))
                notices.Add("The workbook has external links. They were not followed; cached values were read.");

            if (!entries.TryGetValue("xl/workbook.xml", out var workbookEntry)) throw new SourceFormatException("MIG-F005", "The file is not a workbook (xl/workbook.xml is missing).");
            var (sheets, date1904) = ReadWorkbook(workbookEntry);
            var rels = entries.TryGetValue("xl/_rels/workbook.xml.rels", out var relEntry) ? ReadRelationships(relEntry) : new Dictionary<string, string>();

            var hidden = sheets.Where(s => s.State is "hidden" or "veryHidden").Select(s => s.Name).ToList();
            foreach (var h in hidden) notices.Add($"The sheet '{h}' is hidden and was not read.");
            var visible = sheets.Where(s => s.State is not ("hidden" or "veryHidden")).ToList();
            var chosen = sheetName is null ? visible.FirstOrDefault() : sheets.FirstOrDefault(s => s.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
            if (chosen is null) throw new SourceFormatException("MIG-F005", sheetName is null ? "The workbook has no visible sheet." : $"The sheet '{sheetName}' was not found.");
            if (sheetName is not null && chosen.State is "hidden" or "veryHidden") notices.Add($"The sheet '{chosen.Name}' is hidden. It was read because it was chosen explicitly.");
            if (!rels.TryGetValue(chosen.RelationshipId, out var target)) throw new SourceFormatException("MIG-F005", "The sheet's part could not be found.");
            var partName = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (!entries.TryGetValue(partName, out var sheetEntry)) throw new SourceFormatException("MIG-F005", "The sheet's part could not be found.");

            var shared = entries.TryGetValue("xl/sharedStrings.xml", out var sharedEntry) ? ReadSharedStrings(sharedEntry, limits) : [];
            var dateStyles = entries.TryGetValue("xl/styles.xml", out var stylesEntry) ? ReadDateStyles(stylesEntry) : new HashSet<int>();
            var (headers, rows) = ReadSheet(sheetEntry, shared, dateStyles, date1904, limits, notices);
            return new SourceTable(headers, rows, "xlsx", null, null, chosen.Name, notices);
        }
    }

    private static bool ContentTypesAreMacroEnabled(Dictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.TryGetValue("[Content_Types].xml", out var e)) return false;
        using var s = e.Open(); using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Contains("macroEnabled", StringComparison.OrdinalIgnoreCase);
    }

    private static XmlReader Open(ZipArchiveEntry entry, long maxChars = MaxPartBytes)
        => XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maxChars, IgnoreWhitespace = false, CloseInput = true });

    private sealed record SheetRef(string Name, string RelationshipId, string State);

    private static (List<SheetRef> Sheets, bool Date1904) ReadWorkbook(ZipArchiveEntry entry)
    {
        var sheets = new List<SheetRef>(); var date1904 = false;
        using var x = Open(entry);
        while (x.Read())
        {
            if (x.NodeType != XmlNodeType.Element) continue;
            if (x.LocalName == "workbookPr") date1904 = x.GetAttribute("date1904") is "1" or "true";
            else if (x.LocalName == "sheet")
                sheets.Add(new SheetRef(x.GetAttribute("name") ?? "", x.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? "", x.GetAttribute("state") ?? "visible"));
        }
        return (sheets, date1904);
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchiveEntry entry)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var x = Open(entry);
        while (x.Read())
            if (x.NodeType == XmlNodeType.Element && x.LocalName == "Relationship" && x.GetAttribute("Id") is { } id && x.GetAttribute("Target") is { } target
                && !"External".Equals(x.GetAttribute("TargetMode"), StringComparison.OrdinalIgnoreCase)) map[id] = target;
        return map;
    }

    private static List<string> ReadSharedStrings(ZipArchiveEntry entry, ReadLimits limits)
    {
        var list = new List<string>();
        using var x = Open(entry);
        var sb = new StringBuilder(); var inSi = false; var phonetic = 0;
        x.Read();
        while (!x.EOF)
        {
            if (x.NodeType == XmlNodeType.Element)
            {
                if (x.LocalName == "si") { inSi = true; sb.Clear(); if (x.IsEmptyElement) { inSi = false; list.Add(""); } }
                else if (x.LocalName == "rPh") phonetic++;
                else if (x.LocalName == "t" && inSi && !x.IsEmptyElement)
                {
                    var text = x.ReadElementContentAsString(); // leaves the reader after </t>
                    if (phonetic == 0) sb.Append(text);
                    continue;
                }
            }
            else if (x.NodeType == XmlNodeType.EndElement)
            {
                if (x.LocalName == "rPh") phonetic--;
                else if (x.LocalName == "si")
                {
                    inSi = false; list.Add(sb.ToString());
                    if (list.Count > (long)limits.MaxRows * limits.MaxColumns) throw new SourceFormatException("MIG-F006", "The workbook has too many distinct strings.");
                }
            }
            x.Read();
        }
        return list;
    }

    private static readonly HashSet<int> BuiltInDateFormats = [14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58];

    private static HashSet<int> ReadDateStyles(ZipArchiveEntry entry)
    {
        var formats = new Dictionary<int, string>(); var xfs = new List<int>(); var inXfs = false;
        using var x = Open(entry);
        while (x.Read())
        {
            if (x.NodeType == XmlNodeType.Element)
            {
                if (x.LocalName == "numFmt" && int.TryParse(x.GetAttribute("numFmtId"), out var id)) formats[id] = x.GetAttribute("formatCode") ?? "";
                else if (x.LocalName == "cellXfs") inXfs = true;
                else if (x.LocalName == "xf" && inXfs) xfs.Add(int.TryParse(x.GetAttribute("numFmtId"), out var f) ? f : 0);
            }
            else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "cellXfs") inXfs = false;
        }
        var result = new HashSet<int>();
        for (var i = 0; i < xfs.Count; i++)
        {
            var f = xfs[i];
            if (BuiltInDateFormats.Contains(f) || (formats.TryGetValue(f, out var code) && LooksLikeDate(code))) result.Add(i);
        }
        return result;
    }

    private static bool LooksLikeDate(string code)
    {
        var sb = new StringBuilder(); var quoted = false; var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (quoted) continue;
            if (c == '[') bracket = true; else if (c == ']') bracket = false;
            else if (c == '\\') i++;
            else if (!bracket) sb.Append(char.ToLowerInvariant(c));
        }
        var s = sb.ToString();
        return s.Any(ch => ch is 'y' or 'd' or 'h' or 's') || (s.Contains('m') && !s.Contains("0.00"));
    }

    private static (IReadOnlyList<string> Headers, IReadOnlyList<SourceRow> Rows) ReadSheet(ZipArchiveEntry entry, List<string> shared, HashSet<int> dateStyles, bool date1904,
        ReadLimits limits, List<string> notices)
    {
        var rawRows = new List<(int Row, bool Hidden, SortedDictionary<int, string> Cells)>();
        using var x = Open(entry);
        SortedDictionary<int, string>? current = null; var rowNumber = 0; var rowHidden = false; var hiddenRows = 0; var formulaWithoutValue = 0; var errors = 0;
        x.Read();
        while (!x.EOF)
        {
            if (x.NodeType == XmlNodeType.Element && x.LocalName == "row")
            {
                rowNumber = int.TryParse(x.GetAttribute("r"), out var r) ? r : rowNumber + 1;
                rowHidden = x.GetAttribute("hidden") is "1" or "true";
                if (rowHidden) hiddenRows++;
                current = [];
                if (x.IsEmptyElement) { rawRows.Add((rowNumber, rowHidden, current)); current = null; }
            }
            else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "row" && current is not null)
            {
                if (rawRows.Count > limits.MaxRows + 1) throw new SourceFormatException("MIG-F006", $"The sheet has more than {limits.MaxRows} rows.");
                rawRows.Add((rowNumber, rowHidden, current)); current = null;
            }
            else if (x.NodeType == XmlNodeType.Element && x.LocalName == "c" && current is not null)
            {
                var reference = x.GetAttribute("r") ?? "";
                var col = ColumnIndex(reference);
                if (col < 0) col = current.Count == 0 ? 0 : current.Keys.Max() + 1;
                if (col >= limits.MaxColumns) throw new SourceFormatException("MIG-F006", $"The sheet has more than {limits.MaxColumns} columns.");
                var type = x.GetAttribute("t");
                var style = int.TryParse(x.GetAttribute("s"), out var s) ? s : 0;
                string? value = null; string? inline = null; var hasFormula = false;
                if (!x.IsEmptyElement)
                {
                    var depth = x.Depth;
                    x.Read();
                    while (!x.EOF && x.Depth > depth)
                    {
                        if (x.NodeType == XmlNodeType.Element && x.LocalName == "v" && !x.IsEmptyElement) { value = x.ReadElementContentAsString(); continue; }
                        if (x.NodeType == XmlNodeType.Element && x.LocalName == "f") { hasFormula = true; if (!x.IsEmptyElement) { x.ReadElementContentAsString(); continue; } }
                        if (x.NodeType == XmlNodeType.Element && x.LocalName == "is") { inline = ReadInline(x); continue; }
                        x.Read();
                    }
                }
                else x.Read();
                string cell;
                switch (type)
                {
                    case "s": cell = value is not null && int.TryParse(value, out var si) && si >= 0 && si < shared.Count ? shared[si] : ""; break;
                    case "inlineStr": cell = inline ?? ""; break;
                    case "str": cell = value ?? ""; break;
                    case "b": cell = value == "1" ? "TRUE" : "FALSE"; break;
                    case "e": cell = value ?? "#ERROR"; errors++; break;
                    default:
                        if (value is null) { cell = ""; if (hasFormula) formulaWithoutValue++; }
                        else if (dateStyles.Contains(style) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)) cell = FromSerial(serial, date1904);
                        else cell = value;
                        break;
                }
                if (cell.Length > limits.MaxCellChars) throw new SourceFormatException("MIG-F006", $"A cell is longer than {limits.MaxCellChars} characters.");
                if (cell.Length > 0) current[col] = cell;
                continue;
            }
            x.Read();
        }
        if (hiddenRows > 0) notices.Add($"{hiddenRows} hidden row(s) were read and flagged.");
        if (formulaWithoutValue > 0) notices.Add($"{formulaWithoutValue} formula cell(s) had no cached value and were read as empty.");
        if (errors > 0) notices.Add($"{errors} cell(s) contain a spreadsheet error value.");

        var headerIndex = rawRows.FindIndex(r => r.Cells.Count > 0);
        if (headerIndex < 0) throw new SourceFormatException("MIG-F004", "The sheet has no header row.");
        var headerCells = rawRows[headerIndex].Cells;
        var width = headerCells.Keys.Max() + 1;
        var raw = new List<string>();
        for (var i = 0; i < width; i++) raw.Add(headerCells.TryGetValue(i, out var h) ? h : "");
        var headers = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < raw.Count; i++)
        {
            var h = raw[i].Trim(); if (h.Length == 0) h = $"Column{i + 1}";
            if (!seen.Add(h)) throw new SourceFormatException("MIG-E013", $"The header '{h}' appears more than once, so columns cannot be told apart.");
            headers.Add(h);
        }
        var rows = new List<SourceRow>();
        foreach (var (row, hidden, cells) in rawRows.Skip(headerIndex + 1))
        {
            if (cells.Count == 0) continue; // a blank row is not a record
            var fields = new List<string>(width);
            for (var i = 0; i < width; i++) fields.Add(cells.TryGetValue(i, out var v) ? v : "");
            var ragged = cells.Keys.Max() >= width;
            rows.Add(new SourceRow(row, fields, ragged, hidden));
        }
        return (headers, rows);
    }

    /// <summary>Reads an inline-string element and leaves the reader after its end tag.</summary>
    private static string ReadInline(XmlReader x)
    {
        var sb = new StringBuilder(); var depth = x.Depth; var phonetic = 0;
        if (x.IsEmptyElement) { x.Read(); return ""; }
        x.Read();
        while (!x.EOF && x.Depth > depth)
        {
            if (x.NodeType == XmlNodeType.Element)
            {
                if (x.LocalName == "rPh") phonetic++;
                else if (x.LocalName == "t" && !x.IsEmptyElement) { var text = x.ReadElementContentAsString(); if (phonetic == 0) sb.Append(text); continue; }
            }
            else if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "rPh") phonetic--;
            x.Read();
        }
        x.Read(); // past </is>
        return sb.ToString();
    }

    private static int ColumnIndex(string reference)
    {
        var n = 0; var any = false;
        foreach (var ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') { n = n * 26 + (ch - 'A' + 1); any = true; }
            else if (ch is >= 'a' and <= 'z') { n = n * 26 + (ch - 'a' + 1); any = true; }
            else break;
        }
        return any ? n - 1 : -1;
    }

    /// <summary>Excel serial number to ISO text, honouring the workbook's date system (and the 1900 leap-year quirk).</summary>
    public static string FromSerial(double serial, bool date1904)
    {
        DateTime baseDate;
        if (date1904) baseDate = new DateTime(1904, 1, 1);
        else baseDate = serial < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30);
        var dt = baseDate.AddDays(Math.Floor(serial)).AddSeconds(Math.Round((serial - Math.Floor(serial)) * 86400));
        return dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }
}

public sealed record WorksheetData(string Name, IReadOnlyList<IReadOnlyList<object?>> Rows, bool HeaderRow = true);

/// <summary>
/// Writes a plain .xlsx workbook (inline strings, one bold header row). Text that a spreadsheet could read as a formula is neutralised (MIG-021), and no value is ever
/// written as a formula.
/// </summary>
public static class XlsxWriter
{
    private const int MaxCell = 32_000;

    public static byte[] Write(IReadOnlyList<WorksheetData> sheets)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", ContentTypes(sheets.Count));
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < sheets.Count; i++)
            {
                wb.Append($"<sheet name=\"{Escape(SheetName(sheets[i].Name, i))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                rels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i]));
            }
            wb.Append("</sheets></workbook>");
            rels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml", wb.ToString());
            Add(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
            Add(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs></styleSheet>");
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(new UTF8Encoding(false).GetBytes(content));
    }

    private static string ContentTypes(int sheets)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var i = 1; i <= sheets; i++) sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        return sb.Append("</Types>").ToString();
    }

    private static string SheetName(string name, int index)
    {
        var cleaned = new string(name.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = $"Sheet{index + 1}";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string SheetXml(WorksheetData sheet)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        var widths = new List<int>();
        foreach (var row in sheet.Rows.Take(200))
            for (var c = 0; c < row.Count; c++)
            {
                while (widths.Count <= c) widths.Add(8);
                widths[c] = Math.Clamp(Math.Max(widths[c], (row[c]?.ToString()?.Length ?? 0) + 2), 8, 60);
            }
        if (widths.Count > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < widths.Count; c++) sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{widths[c]}\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            var row = sheet.Rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                var reference = ColumnName(c) + (r + 1);
                var style = sheet.HeaderRow && r == 0 ? " s=\"1\"" : "";
                switch (row[c])
                {
                    case null: break;
                    case bool b: sb.Append($"<c r=\"{reference}\"{style} t=\"b\"><v>{(b ? 1 : 0)}</v></c>"); break;
                    case int or long or short or byte: sb.Append($"<c r=\"{reference}\"{style}><v>{Convert.ToString(row[c], CultureInfo.InvariantCulture)}</v></c>"); break;
                    case decimal d: sb.Append($"<c r=\"{reference}\"{style}><v>{d.ToString(CultureInfo.InvariantCulture)}</v></c>"); break;
                    case double d when !double.IsNaN(d) && !double.IsInfinity(d): sb.Append($"<c r=\"{reference}\"{style}><v>{d.ToString("R", CultureInfo.InvariantCulture)}</v></c>"); break;
                    default:
                        var text = SafeCell.Neutralise(row[c] is DateTime dt ? dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : row[c]!.ToString() ?? "");
                        if (text.Length > MaxCell) text = text[..MaxCell] + " [shortened for the spreadsheet cell limit]";
                        sb.Append($"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>");
                        break;
                }
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (!XmlConvert.IsXmlChar(ch) && !char.IsSurrogate(ch)) continue; // control characters are not valid in XML
            sb.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => ch.ToString() });
        }
        return sb.ToString();
    }
}
