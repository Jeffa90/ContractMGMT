using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace JbsContracts.Api.Features;

/// <summary>
/// A business review as a Word document (D-104): something to edit and send to the supplier as the minutes. Plain and readable:
/// the JBS navy for headings, tables for the scores and the actions. Built with Open XML (already in the app through ClosedXML).
/// </summary>
public static class ReviewDocument
{
    public sealed record Action(string Text, string Who, DateOnly Due, bool Done);

    public sealed record Content(
        string Supplier, DateOnly HeldOn, string? Rating, (string Label, int? Score)[] Scores, string Summary, IReadOnlyList<string> JbsAttendees,
        IReadOnlyList<string> SupplierAttendees, IReadOnlyList<Action> Actions, DateOnly? NextReviewDue, string ExportedBy, DateTime ExportedLocal);

    private const string Navy = "232378";
    private const string Font = "Arial";

    private static string Day(DateOnly d) => d.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public static byte[] Build(Content c)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            main.Document = new Document(body);

            body.Append(Para($"Business review: {c.Supplier}", size: 32, bold: true, colour: Navy, after: 80));
            body.Append(Para($"Held {Day(c.HeldOn)}", size: 22, colour: "555555", after: 240));

            body.Append(Heading("How it went"));
            var scoreRows = new List<string[]> { new[] { "Overall", c.Rating ?? "Not rated" } };
            scoreRows.AddRange(c.Scores.Select(s => new[] { s.Label, s.Score is { } n ? $"{n} out of 5" : "Not scored" }));
            body.Append(Table(["", "Rating"], scoreRows, [3000, 6000], header: false));

            body.Append(Heading("Who attended"));
            body.Append(Para($"JBS: {(c.JbsAttendees.Count > 0 ? string.Join(", ", c.JbsAttendees) : "not recorded")}"));
            body.Append(Para($"{c.Supplier}: {(c.SupplierAttendees.Count > 0 ? string.Join(", ", c.SupplierAttendees) : "not recorded")}"));

            body.Append(Heading("Summary"));
            foreach (var line in c.Summary.Replace("\r\n", "\n").Split('\n')) body.Append(Para(line));

            body.Append(Heading("Actions"));
            if (c.Actions.Count == 0) body.Append(Para("No actions."));
            else body.Append(Table(["Action", "Who", "Due", "Status"],
                c.Actions.Select(a => new[] { a.Text, a.Who, Day(a.Due), a.Done ? "Done" : "Open" }).ToList(), [4300, 2300, 1500, 900], header: true));

            if (c.NextReviewDue is { } next)
            {
                body.Append(Heading("Next review"));
                body.Append(Para($"Due {Day(next)}."));
            }
            body.Append(Para($"From JBS Contracts, {c.ExportedLocal:d MMMM yyyy, h:mm tt}, by {c.ExportedBy}.", size: 16, colour: "777777", before: 360));

            body.Append(new SectionProperties(
                new PageSize { Width = 11906, Height = 16838 }, // A4
                new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 708, Footer = 708, Gutter = 0 }));
            main.Document.Save();
        }
        return stream.ToArray();
    }

    internal static Paragraph Heading(string text) => Para(text, size: 26, bold: true, colour: Navy, before: 280, after: 100, keepNext: true);

    internal static Paragraph Para(string text, int size = 22, bool bold = false, string? colour = null, int before = 0, int after = 120, bool keepNext = false)
    {
        var props = new RunProperties(new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font }, new FontSize { Val = size.ToString() });
        if (bold) props.Append(new Bold());
        if (colour is not null) props.Append(new Color { Val = colour });
        var pp = new ParagraphProperties(new SpacingBetweenLines { Before = before.ToString(), After = after.ToString() });
        if (keepNext) pp.Append(new KeepNext());
        return new Paragraph(pp, new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    internal static Table Table(string[] headers, List<string[]> rows, int[] widths, bool header)
    {
        var border = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" }, new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
            new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" }, new RightBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "CCCCCC" });
        var table = new Table(new TableProperties(border, new TableWidth { Width = widths.Sum().ToString(), Type = TableWidthUnitValues.Dxa },
            new TableLayout { Type = TableLayoutValues.Fixed }));
        table.Append(new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString() })));
        TableCell Cell(string text, int width, bool bold, string? shade)
        {
            var props = new TableCellProperties(new TableCellWidth { Width = width.ToString(), Type = TableWidthUnitValues.Dxa },
                new TableCellMargin(new TopMargin { Width = "60", Type = TableWidthUnitValues.Dxa }, new BottomMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                    new LeftMargin { Width = "100", Type = TableWidthUnitValues.Dxa }, new RightMargin { Width = "100", Type = TableWidthUnitValues.Dxa }));
            if (shade is not null) props.Append(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = shade });
            return new TableCell(props, Para(text, size: 20, bold: bold, after: 0));
        }
        if (header)
        {
            var head = new TableRow(new TableRowProperties(new TableHeader()));
            head.Append(headers.Select((h, i) => Cell(h, widths[i], true, "E8E8F2")));
            table.Append(head);
        }
        foreach (var r in rows) table.Append(new TableRow(r.Select((v, i) => Cell(v, widths[i], !header && i == 0, null))));
        return table;
    }
}
