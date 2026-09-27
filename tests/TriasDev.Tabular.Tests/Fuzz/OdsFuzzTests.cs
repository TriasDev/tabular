using System.Globalization;
using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Fuzz;

/// <summary>
/// Random workbooks written as OpenDocument in the many ways the format allows — repeated cells and
/// rows, paragraphs, spaces, tabs and line breaks as elements, spans, annotations, stated string
/// values, covered cells, header rows — read back exactly.
/// </summary>
public sealed class OdsFuzzTests
{
    [Fact]
    public void ReadsBackEveryWorkbookItWrote()
    {
        foreach ((int seed, Random random) in FuzzCases.Generate(300))
        {
            List<FuzzSheet> workbook = FuzzSheets.Workbook(random);
            byte[] file = Write(workbook, random);
            FuzzCases.Keep(seed, ".ods", file);

            using OdsCursor cursor = new(new MemoryStream(file), cancellationToken: TestContext.Current.CancellationToken);
            FuzzSheets.AssertReadsAs(workbook, cursor, seed, blankRowsAreRead: false);
        }
    }

    /// <summary>The workbook as an OpenDocument spreadsheet, each value written one of the ways the format allows.</summary>
    internal static byte[] Write(List<FuzzSheet> workbook, Random random)
    {
        OdsPackage package = new();

        foreach (FuzzSheet sheet in workbook)
        {
            package = package.WithRawTable(Table(sheet, random));
        }

        return package.Build();
    }

    private static string Table(FuzzSheet sheet, Random random)
    {
        StringBuilder xml = new($"""<table:table table:name="{FuzzSheets.XmlAttribute(sheet.Name)}" table:style-name="ta1"><table:table-column table:style-name="co1" table:number-columns-repeated="8" table:default-cell-style-name="Default"/>""");
        int previous = 0;
        bool inHeader = false;
        string? pending = null;
        int repeat = 0;

        void Flush()
        {
            if (pending is not null)
            {
                xml.Append(repeat == 1 ? pending : pending.Replace("<table:table-row>", $"""<table:table-row table:number-rows-repeated="{repeat}">""", StringComparison.Ordinal));
            }

            pending = null;
        }

        for (int r = 0; r < sheet.Rows.Count; r++)
        {
            FuzzRow row = sheet.Rows[r];

            // Folded into the element before: the same row, once more.
            if (pending is not null && r > 0 && row.Number == previous + 1 && row.Cells == sheet.Rows[r - 1].Cells && random.Chance(0.7))
            {
                repeat++;
                previous = row.Number;
                continue;
            }

            Flush();

            if (r == 0 && row.Number == 1 && random.Chance(0.2))
            {
                xml.Append("<table:table-header-rows>");
                inHeader = true;
            }
            else if (inHeader)
            {
                xml.Append("</table:table-header-rows>");
                inHeader = false;
            }

            xml.Append(Gap(row.Number - previous - 1, random));
            pending = Row(row, random);
            repeat = 1;
            previous = row.Number;
        }

        Flush();

        if (inHeader)
        {
            xml.Append("</table:table-header-rows>");
        }

        if (random.Chance(0.4))
        {
            xml.Append("""<table:table-row table:number-rows-repeated="1048000"><table:table-cell table:number-columns-repeated="1024"/></table:table-row>""");
        }

        return xml.Append("</table:table>").ToString();
    }

    /// <summary>Rows that hold nothing, written one by one or as one repeated element.</summary>
    private static string Gap(int rows, Random random)
    {
        if (rows <= 0)
        {
            return string.Empty;
        }

        return random.Chance(0.5)
            ? $"""<table:table-row table:number-rows-repeated="{rows}"><table:table-cell table:number-columns-repeated="{random.Next(1, 1025)}"/></table:table-row>"""
            : string.Concat(Enumerable.Repeat("<table:table-row><table:table-cell/></table:table-row>", rows));
    }

    private static string Row(FuzzRow row, Random random)
    {
        StringBuilder xml = new("<table:table-row>");
        string[] cells = [.. row.Cells.Select(c => Cell(c, random))];

        int c = 0;

        while (c < cells.Length)
        {
            int same = 1;

            while (c + same < cells.Length && cells[c + same] == cells[c] && cells[c].StartsWith("<table:table-cell", StringComparison.Ordinal) && random.Chance(0.8))
            {
                same++;
            }

            xml.Append(same == 1 ? cells[c] : cells[c].Replace("<table:table-cell", $"""<table:table-cell table:number-columns-repeated="{same}" """, StringComparison.Ordinal));
            c += same;
        }

        if (random.Chance(0.3))
        {
            xml.Append("""<table:table-cell table:number-columns-repeated="1016"/>""");
        }

        return xml.Append("</table:table-row>").ToString();
    }

