using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

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
        NumberFormat = NumberFormat.Parse("#,##0.00"),
        DateFormat = DateFormat.Parse("dd/mm/yyyy"),
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

    /// <summary>The <c>&lt;xf&gt;</c> elements of <c>cellXfs</c>, in index order.</summary>
    private static List<string> CellXfs(string styles)
    {
        string cellXfs = styles[styles.IndexOf("<cellXfs", StringComparison.Ordinal)..styles.IndexOf("</cellXfs>", StringComparison.Ordinal)];
        return [.. Regex.Matches(cellXfs, "<xf [^>]*?(/>|>.*?</xf>)").Select(m => m.Value)];
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

    /// <summary>Fonts and alignments other than the bold, italic, centred and wrapped legend: each part of their markup stands alone.</summary>
    [Fact]
    public async Task StatesEachFontAndAlignmentOnItsOwn()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId[] styles =
            [
                writer.RegisterStyle(new CellStyle { Font = new CellFont { Color = CellColor.FromRgb(0x112233) } }),
                writer.RegisterStyle(new CellStyle { Font = new CellFont { Bold = true } }),
                writer.RegisterStyle(new CellStyle { Font = new CellFont { Italic = true } }),
                writer.RegisterStyle(new CellStyle { Horizontal = CellHorizontalAlignment.Left }),
                writer.RegisterStyle(new CellStyle { Horizontal = CellHorizontalAlignment.Right }),
                writer.RegisterStyle(new CellStyle { Wrap = true }),
            ];
            writer.BeginSheet("data", [new("v")]);

            foreach (StyleId style in styles)
            {
                writer.BeginRow();
                writer.Write("x", style);
                writer.EndRow();
            }
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));

        string styles = Part(xlsx, "xl/styles.xml");
        Assert.Contains("<font><sz val=\"11\"/><color rgb=\"FF112233\"/><name val=\"Calibri\"/></font>", styles, StringComparison.Ordinal);
        Assert.Contains("<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>", styles, StringComparison.Ordinal);
        Assert.Contains("<font><i/><sz val=\"11\"/><name val=\"Calibri\"/></font>", styles, StringComparison.Ordinal);
        Assert.Contains("<alignment horizontal=\"left\"/></xf>", styles, StringComparison.Ordinal);
        Assert.Contains("<alignment horizontal=\"right\"/></xf>", styles, StringComparison.Ordinal);
        Assert.Contains("<alignment wrapText=\"1\"/></xf>", styles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStyledWorkbookIsValidAndStatesTheStyle()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, writer.RegisterStyle(Legend)));

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
        byte[] styled = await Workbook(writer => EveryKind(writer, writer.RegisterStyle(Legend)));

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
        byte[] xlsx = await Workbook(writer => EveryKind(writer, writer.RegisterStyle(Legend)));
        string styles = Part(xlsx, "xl/styles.xml");

        // text/flag/none share one xf (General), long/decimal/double share the number format, date and stamp the date format.
        Assert.Contains("<cellXfs count=\"7\">", styles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EqualStylesAreWrittenOnce()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId a = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x00FF00) });
            StyleId b = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x00FF00) });
            StyleId c = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x00FF00), Wrap = true });
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
                writer.Write(i, i == 1000 ? writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x0000FF) }) : default);
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
            StyleId style = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse("\"R&D <\"0") });
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
            StyleId style = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse(code) });
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
            StyleId style = writer.RegisterStyle(new CellStyle { DateFormat = DateFormat.Parse("dd \"\U0001F4C5\" yyyy") });
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
            StyleId style = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse("\"a\tb\"0") });
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
    public async Task AClosingBracketAndLineEndsInALiteralAreEscaped()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId style = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse("\"a>b\nc\rd\"0") });
            writer.BeginSheet("data", [new("n")]);
            writer.BeginRow();
            writer.Write(5L, style);
            writer.EndRow();
        });

        string styles = Part(xlsx, "xl/styles.xml");

        // An attribute's line ends are normalised to spaces unless written as references; '>' is escaped as '<' is.
        Assert.Contains("formatCode=\"&quot;a&gt;b&#10;c&#13;d&quot;0\"", styles, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>A styled decimal takes the number format a styled long takes, and a styled flag the format a styled text takes: neither is dropped to unstyled.</summary>
    [Fact]
    public async Task AStyledDecimalAndAStyledBooleanKeepTheirStyle()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, writer.RegisterStyle(Legend)));
        string sheet = Part(xlsx, "xl/worksheets/sheet1.xml");

        Match text = Regex.Match(sheet, "<c s=\"(\\d+)\" t=\"inlineStr\"><is><t>R&amp;D</t>");
        Match numbers = Regex.Match(sheet, "<c s=\"(\\d+)\"><v>1234567</v></c><c s=\"(\\d+)\"><v>1234.5</v></c>");
        Match flag = Regex.Match(sheet, "<c s=\"(\\d+)\" t=\"b\"><v>1</v></c>");

        Assert.True(text.Success);
        Assert.True(numbers.Success);
        Assert.True(flag.Success);
        Assert.Equal(numbers.Groups[1].Value, numbers.Groups[2].Value);
        Assert.Equal(text.Groups[1].Value, flag.Groups[1].Value);
        Assert.Contains("numFmtId=\"165\"", CellXfs(Part(xlsx, "xl/styles.xml"))[int.Parse(numbers.Groups[2].Value, CultureInfo.InvariantCulture)], StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    /// <summary>
    /// A style that states no date format still shows a date as a date and a date with its time as both:
    /// built-in 14 for the one, the fixed 164 for the other, in two cell formats that keep the fill.
    /// </summary>
    [Fact]
    public async Task AStyledDateWithoutADateFormatTakesTheDefaultForItsKind()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId fill = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x00FF00) });
            writer.BeginSheet("data", [new("date"), new("stamp")]);
            writer.BeginRow();
            writer.Write(new DateOnly(2026, 10, 4), fill);
            writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), fill);
            writer.EndRow();
        });

        Match cells = Regex.Match(Part(xlsx, "xl/worksheets/sheet1.xml"), "<row r=\"2\"><c s=\"(\\d+)\"><v>[^<]+</v></c><c s=\"(\\d+)\"><v>[^<]+</v></c></row>");
        Assert.True(cells.Success);

        List<string> xfs = CellXfs(Part(xlsx, "xl/styles.xml"));
        string date = xfs[int.Parse(cells.Groups[1].Value, CultureInfo.InvariantCulture)];
        string stamp = xfs[int.Parse(cells.Groups[2].Value, CultureInfo.InvariantCulture)];

        Assert.StartsWith("<xf numFmtId=\"14\" fontId=\"0\" fillId=\"2\"", date, StringComparison.Ordinal);
        Assert.StartsWith("<xf numFmtId=\"164\" fontId=\"0\" fillId=\"2\"", stamp, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));

        RawCell[] row = Rows(xlsx)[1];
        Assert.Equal(RawCell.FromDate(new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Unspecified)), row[0]);
        Assert.Equal(RawCell.FromDate(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified)), row[1]);
    }

    /// <summary>
    /// A style registered after another was already resolved lies past the cache the first resolution
    /// sized; its own resolution grows the cache rather than reading or writing past its end.
    /// </summary>
    [Fact]
    public async Task AStyleRegisteredAfterTheFirstWasResolvedIsWritten()
    {
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId first = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x00FF00) });
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write("a", first);
            writer.EndRow();

            StyleId second = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0x0000FF) });
            writer.BeginRow();
            writer.Write("b", second);
            writer.EndRow();
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("<fgColor rgb=\"FF0000FF\"/>", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
        Assert.Contains("<row r=\"3\"><c s=\"5\" t=\"inlineStr\"><is><t>b</t>", Part(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatesEveryPartOfTheLegendInItsCellFormats()
    {
        // Each apply flag beside the index it applies: without them Excel shows the indices anyway,
        // other readers do not — and nothing reads the flags back, so only the markup can say.
        byte[] xlsx = await Workbook(writer => EveryKind(writer, writer.RegisterStyle(Legend)));

        Assert.Equal(
            [
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>",
                "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>",
                "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>",
                "<xf numFmtId=\"1\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>",
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" wrapText=\"1\"/></xf>",
                "<xf numFmtId=\"165\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" wrapText=\"1\"/></xf>",
                "<xf numFmtId=\"166\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" wrapText=\"1\"/></xf>",
            ],
            CellXfs(Part(xlsx, "xl/styles.xml")));
    }

    [Fact]
    public async Task AStyleThatComesOutAsAFixedFormatReusesIt()
    {
        // A number format on a date leaves the date its built-in format and nothing else: the fixed
        // date format already is that, so it is reused rather than written a second time.
        byte[] xlsx = await Workbook(writer =>
        {
            StyleId amounts = writer.RegisterStyle(new CellStyle { NumberFormat = NumberFormat.Parse("0.00") });
            writer.BeginSheet("data", [new("amount"), new("day")]);
            writer.BeginRow();
            writer.Write(1.5, amounts);
            writer.Write(new DateOnly(2026, 11, 17), amounts);
            writer.EndRow();
        });

        Assert.Equal(5, CellXfs(Part(xlsx, "xl/styles.xml")).Count);
        Assert.Matches("<row r=\"2\"><c s=\"4\"><v>1.5</v></c><c s=\"1\"><v>", Part(xlsx, "xl/worksheets/sheet1.xml"));
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    [Fact]
    public async Task AnUnstyledWorkbookKeepsItsFourFormats()
    {
        byte[] xlsx = await Workbook(writer => EveryKind(writer, default));

        Assert.Contains("<cellXfs count=\"4\">", Part(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }
}
