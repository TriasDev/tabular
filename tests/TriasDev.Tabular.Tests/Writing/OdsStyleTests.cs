using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The common cell styles and data styles the ods writer states in styles.xml.</summary>
public sealed class OdsStyleTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Spreadsheet(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<RawCell[]> Rows(byte[] ods)
    {
        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(0, Token));

        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static string Entry(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task LibreOfficeAppliesTheCommonStyle()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId red = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0xF8696B), Font = new CellFont { Bold = true } });
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            writer.Write("hot", red);
            writer.EndRow();
        });

        byte[] xlsx = LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx");
        string styles = Entry(xlsx, "xl/styles.xml");

        Assert.Contains("rgb=\"FFF8696B\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("<b( val=\"(true|1)\")?/>", styles);
    }

    private static readonly CellStyle Legend = new()
    {
        Fill = CellColor.FromRgb(0xF8696B),
        Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true, Italic = true },
        NumberFormat = NumberFormat.Parse("#,##0.00"),
        DateFormat = DateFormat.Parse("dd/mm/yyyy"),
        Horizontal = CellHorizontalAlignment.Center,
        Wrap = true,
        Border = CellBorder.Thin(CellColor.FromRgb(0xBFBFBF)),
    };

    private static void EveryKind(TabularWriter writer, StyleId style)
    {
        writer.BeginSheet("data", [new("text"), new("long"), new("decimal"), new("double"), new("date"), new("stamp"), new("flag"), new("none")]);
        writer.BeginRow();
        writer.Write("R&D", style);
        writer.Write(1234567L, style);
        writer.Write(1234.5m, style);
        writer.Write(0.125, style);
        writer.Write(new DateOnly(2026, 10, 4), style);
        writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), style);
        writer.Write(true, style);
        writer.WriteEmpty(style);
        writer.EndRow();
    }

    [Fact]
    public async Task StatesTheStyleAsACommonStyle()
    {
        byte[] ods = await Spreadsheet(writer => EveryKind(writer, writer.RegisterStyle(Legend)));
        string styles = Entry(ods, "styles.xml");
        string content = Entry(ods, "content.xml");

        Assert.Contains("<number:number-style style:name=\"tn1\"><number:number number:decimal-places=\"2\" number:min-decimal-places=\"2\" number:min-integer-digits=\"1\" number:grouping=\"true\"/></number:number-style>", styles, StringComparison.Ordinal);
        Assert.Contains("<number:date-style style:name=\"tn2\"><number:day number:style=\"long\"/><number:text>/</number:text><number:month number:style=\"long\"/><number:text>/</number:text><number:year number:style=\"long\"/></number:date-style>", styles, StringComparison.Ordinal);
        Assert.Contains("fo:background-color=\"#F8696B\"", styles, StringComparison.Ordinal);
        Assert.Contains("fo:border=\"0.06pt solid #BFBFBF\"", styles, StringComparison.Ordinal);
        Assert.Contains("fo:wrap-option=\"wrap\"", styles, StringComparison.Ordinal);
        Assert.Contains("<style:paragraph-properties fo:text-align=\"center\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("fo:color=\"#FFFFFF\" fo:font-weight=\"bold\"", styles, StringComparison.Ordinal);
        Assert.Contains("fo:font-style=\"italic\"", styles, StringComparison.Ordinal);
        Assert.Contains("table:style-name=\"ts", content, StringComparison.Ordinal);
        Assert.Contains("<table:table-cell table:style-name=\"ts1\"/>", content, StringComparison.Ordinal);   // the styled empty cell
    }

    [Fact]
    public async Task TheImportReadsAStyledRowAsItReadsAnUnstyledOne()
    {
        byte[] plain = await Spreadsheet(writer => EveryKind(writer, default));
        byte[] styled = await Spreadsheet(writer => EveryKind(writer, writer.RegisterStyle(Legend)));

        Assert.Equal(Rows(plain).Select(r => r.ToArray()), Rows(styled).Select(r => r.ToArray()));
        Assert.Equal(RawCell.FromBoolean(true), Rows(styled)[1][6]);
    }

    [Fact]
    public async Task APercentFormatMakesAPercentageCell()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId percent = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse("0.0%") });
            writer.BeginSheet("data", [new("p")]);
            writer.BeginRow();
            writer.Write(0.125, percent);
            writer.EndRow();
        });

        Assert.Contains("office:value-type=\"percentage\" office:value=\"0.125\"", Entry(ods, "content.xml"), StringComparison.Ordinal);
        Assert.Equal(0.125, Rows(ods)[1][0].Number);
    }

    [Fact]
    public async Task AStyleRegisteredInTheMiddleOfTheDataIsWritten()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("n")]);

            for (int i = 1; i <= 1000; i++)
            {
                writer.BeginRow();
                writer.Write(i, i == 1000 ? writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x0000FF) }) : default);
                writer.EndRow();
            }
        });

        Assert.Contains("fo:background-color=\"#0000FF\"", Entry(ods, "styles.xml"), StringComparison.Ordinal);
        Assert.Equal(1000, Rows(ods)[1000][0].Number);
    }

    [Fact]
    public async Task LiteralTextInAFormatIsEscaped()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId style = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse("\"R&D <>\"0") });
            writer.BeginSheet("data", [new("n")]);
            writer.BeginRow();
            writer.Write(5L, style);
            writer.EndRow();
        });

        Assert.Contains("<number:text>R&amp;D &lt;&gt;</number:text>", Entry(ods, "styles.xml"), StringComparison.Ordinal);
        Assert.Equal(5, Rows(ods)[1][0].Number);
    }

    /// <summary>
    /// Every way a style's parts can be present or absent, on every kind of value: styles.xml and
    /// content.xml must stay well-formed. The reader never reads styles.xml, so no round trip would
    /// notice markup it broke.
    /// </summary>
    [Fact]
    public async Task WritesWellFormedStylesForEveryShapeOfStyle()
    {
        CellStyle[] shapes =
        [
            new() { Fill = CellColor.FromRgb(0x00FF00) },
            new() { Font = new CellFont { Italic = true } },
            new() { Font = new CellFont { Bold = true } },
            new() { Font = new CellFont { Color = CellColor.FromRgb(0x112233) } },
            new() { NumberFormat = NumberFormat.Parse("0.0%") },
            new() { NumberFormat = NumberFormat.Parse("0.00") },
            new() { NumberFormat = NumberFormat.Parse("0\" kg\"") },
            new() { DateFormat = DateFormat.Parse("d/m/yy") },
            new() { DateFormat = DateFormat.Parse("yyyy-mm-dd hh:mm:ss") },
            new() { Horizontal = CellHorizontalAlignment.Left },
            new() { Horizontal = CellHorizontalAlignment.Right },
            new() { Wrap = true },
            Legend,
        ];

        foreach (CellStyle shape in shapes)
        {
            byte[] ods = await Spreadsheet(writer => EveryKind(writer, writer.RegisterStyle(shape)));

            foreach (string part in (string[])["styles.xml", "content.xml"])
            {
                using System.Xml.XmlReader reader = System.Xml.XmlReader.Create(new StringReader(Entry(ods, part)));

                while (reader.Read())
                {
                    Assert.NotNull(reader.Name);
                }
            }
        }
    }

    [Fact]
    public async Task AStyleWithoutFormatsStatesTheDefaultDateAndBooleanStyles()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId fill = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0xFF0000) });
            writer.BeginSheet("data", [new("date"), new("stamp"), new("flag")]);
            writer.BeginRow();
            writer.Write(new DateOnly(2026, 1, 2), fill);
            writer.Write(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), fill);
            writer.Write(true, fill);
            writer.EndRow();
        });
        string styles = Entry(ods, "styles.xml");

        Assert.Contains("<number:date-style style:name=\"tnd\"><number:year number:style=\"long\"/><number:text>-</number:text><number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/></number:date-style>", styles, StringComparison.Ordinal);
        Assert.Contains("<number:date-style style:name=\"tndt\"><number:year number:style=\"long\"/><number:text>-</number:text><number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/><number:text> </number:text><number:hours number:style=\"long\"/><number:text>:</number:text><number:minutes number:style=\"long\"/><number:text>:</number:text><number:seconds number:style=\"long\"/></number:date-style>", styles, StringComparison.Ordinal);
        Assert.Contains("<number:boolean-style style:name=\"tnb\"><number:boolean/></number:boolean-style>", styles, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(styles, "style:name=\"tnd\""));
        Assert.Contains("<style:style style:name=\"ts1\" style:family=\"table-cell\" style:data-style-name=\"tnd\">", styles, StringComparison.Ordinal);
        Assert.Contains("<style:style style:name=\"ts2\" style:family=\"table-cell\" style:data-style-name=\"tndt\">", styles, StringComparison.Ordinal);
        Assert.Contains("<style:style style:name=\"ts3\" style:family=\"table-cell\" style:data-style-name=\"tnb\">", styles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoStylesOnOneKindOfValueStayApartEvenWhenTheSecondIsRegisteredLate()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId red = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0xFF0000) });
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write("red", red);
            writer.EndRow();

            StyleId blue = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x0000FF) });
            writer.BeginRow();
            writer.Write("blue", blue);
            writer.EndRow();
        });
        string styles = Entry(ods, "styles.xml");
        string content = Entry(ods, "content.xml");

        Assert.Contains("<style:style style:name=\"ts1\" style:family=\"table-cell\"><style:table-cell-properties fo:background-color=\"#FF0000\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<style:style style:name=\"ts2\" style:family=\"table-cell\"><style:table-cell-properties fo:background-color=\"#0000FF\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("table:style-name=\"ts1\" office:value-type=\"string\"><text:p>red</text:p>", content, StringComparison.Ordinal);
        Assert.Contains("table:style-name=\"ts2\" office:value-type=\"string\"><text:p>blue</text:p>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatesNumberDateAndCellPropertiesExactly()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            StyleId percent = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse("0.0%") });
            StyleId kilograms = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse("0\" kg\"") });
            StyleId shortDate = writer.RegisterStyle(new CellStyle { DateFormat = DateFormat.Parse("d/m/yy") });
            StyleId left = writer.RegisterStyle(new CellStyle { Horizontal = CellHorizontalAlignment.Left, Font = new CellFont { Italic = true } });
            StyleId right = writer.RegisterStyle(new CellStyle { Horizontal = CellHorizontalAlignment.Right, Font = new CellFont { Bold = true } });
            writer.BeginSheet("data", [new("p"), new("kg"), new("d"), new("l"), new("r")]);
            writer.BeginRow();
            writer.Write(0.5, percent);
            writer.Write(5L, kilograms);
            writer.Write(new DateOnly(2026, 1, 2), shortDate);
            writer.Write("l", left);
            writer.Write("r", right);
            writer.EndRow();
        });
        string styles = Entry(ods, "styles.xml");

        Assert.Contains("<number:percentage-style style:name=\"tn1\"><number:number number:decimal-places=\"1\" number:min-decimal-places=\"1\" number:min-integer-digits=\"1\"/><number:text>%</number:text></number:percentage-style>", styles, StringComparison.Ordinal);
        Assert.Contains("<number:number-style style:name=\"tn2\"><number:number number:decimal-places=\"0\" number:min-decimal-places=\"0\" number:min-integer-digits=\"1\"/><number:text> kg</number:text></number:number-style>", styles, StringComparison.Ordinal);
        Assert.Contains("<number:date-style style:name=\"tn3\"><number:day/><number:text>/</number:text><number:month/><number:text>/</number:text><number:year/></number:date-style>", styles, StringComparison.Ordinal);
        Assert.Contains("style:data-style-name=\"tn1\"", styles, StringComparison.Ordinal);
        Assert.Contains("<style:table-cell-properties style:text-align-source=\"fix\"/><style:paragraph-properties fo:text-align=\"start\"/><style:text-properties fo:font-style=\"italic\" style:font-style-asian=\"italic\" style:font-style-complex=\"italic\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<style:paragraph-properties fo:text-align=\"end\"/><style:text-properties fo:font-weight=\"bold\"", styles, StringComparison.Ordinal);

        string italicOnly = System.Text.RegularExpressions.Regex.Match(styles, "fo:text-align=\"start\"/><style:text-properties[^>]*>").Value;
        Assert.DoesNotContain("font-weight", italicOnly, StringComparison.Ordinal);
        string boldOnly = System.Text.RegularExpressions.Regex.Match(styles, "fo:text-align=\"end\"/><style:text-properties[^>]*>").Value;
        Assert.DoesNotContain("font-style", boldOnly, StringComparison.Ordinal);
    }
}