    private static string Cell(FuzzCell cell, Random random) => cell.Kind switch
    {
        FuzzKind.Empty => random.Pick("<table:table-cell/>", """<table:table-cell table:style-name="ce1"/>""", "<table:covered-table-cell/>"),
        FuzzKind.Text => Text(cell.Text, random),
        FuzzKind.Number => Number(cell.Number, random),
        FuzzKind.Boolean => $"""<table:table-cell office:value-type="boolean" office:boolean-value="{random.Pick(cell.Boolean ? ["true", "1"] : new[] { "false", "0" })}"><text:p>{(cell.Boolean ? "TRUE" : "FALSE")}</text:p></table:table-cell>""",
        FuzzKind.Date => $"""<table:table-cell office:value-type="date" office:date-value="{DateValue(cell.Date, random)}"><text:p>15.01.24</text:p></table:table-cell>""",
        _ => $"""<table:table-cell table:formula="of:=1/0" office:value-type="string" office:string-value="" calcext:value-type="error"><text:p>{cell.Text}</text:p></table:table-cell>""",
    };

    private static string Number(double value, Random random)
    {
        string type = random.Pick("float", "percentage", "currency");
        string currency = type == "currency" ? """ office:currency="EUR" """ : " ";

        return $"""<table:table-cell office:value-type="{type}"{currency}office:value="{FuzzSheets.Invariant(value)}" calcext:value-type="{type}"><text:p>1,5 €</text:p></table:table-cell>""";
    }

    private static string DateValue(DateTime date, Random random) =>
        date.TimeOfDay == TimeSpan.Zero && random.Chance(0.7)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Text(string text, Random random)
    {
        string annotation = random.Chance(0.1)
            ? "<office:annotation><dc:creator>someone</dc:creator><text:p>a note, not the value</text:p></office:annotation>"
            : string.Empty;

        return random.Next(3) switch
        {
            0 => $"""<table:table-cell office:value-type="string" office:string-value="{FuzzSheets.XmlAttribute(text)}" calcext:value-type="string">{annotation}<text:p>shown</text:p></table:table-cell>""",
            1 => $"<table:table-cell>{annotation}{Paragraphs(text, random)}</table:table-cell>",
            _ => $"""<table:table-cell office:value-type="string" calcext:value-type="string">{annotation}{Paragraphs(text, random)}</table:table-cell>""",
        };
    }

    /// <summary>Text as paragraphs: a line feed ends a paragraph or is a line break inside one.</summary>
    private static string Paragraphs(string text, Random random)
    {
        string[] lines = text.Split('\n');
        StringBuilder xml = new("<text:p>");

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                xml.Append(random.Chance(0.5) ? "<text:line-break/>" : "</text:p><text:p>");
            }

            string line = Line(lines[i], random);
            xml.Append(random.Chance(0.2) && line.Length > 0 ? $"""<text:span text:style-name="T1">{line}</text:span>""" : line);
        }

        return xml.Append("</text:p>").ToString().Replace("<text:p></text:p>", "<text:p/>", StringComparison.Ordinal);
    }

    /// <summary>One line, its tabs and runs of spaces as the elements a writer uses for them.</summary>
    private static string Line(string line, Random random)
    {
        StringBuilder xml = new();
        int i = 0;

        while (i < line.Length)
        {
            if (line[i] == '\t')
            {
                xml.Append("<text:tab/>");
                i++;
                continue;
            }

            int end = i;

            while (end < line.Length && line[end] == ' ')
            {
                end++;
            }

            if (end > i)
            {
                xml.Append(Spaces(end - i, random));
                i = end;
                continue;
            }

            while (end < line.Length && line[end] is not ' ' and not '\t')
            {
                end++;
            }

            xml.Append(FuzzSheets.XmlText(line[i..end], random));
            i = end;
        }

        return xml.ToString();
    }

    private static string Spaces(int count, Random random) => (count, random.Next(3)) switch
    {
        (1, 0) => "<text:s/>",
        (1, _) => " ",
        (_, 0) => $"""<text:s text:c="{count}"/>""",
        (_, 1) => $""" <text:s text:c="{count - 1}"/>""",
        _ => string.Concat(Enumerable.Repeat("<text:s/>", count)),
    };
}
