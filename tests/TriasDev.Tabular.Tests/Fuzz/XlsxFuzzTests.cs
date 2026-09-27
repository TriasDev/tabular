using System.Globalization;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Fuzz;

/// <summary>
/// Random workbooks written as xlsx in the many ways the format allows one value to be spelt — shared
/// or inline strings, rich-text runs, entities, references, CDATA, omitted cell and row references,
/// date styles — read back exactly.
/// </summary>
public sealed class XlsxFuzzTests
{
    private static readonly DateTime Epoch1900 = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);

    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private const string Styles =
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="4"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="22" applyNumberFormat="1"/><xf numFmtId="2" applyNumberFormat="1"/></cellXfs></styleSheet>""";

    [Fact]
    public void ReadsBackEveryWorkbookItWrote()
    {
        foreach ((int seed, Random random) in FuzzCases.Generate(300))
        {
            List<FuzzSheet> workbook = FuzzSheets.Workbook(random);
            byte[] file = Write(workbook, random);
            FuzzCases.Keep(seed, ".xlsx", file);

            using XlsxCursor cursor = new(new MemoryStream(file), cancellationToken: TestContext.Current.CancellationToken);
            FuzzSheets.AssertReadsAs(workbook, cursor, seed, blankRowsAreRead: true);
        }
    }

    /// <summary>The workbook as an xlsx package, each value spelt one of the ways the format allows.</summary>
    internal static byte[] Write(List<FuzzSheet> workbook, Random random)
    {
        List<string> shared = [];
        XlsxPackage package = new XlsxPackage().WithStyles(Styles);

        // The 1904 system counts from its own epoch, and holds no date before it.
        DateTime epoch = Epoch1900;

        if (random.Chance(0.2) && workbook.SelectMany(s => s.Rows).SelectMany(r => r.Cells).All(c => c.Kind != FuzzKind.Date || c.Date >= Epoch1904))
        {
            epoch = Epoch1904;
            package = package.WithDate1904();
        }

        foreach (FuzzSheet sheet in workbook)
        {
            string rows = Rows(sheet, shared, epoch, random);

            package = random.Chance(0.3)
                ? package.WithRawSheet(sheet.Name,
                    """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">"""
                    + """<dimension ref="A1:H20"/><sheetViews><sheetView workbookViewId="0"/></sheetViews><sheetFormatPr defaultRowHeight="15"/><cols><col min="1" max="3" width="12" customWidth="1"/></cols>"""
                    + $"<sheetData>{rows}</sheetData>"
                    + """<mergeCells count="1"><mergeCell ref="J1:K1"/></mergeCells><pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" header="0.3" footer="0.3"/></worksheet>""")
                : package.WithSheet(sheet.Name, rows);
        }

        if (shared.Count > 0)
        {
            package = package.WithSharedStrings(string.Concat(shared));
        }

        return package.Build();
    }

    private static string Rows(FuzzSheet sheet, List<string> shared, DateTime epoch, Random random)
    {
        StringBuilder xml = new();
        int previous = 0;

        foreach (FuzzRow row in sheet.Rows)
        {
            bool numbered = row.Number != previous + 1 || random.Chance(0.7);
            xml.Append(numbered ? $"""<row r="{row.Number}">""" : "<row>");
            int column = 0;

            for (int c = 0; c < row.Cells.Length; c++)
            {
                FuzzCell cell = row.Cells[c];

                if (cell.Kind == FuzzKind.Empty && random.Chance(0.7))
                {
                    continue;
                }

                // A cell right after the one before may leave out its reference; one after a gap may not.
                string reference = c == column && random.Chance(0.3)
                    ? string.Empty
                    : $""" r="{Column(c)}{row.Number}" """;
                xml.Append(Cell(cell, reference, shared, epoch, random));
                column = c + 1;
            }

            xml.Append("</row>");
            previous = row.Number;
        }

        return xml.ToString();
    }

    private static string Cell(FuzzCell cell, string reference, List<string> shared, DateTime epoch, Random random) => cell.Kind switch
    {
        FuzzKind.Empty => random.Pick($"<c{reference}/>", $"""<c{reference} s="3"/>""", $"<c{reference}><v></v></c>"),
        FuzzKind.Text => Text(cell.Text, reference, shared, random),
        FuzzKind.Number => $"""<c{reference}{random.Pick("", " s=\"0\"", " s=\"3\"", " t=\"n\"")}><v>{Number(cell.Number, random)}</v></c>""",
        FuzzKind.Boolean => $"""<c{reference} t="b"><v>{(cell.Boolean ? 1 : 0)}</v></c>""",
        FuzzKind.Date => $"""<c{reference} s="{random.Pick(1, 2)}"><v>{FuzzSheets.Invariant((cell.Date - epoch).TotalDays)}</v></c>""",
        _ => $"""<c{reference} t="e">{(random.Chance(0.5) ? "<f>1/0</f>" : "")}<v>{cell.Text}</v></c>""",
    };

    /// <summary>A number as a writer may spell it, in seventeen digits either way so it names the same double.</summary>
    private static string Number(double value, Random random) =>
        random.Chance(0.2) && Math.Abs(value) < 1e300
            ? value.ToString("E16", CultureInfo.InvariantCulture)
            : FuzzSheets.Invariant(value);

    private static string Text(string text, string reference, List<string> shared, Random random)
    {
        switch (random.Next(4))
        {
            case 0:
                shared.Add($"<si>{Runs(text, random)}</si>");
                return $"""<c{reference} t="s"><v>{shared.Count - 1}</v></c>""";

            case 1:
                return $"""<c{reference} t="inlineStr"><is>{Runs(text, random)}</is></c>""";

            case 2 when !text.Contains('\r', StringComparison.Ordinal):
                return $"""<c{reference} t="str"><f>"x"&amp;"y"</f><v>{FuzzSheets.XmlText(text, random)}</v></c>""";

            default:
                shared.Add($"""<si><t xml:space="preserve">{FuzzSheets.XmlText(text, random)}</t></si>""");
                return $"""<c{reference} t="s"><v>{shared.Count - 1}</v></c>""";
        }
    }

    /// <summary>Text split into formatted runs, with now and then a phonetic reading that is not the value.</summary>
    private static string Runs(string text, Random random)
    {
        if (random.Chance(0.4))
        {
            return $"""<t xml:space="preserve">{FuzzSheets.XmlText(text, random)}</t>""";
        }

        StringBuilder xml = new();
        int i = 0;

        while (i < text.Length)
        {
            int length = Math.Min(text.Length - i, random.Next(1, 4));

            if (char.IsHighSurrogate(text[i + length - 1]) && i + length < text.Length)
            {
                length++;
            }

            xml.Append(random.Chance(0.5) ? """<r><rPr><b/><sz val="11"/></rPr>""" : "<r>")
               .Append($"""<t xml:space="preserve">{FuzzSheets.XmlText(text.Substring(i, length), random)}</t></r>""");
            i += length;
        }

        if (random.Chance(0.2))
        {
            xml.Append("""<rPh sb="0" eb="1"><t>ヨミ</t></rPh><phoneticPr fontId="1"/>""");
        }

        return xml.ToString();
    }

    private static string Column(int index)
    {
        string name = string.Empty;

        for (int n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }
}
