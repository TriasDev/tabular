using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Styles in the workbook: valid to the schema, deduplicated, and invisible to the import.</summary>
public sealed class XlsxStyleTests
{
    private static readonly CellStyle Legend = new()
    {
        Fill = CellColor.FromRgb(0xF8696B),
        Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true, Italic = true },
        Number = NumberFormat.Parse("#,##0.00"),
        Date = DateFormat.Parse("dd/mm/yyyy"),
        Horizontal = CellHorizontalAlignment.Center,
        Wrap = true,
        Border = CellBorder.Thin(CellColor.FromRgb(0xBFBFBF)),
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Workbook(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static string Part(byte[] xlsx, string name)
    {
        using ZipArchive archive = new(new MemoryStream(xlsx, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static List<RawCell[]> Rows(byte[] xlsx)
    {
        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(0, Token));
        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    /// <summary>One row of every kind, all in one style (or unstyled).</summary>
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
    public async Task AStyledWorkbookIsValidAndStatesTheStyle()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, writer.Style(Legend)));

        Assert.Empty(OoxmlValidation.Errors(xlsx));

        string styles = Part(xlsx, "xl/styles.xml");
        Assert.Contains("<numFmt numFmtId=\"165\" formatCode=\"#,##0.00\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<numFmt numFmtId=\"166\" formatCode=\"dd\\/mm\\/yyyy\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<fgColor rgb=\"FFF8696B\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<b/><i/>", styles, StringComparison.Ordinal);
        Assert.Contains("<color rgb=\"FFFFFFFF\"/>", styles, StringComparison.Ordinal);
        Assert.Contains("<left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left>", styles, StringComparison.Ordinal);
        Assert.Contains("<alignment horizontal=\"center\" wrapText=\"1\"/>", styles, StringComparison.Ordinal);

        string sheet = Part(xlsx, "xl/worksheets/sheet1.xml");
        Assert.Matches("<c s=\"\\d+\"/></row>", sheet);      // the styled empty cell is written, after the flag in G2
    }

    [Fact]
    public async Task TheImportReadsAStyledRowAsItReadsAnUnstyledOne()
    {
        byte[] plain = await Workbook(writer => EveryKind(writer, default));
        byte[] styled = await Workbook(writer => EveryKind(writer, writer.Style(Legend)));

        // The unstyled empty cell is left out; the styled one is written, and reads as an empty cell at the end of the row.
        Assert.Equal(Rows(plain)[0], Rows(styled)[0]);
        Assert.Equal(Rows(plain)[1], Rows(styled)[1].Take(7));
        Assert.Equal(8, Rows(styled)[1].Length);
        Assert.Equal(RawCell.FromDate(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Unspecified)), Rows(styled)[1][4]);
        Assert.Equal(RawCell.FromBoolean(true), Rows(styled)[1][6]);
        Assert.True(Rows(styled)[1][7].IsEmpty);
    }

    [Fact]
    public async Task OneStyleOnSeveralKindsTakesAFormatPerKind()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, writer.Style(Legend)));
        string styles = Part(xlsx, "xl/styles.xml");

        // text/flag/none share one xf (General), long/decimal/double share the number format, date and stamp the date format.
        Assert.Contains("<cellXfs count=\"7\">", styles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EqualStylesAreWrittenOnce()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId a = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x00FF00) });
            StyleId b = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x00FF00) });
            StyleId c = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x00FF00), Wrap = true });
            writer.BeginSheet("data", [new("a"), new("b"), new("c")]);
            writer.BeginRow();
            writer.Write("x", a);
            writer.Write("y", b);
            writer.Write("z", c);
            writer.EndRow();
        });

        string styles = Part(xlsx, "xl/styles.xml");
        Assert.Contains("<fills count=\"3\">", styles, StringComparison.Ordinal);          // none, gray125, one green
        Assert.Contains("<cellXfs count=\"6\">", styles, StringComparison.Ordinal);        // four fixed + two
    }

    [Fact]
    public async Task AStyleRegisteredInTheMiddleOfTheDataIsWritten()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            writer.BeginSheet("data", [new("n")]);

            for (int i = 1; i <= 1000; i++)
            {
                writer.BeginRow();
                writer.Write(i, i == 1000 ? writer.Style(new CellStyle { Fill = CellColor.FromRgb(0x0000FF) }) : default);
                writer.EndRow();
            }
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("<fgColor rgb=\"FF0000FF\"/>", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
        Assert.Contains("<row r=\"1001\"><c s=\"4\"><v>1000</v></c>", Part(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiteralTextInAFormatIsEscaped()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId style = writer.Style(new CellStyle { Number = NumberFormat.Parse("\"R&D <\"0") });
            writer.BeginSheet("data", [new("n")]);
            writer.BeginRow();
            writer.Write(5L, style);
            writer.EndRow();
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("formatCode=\"&quot;R&amp;D &lt;&quot;0\"", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"x\\\"0\" days\"")]
    [InlineData("\"d m y h s\"0")]
    [InlineData("0\" dd mm yyyy hh ss\"")]
    [InlineData("\"\\d\"0\"\\y\"")]
    [InlineData("\"[\"0\"]\"")]
    [InlineData("\"[d]\"0")]
    [InlineData("\"%\"0.0")]
    [InlineData("0.0%\" d\\\"")]
    [InlineData("\"\\\"#,##0\" s\"")]
    public async Task EveryAcceptedNumberFormatShapeReadsBackAsTheNumber(string code)
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId style = writer.Style(new CellStyle { Number = NumberFormat.Parse(code) });
            writer.BeginSheet("data", [new("a"), new("b"), new("c")]);
            writer.BeginRow();
            writer.Write(42.0, style);
            writer.Write(42L, style);
            writer.Write(42m, style);
            writer.EndRow();
        });

        RawCell[] row = Rows(xlsx)[1];

        Assert.All(row, cell => Assert.Equal(RawCell.FromNumber(42), cell));
    }

    [Fact]
    public async Task AnEmojiLiteralInADateFormatSurvivesIntact()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId style = writer.Style(new CellStyle { Date = DateFormat.Parse("dd \"\U0001F4C5\" yyyy") });
            writer.BeginSheet("data", [new("d")]);
            writer.BeginRow();
            writer.Write(new DateOnly(2026, 10, 4), style);
            writer.EndRow();
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("\U0001F4C5", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', Part(xlsx, "xl/styles.xml"));
        Assert.Equal(RawCell.FromDate(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Unspecified)), Rows(xlsx)[1][0]);
    }

    [Fact]
    public async Task ATabInALiteralIsWrittenAsACharacterReference()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId style = writer.Style(new CellStyle { Number = NumberFormat.Parse("\"a\tb\"0") });
            writer.BeginSheet("data", [new("n")]);
            writer.BeginRow();
            writer.Write(5L, style);
            writer.EndRow();
        });

        string styles = Part(xlsx, "xl/styles.xml");

        Assert.Contains("a&#9;b", styles, StringComparison.Ordinal);
        Assert.DoesNotContain('\t', styles);
    }

    [Fact]
    public async Task AnUnstyledWorkbookKeepsItsFourFormats()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, default));

        Assert.Contains("<cellXfs count=\"4\">", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }
}
