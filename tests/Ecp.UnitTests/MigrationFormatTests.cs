using System.IO.Compression;
using System.Text;
using Ecp.DataMigration.Formats;
using Ecp.Tests.Common;
using Xunit;

namespace Ecp.UnitTests;

[Trait("Category", "Migration")]
public class CsvReaderTests
{
    private static SourceTable Read(byte[] bytes, char? delimiter = null, string? encoding = null) => CsvReader.Read(new MemoryStream(bytes), null, delimiter, encoding);
    private static byte[] Utf8(string s, bool bom = false) => bom ? [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(s)] : Encoding.UTF8.GetBytes(s);

    [Fact] // MIG-010
    public void A_UTF8_byte_order_mark_is_stripped_and_the_repository_samples_read()
    {
        var t = Read(Utf8("Id,Name\r\n1,Alpha\r\n", bom: true));
        Assert.Equal(["Id", "Name"], t.Headers);
        Assert.Equal("utf-8", t.Encoding);
        var sample = CsvReader.Read(new MemoryStream(MigrationSamples.ContractsCsv));
        Assert.Equal(2, sample.Count);
        Assert.Equal("SourceRecordId", sample.Headers[0]); // no BOM character left on the first header
        Assert.Equal(18, sample.Headers.Count);
        Assert.Equal(3, CsvReader.Read(new MemoryStream(MigrationSamples.ManifestCsv)).Count);
    }

    [Fact] // MIG-010
    public void UTF16_is_detected_with_or_without_a_byte_order_mark()
    {
        var text = "Id,Name\r\n1,Zoë\r\n";
        var le = new UnicodeEncoding(false, true).GetPreamble().Concat(new UnicodeEncoding(false, false).GetBytes(text)).ToArray();
        var be = new UnicodeEncoding(true, true).GetPreamble().Concat(new UnicodeEncoding(true, false).GetBytes(text)).ToArray();
        var bare = new UnicodeEncoding(false, false).GetBytes(text);
        foreach (var bytes in new[] { le, be, bare })
            Assert.Equal("Zoë", Read(bytes).Rows[0].Fields[1]);
        Assert.Equal("utf-16le", Read(le).Encoding);
        Assert.Equal("utf-16be", Read(be).Encoding);
    }

