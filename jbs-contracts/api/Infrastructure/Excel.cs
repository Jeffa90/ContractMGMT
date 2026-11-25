using System.Globalization;
using ClosedXML.Excel;

namespace JbsContracts.Api.Infrastructure;

/// <summary>
/// Reading and writing Excel workbooks for imports and exports (ClosedXML, MIT licence, runs on the server; no Office needed).
/// An export is also the template for an import: export, change or add rows, import the same file.
/// </summary>
public static class Excel
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 5000;
    private static readonly CultureInfo AU = CultureInfo.GetCultureInfo("en-AU");

    public sealed record Column(string Header, string? Note = null, double Width = 22);

    public sealed record Sheet(string Name, IReadOnlyList<Column> Columns, IEnumerable<object?[]> Rows);

    /// <summary>One sheet with a bold header row, frozen, with notes on the header cells that explain what to type.</summary>
    public static byte[] Write(string sheetName, IReadOnlyList<Column> columns, IEnumerable<object?[]> rows) => WriteBook([new Sheet(sheetName, columns, rows)]);

    /// <summary>A workbook of several sheets, each laid out as <see cref="Write"/> does.</summary>
    public static byte[] WriteBook(IEnumerable<Sheet> sheets)
    {
        using var book = new XLWorkbook();
        foreach (var s in sheets) AddSheet(book, s.Name, s.Columns, s.Rows);
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static void AddSheet(XLWorkbook book, string sheetName, IReadOnlyList<Column> columns, IEnumerable<object?[]> rows)
    {
        var sheet = book.Worksheets.Add(sheetName);
        for (var i = 0; i < columns.Count; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = columns[i].Header;
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E1E6F5");
            if (columns[i].Note is { } note) cell.CreateComment().AddText(note);
            sheet.Column(i + 1).Width = columns[i].Width;
        }
        var r = 2;
        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Count && i < row.Length; i++)
            {
                var cell = sheet.Cell(r, i + 1);
                switch (row[i])
                {
                    case null: break;
                    case DateOnly d: cell.Value = d.ToDateTime(TimeOnly.MinValue); cell.Style.DateFormat.Format = "d/mm/yyyy"; break;
                    case DateTime t: cell.Value = t; cell.Style.DateFormat.Format = "d/mm/yyyy h:mm AM/PM"; break;
                    case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = "#,##0.00"; break;
                    case int n: cell.Value = n; break;
                    case long n: cell.Value = n; break;
                    case double n: cell.Value = n; break;
                    case bool b: cell.Value = b ? "Yes" : "No"; break;
                    default: cell.Value = row[i]!.ToString(); break;
                }
            }
            r++;
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, Math.Max(1, r - 1), columns.Count).SetAutoFilter();
    }

    /// <summary>A copy of one cell's value, taken while the workbook is open (rows outlive the workbook).</summary>
    public sealed class CellValue(IXLCell cell)
    {
        private readonly XLDataType type = cell.DataType;
        private readonly double number = cell.DataType == XLDataType.Number ? cell.GetDouble() : 0;
        private readonly DateTime date = cell.DataType == XLDataType.DateTime ? cell.GetDateTime() : default;
        private readonly string text = cell.GetFormattedString();
        private readonly bool empty = cell.IsEmpty();

        public XLDataType DataType => type;
        public bool IsEmpty() => empty || (type is not XLDataType.Number and not XLDataType.DateTime && text.Trim().Length == 0);
        public double GetDouble() => number;
        public DateTime GetDateTime() => date;
        public string GetFormattedString() => text;
    }

    /// <summary>A row read from an import sheet: values by header (case and spacing ignored), and its row number in Excel.</summary>
    public sealed class Row(int number, Dictionary<string, CellValue> cells)
    {
        public int Number { get; } = number;

        private CellValue? Cell(string header) => cells.TryGetValue(Normalise(header), out var c) ? c : null;

        public bool Has(string header) => cells.ContainsKey(Normalise(header));

        public string? Text(string header)
        {
            var cell = Cell(header);
            if (cell is null || cell.IsEmpty()) return null;
            var value = cell.DataType == XLDataType.Number ? cell.GetDouble().ToString(CultureInfo.InvariantCulture) : cell.GetFormattedString();
            value = value.Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>Yes/No, Y/N, True/False, 1/0, x. Blank gives the default.</summary>
        public bool? YesNo(string header, out string? problem)
        {
            problem = null;
            var text = Text(header);
            if (text is null) return null;
            switch (text.ToLowerInvariant())
            {
                case "yes" or "y" or "true" or "1" or "x" or "✓": return true;
                case "no" or "n" or "false" or "0": return false;
                default: problem = $"{header}: use Yes or No (it says \"{text}\")."; return null;
            }
        }

        /// <summary>An Excel date, or text like 31/12/2027 or 2027-12-31.</summary>
        public DateOnly? Date(string header, out string? problem)
        {
            problem = null;
            var cell = Cell(header);
            if (cell is null || cell.IsEmpty()) return null;
            if (cell.DataType == XLDataType.DateTime) return DateOnly.FromDateTime(cell.GetDateTime());
            if (cell.DataType == XLDataType.Number)
            {
                try { return DateOnly.FromDateTime(DateTime.FromOADate(cell.GetDouble())); }
                catch (ArgumentException) { problem = $"{header}: that isn't a date."; return null; }
            }
            var text = cell.GetFormattedString().Trim();
            if (text.Length == 0) return null;
            string[] formats = ["d/M/yyyy", "d/M/yy", "d-M-yyyy", "yyyy-MM-dd", "d MMM yyyy", "d MMMM yyyy"];
            if (DateOnly.TryParseExact(text, formats, AU, DateTimeStyles.AllowWhiteSpaces, out var d)) return d;
            problem = $"{header}: \"{text}\" isn't a date. Use day/month/year, for example 31/12/2027.";
            return null;
        }

        public decimal? Money(string header, out string? problem)
        {
            problem = null;
            var cell = Cell(header);
            if (cell is null || cell.IsEmpty()) return null;
            if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();
            var text = cell.GetFormattedString().Replace("$", "").Replace(",", "").Replace(" ", "").Trim();
            if (text.Length == 0) return null;
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var m)) return m;
            problem = $"{header}: \"{cell.GetFormattedString()}\" isn't a number.";
            return null;
        }

        public int? WholeNumber(string header, out string? problem)
        {
            problem = null;
            var money = Money(header, out problem);
            if (money is null) return null;
            if (money != decimal.Truncate(money.Value)) { problem = $"{header}: use a whole number."; return null; }
            return (int)money.Value;
        }
    }

    public static string Normalise(string header) => new string(header.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Reads the first sheet. Returns the rows (blank rows skipped), or a plain problem when the file can't be used.
    /// The required headers must be present; other columns are ignored (and listed so people know).
    /// </summary>
    public static (List<Row> Rows, List<string> Ignored, string? Problem) Read(Stream stream, IReadOnlyCollection<string> knownHeaders, IReadOnlyCollection<string> requiredHeaders)
    {
        XLWorkbook book;
        try { book = new XLWorkbook(stream); }
        catch (Exception) { return ([], [], "That file couldn't be opened as an Excel workbook. Save it as .xlsx and try again."); }
        using (book)
        {
            var sheet = book.Worksheets.FirstOrDefault();
            if (sheet is null) return ([], [], "The workbook has no sheets.");
            var used = sheet.RangeUsed();
            if (used is null) return ([], [], "The sheet is empty. Put the column headings in row 1 and the data underneath.");
            var lastColumn = used.LastColumn().ColumnNumber();
            var headers = new Dictionary<string, int>();
            var ignored = new List<string>();
            var known = knownHeaders.ToDictionary(Normalise, h => h);
            for (var col = 1; col <= lastColumn; col++)
            {
                var text = sheet.Cell(1, col).GetFormattedString().Trim();
                if (text.Length == 0) continue;
                if (known.ContainsKey(Normalise(text))) headers.TryAdd(Normalise(text), col);
                else ignored.Add(text);
            }
            var missing = requiredHeaders.Where(h => !headers.ContainsKey(Normalise(h))).ToList();
            if (missing.Count > 0)
                return ([], ignored, $"The first row needs the column heading{(missing.Count > 1 ? "s" : "")} {string.Join(", ", missing.Select(m => $"\"{m}\""))}. Export the list first to get a sheet with the right headings.");
            var rows = new List<Row>();
            var lastRow = used.LastRow().RowNumber();
            if (lastRow - 1 > MaxRows) return ([], ignored, $"That sheet has more than {MaxRows:N0} rows. Split it into smaller files.");
            for (var r = 2; r <= lastRow; r++)
            {
                var cells = headers.ToDictionary(h => h.Key, h => new CellValue(sheet.Cell(r, h.Value)));
                if (cells.Values.All(c => c.IsEmpty())) continue;
                rows.Add(new Row(r, cells));
            }
            if (rows.Count == 0) return ([], ignored, "There are no rows under the headings.");
            return (rows, ignored, null);
        }
    }

    /// <summary>
    /// The first sheet's column headings, each with up to three example values from the rows under it (for matching columns on an import).
    /// </summary>
    public static (List<(string Header, List<string> Examples)> Columns, int Rows, string? Problem) Headers(Stream stream)
    {
        XLWorkbook book;
        try { book = new XLWorkbook(stream); }
        catch (Exception) { return ([], 0, "That file couldn't be opened. Use an Excel workbook (.xlsx) or a CSV file."); }
        using (book)
        {
            var sheet = book.Worksheets.FirstOrDefault();
            var used = sheet?.RangeUsed();
            if (sheet is null || used is null) return ([], 0, "The sheet is empty. Put the column headings in row 1 and the data underneath.");
            var lastRow = used.LastRow().RowNumber();
            var columns = new List<(string, List<string>)>();
            var seen = new HashSet<string>();
            for (var col = 1; col <= used.LastColumn().ColumnNumber(); col++)
            {
                var header = sheet.Cell(1, col).GetFormattedString().Trim();
                if (header.Length == 0 || !seen.Add(Normalise(header))) continue;
                var examples = new List<string>();
                for (var r = 2; r <= lastRow && examples.Count < 3; r++)
                {
                    var text = sheet.Cell(r, col).GetFormattedString().Trim();
                    if (text.Length > 0) examples.Add(text.Length > 60 ? text[..60] + "…" : text);
                }
                columns.Add((header, examples));
            }
            if (columns.Count == 0) return ([], 0, "Row 1 has no column headings.");
            return (columns, Math.Max(0, lastRow - 1), null);
        }
    }

    /// <summary>
    /// A CSV file (comma separated, "quoted" values may hold commas, quotes and line breaks) as an Excel workbook, so it reads the same way.
    /// Every value is kept as text; dates and numbers are understood when the import reads them. UTF-8, falling back to Windows-1252.
    /// </summary>
    public static MemoryStream FromCsv(Stream csv)
    {
        using var buffer = new MemoryStream();
        csv.CopyTo(buffer);
        var bytes = buffer.ToArray();
        string text;
        try { text = new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException) { text = System.Text.Encoding.Latin1.GetString(bytes); }
        text = text.TrimStart('\uFEFF');
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("Data");
        int row = 1, col = 1;
        var field = new System.Text.StringBuilder();
        var quoted = false;
        void Put()
        {
            var value = field.ToString();
            if (value.Length > 0) sheet.Cell(row, col).SetValue(value);
            field.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) quoted = true;
            else if (ch == ',') { Put(); col++; }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                Put();
                row++;
                col = 1;
                if (row - 1 > MaxRows + 1) break;
            }
            else field.Append(ch);
        }
        Put();
        var output = new MemoryStream();
        book.SaveAs(output);
        output.Position = 0;
        return output;
    }
}