    [Fact] // MIG-010
    public void Windows_1252_is_used_only_when_asked_for_and_invalid_UTF8_is_refused_otherwise()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Id,Name\r\n1,Caf\u00e9\r\n");
        var refused = Assert.Throws<SourceFormatException>(() => Read(bytes));
        Assert.Equal("MIG-F003", refused.Code);
        Assert.Equal("Café", Read(bytes, encoding: "windows-1252").Rows[0].Fields[1]);
    }

    [Fact] // MIG-010
    public void Quoted_values_may_hold_delimiters_quotes_and_line_breaks()
    {
        var t = Read(Utf8("A,B,C\r\n\"x, y\",\"he said \"\"hi\"\"\",\"line1\r\nline2\"\r\n2,,3\r\n"));
        Assert.Equal(2, t.Count);
        Assert.Equal(["x, y", "he said \"hi\"", "line1\r\nline2"], t.Rows[0].Fields);
        Assert.Equal(["2", "", "3"], t.Rows[1].Fields);
        Assert.Equal(4, t.Rows[1].RowNumber); // physical line numbers count the embedded line break
    }

    [Fact] // MIG-010
    public void Ragged_rows_are_flagged_not_padded()
    {
        var t = Read(Utf8("A,B,C\r\n1,2\r\n1,2,3,4\r\n1,2,3\r\n"));
        Assert.True(t.Rows[0].Ragged); Assert.True(t.Rows[1].Ragged); Assert.False(t.Rows[2].Ragged);
        Assert.Equal(2, t.Rows[0].Fields.Count); // not padded
        Assert.Equal(4, t.Rows[1].Fields.Count);
    }

    [Fact]
    public void The_delimiter_is_detected_from_the_header_or_set_explicitly_and_blank_lines_are_skipped()
    {
        Assert.Equal("semicolon".Length > 0 ? ";" : "", Read(Utf8("A;B\r\n1;2\r\n\r\n3;4\r\n")).Delimiter);
        Assert.Equal(2, Read(Utf8("A;B\r\n1;2\r\n\r\n3;4\r\n")).Count);
        Assert.Equal("tab", Read(Utf8("A\tB\r\n1\t2\r\n")).Delimiter);
        Assert.Equal(["1;2"], Read(Utf8("A,B\r\n\"1;2\"\r\n"), delimiter: '|').Rows[0].Fields.Take(1));
    }

    [Fact]
    public void Duplicate_headers_empty_files_truncated_quotes_and_oversized_cells_are_refused()
    {
        Assert.Equal("MIG-E013", Assert.Throws<SourceFormatException>(() => Read(Utf8("A,A\r\n1,2\r\n"))).Code);
        Assert.Equal("MIG-F004", Assert.Throws<SourceFormatException>(() => Read(Utf8("\r\n\r\n"))).Code);
        Assert.Equal("MIG-F005", Assert.Throws<SourceFormatException>(() => Read(Utf8("A,B\r\n\"unterminated,2\r\n"))).Code);
        Assert.Equal("MIG-F006", Assert.Throws<SourceFormatException>(() => CsvReader.Read(new MemoryStream(Utf8("A\r\n" + new string('x', 100) + "\r\n")), new ReadLimits(MaxCellChars: 50))).Code);
        Assert.Equal("MIG-F006", Assert.Throws<SourceFormatException>(() => CsvReader.Read(new MemoryStream(Utf8("A\r\n1\r\n2\r\n3\r\n")), new ReadLimits(MaxRows: 2))).Code);
    }

    [Fact] // MIG-021
    public void The_CSV_writer_neutralises_formula_text_and_quotes_what_needs_it()
    {
        var bytes = CsvWriter.Write([new object?[] { "=cmd|' /C calc'!A0", "+1+1", "-2", "@SUM(A1)", "plain, text", "say \"hi\"" }]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("'=cmd", text);
        Assert.Contains(",'+1+1,", text); Assert.Contains(",'-2,", text); Assert.Contains(",'@SUM(A1),", text);
        Assert.Contains("\"plain, text\"", text); Assert.Contains("\"say \"\"hi\"\"\"", text);
        Assert.Equal(0xEF, bytes[0]); // a byte order mark, so a spreadsheet opens it as UTF-8
    }
}

[Trait("Category", "Migration")]
public class XlsxTests
{
    private static string Worksheet(string rows, string extra = "") => $"<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{rows}</sheetData>{extra}</worksheet>";

    private static byte[] Workbook(string sheetXml, string sheets = "<sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/>", string workbookPr = "", string? sharedStrings = null, string? styles = null, bool macro = false, string? extraPart = null)
    {
        var zip = new ZipBuilder()
            .Add("[Content_Types].xml", $"<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.{(macro ? "macroEnabled." : "")}main+xml\"/></Types>")
            .Add("xl/workbook.xml", $"<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">{workbookPr}<sheets>{sheets}</sheets></workbook>")
            .Add("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"x\" Target=\"worksheets/sheet2.xml\"/></Relationships>")
            .Add("xl/worksheets/sheet1.xml", sheetXml)
            .Add("xl/worksheets/sheet2.xml", Worksheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Hidden</t></is></c></row>"));
        if (sharedStrings is not null) zip.Add("xl/sharedStrings.xml", sharedStrings);
        if (styles is not null) zip.Add("xl/styles.xml", styles);
        if (extraPart is not null) zip.Add(extraPart, "x");
        return zip.Build();
    }

    private const string Styles = "<?xml version=\"1.0\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/></numFmts><cellXfs count=\"3\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/><xf numFmtId=\"164\"/></cellXfs></styleSheet>";

    [Fact] // MIG-011
    public void Shared_strings_cached_formula_values_booleans_and_dates_are_read()
    {
        var shared = "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Ref</t></si><si><t>Amount</t></si><si><t>When</t></si><si><r><t>AG</t></r><r><t>R-1</t></r></si></sst>";
        var rows = "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>"
            + "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>3</v></c><c r=\"B2\"><f>SUM(1,2)*2</f><v>6</v></c><c r=\"C2\" s=\"1\"><v>45306</v></c></row>"   // 15 Jan 2024
            + "<row r=\"3\"><c r=\"A3\" t=\"str\"><f>\"x\"&amp;\"y\"</f><v>xy</v></c><c r=\"B3\" t=\"b\"><v>1</v></c><c r=\"C3\" s=\"2\"><v>45306.5</v></c></row>";
        var t = XlsxReader.Read(new MemoryStream(Workbook(Worksheet(rows), sharedStrings: shared, styles: Styles)));
        Assert.Equal(["Ref", "Amount", "When"], t.Headers);
        Assert.Equal("AGR-1", t.Rows[0].Fields[0]); // rich text pieces joined
        Assert.Equal("6", t.Rows[0].Fields[1]);     // the cached value of the formula, never the formula
        Assert.Equal("2024-01-15", t.Rows[0].Fields[2]);
        Assert.Equal("xy", t.Rows[1].Fields[0]);
        Assert.Equal("TRUE", t.Rows[1].Fields[1]);
        Assert.Equal("2024-01-15T12:00:00", t.Rows[1].Fields[2]);
        Assert.Equal("Data", t.SheetName);
    }

    [Fact] // MIG-011
    public void Hidden_sheets_are_not_read_and_hidden_rows_are_flagged()
    {
        var rows = "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>H</t></is></c></row><row r=\"2\" hidden=\"1\"><c r=\"A2\" t=\"inlineStr\"><is><t>secret</t></is></c></row><row r=\"3\"><c r=\"A3\" t=\"inlineStr\"><is><t>shown</t></is></c></row>";
        var t = XlsxReader.Read(new MemoryStream(Workbook(Worksheet(rows), sheets: "<sheet name=\"Old\" sheetId=\"2\" state=\"hidden\" r:id=\"rId2\"/><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/>")));
        Assert.Equal("Data", t.SheetName); // the first visible sheet
        Assert.Contains(t.Notices, n => n.Contains("'Old' is hidden"));
        Assert.True(t.Rows[0].Hidden); Assert.False(t.Rows[1].Hidden);
        Assert.Contains(t.Notices, n => n.Contains("1 hidden row"));
    }

    [Fact] // MIG-011
    public void Macro_enabled_workbooks_are_refused()
    {
        var withBinary = Workbook(Worksheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>H</t></is></c></row>"), extraPart: "xl/vbaProject.bin");
        Assert.Equal("MIG-F002", Assert.Throws<SourceFormatException>(() => XlsxReader.Read(new MemoryStream(withBinary))).Code);
        var byType = Workbook(Worksheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>H</t></is></c></row>"), macro: true);
        Assert.Equal("MIG-F002", Assert.Throws<SourceFormatException>(() => XlsxReader.Read(new MemoryStream(byType))).Code);
        Assert.Equal("MIG-F005", Assert.Throws<SourceFormatException>(() => XlsxReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("not a workbook")))).Code);
    }

    [Fact]
    public void The_1904_date_system_and_the_1900_leap_year_quirk_are_honoured()
    {
        Assert.Equal("2024-01-15", XlsxReader.FromSerial(45306, false));
        Assert.Equal("2024-01-15", XlsxReader.FromSerial(43844, true));   // the same day in the 1904 system
        Assert.Equal("1900-01-01", XlsxReader.FromSerial(1, false));
        Assert.Equal("1900-03-01", XlsxReader.FromSerial(61, false));
    }

    [Fact]
    public void XML_with_a_DTD_or_external_entity_is_refused_and_never_resolved()
    {
        var evil = Worksheet("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&xxe;</t></is></c></row>").Replace("<?xml version=\"1.0\"?>", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>");
        Assert.ThrowsAny<Exception>(() => XlsxReader.Read(new MemoryStream(Workbook(evil))));
    }

    [Fact] // MIG-021
    public void The_workbook_writer_round_trips_and_neutralises_formula_text()
    {
        var bytes = XlsxWriter.Write([new("Report", [new object?[] { "Message", "Count" }, new object?[] { "=HYPERLINK(\"http://evil.example\",\"x\")", 3 }, new object?[] { "-1+1", 4.5m }, new object?[] { "a & <b>", true }])]);
        var t = XlsxReader.Read(new MemoryStream(bytes));
        Assert.Equal("'=HYPERLINK(\"http://evil.example\",\"x\")", t.Rows[0].Fields[0]); // text, not a formula
        Assert.Equal("3", t.Rows[0].Fields[1]);
        Assert.Equal("'-1+1", t.Rows[1].Fields[0]);
        Assert.Equal("a & <b>", t.Rows[2].Fields[0]);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        Assert.DoesNotContain("<f>", sheet.ReadToEnd()); // no value is ever written as a formula
    }
}

[Trait("Category", "Migration")]
public class ZipIntakeTests
{
    private static ArchiveListing Open(byte[] zip, ArchiveLimits? limits = null) => ZipIntake.Open(new MemoryStream(zip), limits ?? new ArchiveLimits());
    private static string? Reject(ArchiveListing l, string raw) => l.Entries.Single(e => e.RawName == raw).RejectCode;

    [Fact] // MIG-012
    public void Traversal_absolute_drive_UNC_reserved_names_and_symbolic_links_are_rejected_and_good_entries_pass()
    {
        var pdf = Placeholders.Pdf("x");
        var zip = new ZipBuilder().Add("../escape.pdf", pdf).Add("a/../../escape2.pdf", pdf).Add("/etc/absolute.pdf", pdf).Add("C:\\Windows\\drive.pdf", pdf).Add("\\\\server\\share\\unc.pdf", pdf)
            .Add("CON.pdf", pdf).Add("folder/NUL.txt", pdf).Add("trailingdot./x.pdf", pdf).Add("stream:ads.pdf", pdf).AddSymlink("link.pdf", "/etc/passwd").Add("good/ok.pdf", pdf).Add("good\\win.pdf", pdf);
        using var l = Open(zip.Build());
        foreach (var raw in new[] { "../escape.pdf", "a/../../escape2.pdf", "/etc/absolute.pdf", "C:\\Windows\\drive.pdf", "\\\\server\\share\\unc.pdf", "CON.pdf", "folder/NUL.txt", "trailingdot./x.pdf", "stream:ads.pdf", "link.pdf" })
            Assert.Equal("MIG-F001", Reject(l, raw));
        Assert.Null(Reject(l, "good/ok.pdf"));
        Assert.Equal("good/win.pdf", l.Entries.Single(e => e.RawName == "good\\win.pdf").Path); // separators are normalised
    }

    [Fact] // MIG-012
    public void Extension_empty_files_and_duplicate_names_are_rejected_per_entry()
    {
        var zip = new ZipBuilder().Add("run.exe", "MZ").Add("noext", "x").Add("empty.pdf", Array.Empty<byte>()).Add("A/Doc.pdf", Placeholders.Pdf("1")).Add("a/doc.PDF", Placeholders.Pdf("2")).Add("macro.docm", "x");
        using var l = Open(zip.Build());
        Assert.Equal("MIG-F002", Reject(l, "run.exe")); Assert.Equal("MIG-F002", Reject(l, "noext")); Assert.Equal("MIG-F002", Reject(l, "macro.docm"));
        Assert.Equal("MIG-F004", Reject(l, "empty.pdf"));
        Assert.Single(l.Entries, e => e.RejectCode == "MIG-F018"); // the second spelling of the same path
    }

    [Fact] // MIG-013
    public void Entry_count_total_size_and_compression_ratio_are_limited_for_the_whole_archive()
    {
        var many = new ZipBuilder(); for (var i = 0; i < 6; i++) many.Add($"f{i}.txt", "x");
        Assert.Equal("MIG-F006", Assert.Throws<SourceFormatException>(() => Open(many.Build(), new ArchiveLimits { MaxEntries = 5 })).Code);

        var big = new ZipBuilder().Add("a.txt", new byte[3000]).Add("b.txt", new byte[3000]);
        Assert.Equal("MIG-F006", Assert.Throws<SourceFormatException>(() => Open(big.Build(), new ArchiveLimits { MaxTotalBytes = 5000 })).Code);

        // A highly compressible file above the floor is treated as a decompression bomb and the archive is refused without being read.
        var bomb = new ZipBuilder().Add("zeros.txt", new byte[40 * 1024 * 1024]).Build();
        Assert.True(bomb.Length < 400_000);
        Assert.Equal("MIG-F006", Assert.Throws<SourceFormatException>(() => Open(bomb)).Code);
        using var small = Open(new ZipBuilder().Add("zeros.txt", new byte[200_000]).Build()); // under the floor: not a bomb
        Assert.Null(small.Entries[0].RejectCode);
    }

    [Fact] // MIG-013
    public void Reading_is_bounded_by_what_is_really_produced_not_by_what_the_header_claims()
    {
        var zip = new ZipBuilder().Add("a.txt", new byte[10_000]).Build();
        using var l = Open(zip, new ArchiveLimits { MaxEntryBytes = 20_000, MaxTotalBytes = 50_000 });
        using var s = l.Entries[0].Open();
        var buffer = new byte[4096]; long total = 0; int n;
        while ((n = s.Read(buffer, 0, buffer.Length)) > 0) total += n;
        Assert.Equal(10_000, total);
        // A stream that produces more than its declared size, or more than the archive's remaining budget, is cut off however the archive describes itself.
        static void Drain(Stream s) { var b = new byte[2048]; while (s.Read(b, 0, b.Length) > 0) { } }
        var overEntry = new BoundedStream(new MemoryStream(new byte[10_000]), 5_000, new ByteBudget(100_000));
        Assert.Equal("MIG-F006", Assert.Throws<SourceFormatException>(() => Drain(overEntry)).Code);
        var overBudget = new BoundedStream(new MemoryStream(new byte[10_000]), 20_000, new ByteBudget(4_000));
        Assert.Equal("MIG-F006", Assert.Throws<SourceFormatException>(() => Drain(overBudget)).Code);
        Assert.Equal("MIG-F005", Assert.Throws<SourceFormatException>(() => Open(Encoding.ASCII.GetBytes("this is not a zip"))).Code);
    }

    [Fact]
    public void Content_signatures_must_match_the_declared_type()
    {
        Assert.True(FileSignatures.Matches("pdf", Placeholders.Pdf("x")));
        Assert.True(FileSignatures.Matches("docx", Placeholders.Docx("x")));
        Assert.True(FileSignatures.Matches("png", Placeholders.Png()));
        Assert.True(FileSignatures.Matches("txt", "plain text"u8.ToArray()));
        Assert.False(FileSignatures.Matches("pdf", "MZ\u0090\u0000"u8.ToArray()));   // an executable named .pdf
        Assert.False(FileSignatures.Matches("docx", Placeholders.Pdf("x")));
        Assert.False(FileSignatures.Matches("txt", new byte[] { 1, 2, 0, 3 }));      // binary named .txt
        Assert.False(FileSignatures.Matches("exe", "MZ"u8.ToArray()));
        Assert.Equal("MIG-F001", ArchivePaths.Normalise("a/../b").Reason is null ? "" : "MIG-F001");
        Assert.Equal("dir/sub/file.pdf", ArchivePaths.Normalise("./dir//sub\\file.pdf").Path);
        Assert.Equal("sub", ArchivePaths.ParentFolder("dir/sub/file.pdf"));
    }
}
