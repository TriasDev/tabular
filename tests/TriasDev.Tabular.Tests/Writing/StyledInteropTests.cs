using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>LibreOffice shows our formats and keeps our fills and fonts.</summary>
public sealed class StyledInteropTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly CellStyle Red = new()
    {
        Fill = CellColor.FromRgb(0xF8696B),
        Font = new CellFont { Bold = true },
    };

    private static async Task<byte[]> Write(TabularFormat format)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            StyleId twoPlaces = writer.Style(new CellStyle { Number = NumberFormat.Parse("0.00") });
            StyleId percent = writer.Style(new CellStyle { Number = NumberFormat.Parse("0.0%") });
            StyleId prefixed = writer.Style(new CellStyle { Number = NumberFormat.Parse("\"EUR \"0") });
            StyleId day = writer.Style(new CellStyle { Date = DateFormat.Parse("dd/mm/yyyy") });
            StyleId stamp = writer.Style(new CellStyle { Date = DateFormat.Parse("dd.mm.yyyy hh:mm") });
            StyleId red = writer.Style(Red);

            writer.BeginSheet("data", [new("a"), new("b"), new("c"), new("d"), new("e"), new("f")]);
            writer.BeginRow();
            writer.Write(12.5, twoPlaces);
            writer.Write(0.125, percent);
            writer.Write(42L, prefixed);
            writer.Write(new DateOnly(2026, 10, 4), day);
            writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), stamp);
            writer.Write("hot", red);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static string Entry(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    [InlineData(TabularFormat.Ods, "ods")]
    public async Task ShowsTheFormats(TabularFormat format, string extension)
    {
        string[] lines = LibreOffice.ConvertToCsv(await Write(format), extension);

        // The decimal separator is the LibreOffice profile's locale; the rest is what the codes state.
        Assert.Matches("^\"?12[.,]50\"?,\"?12[.,]5%\"?,\"?EUR 42\"?,04/10/2026,04\\.10\\.2026 09:05,\"?hot\"?$", lines[1]);
    }

    [Fact]
    public async Task KeepsTheFillAndFontOfAnXlsx()
    {
        byte[] ods = LibreOffice.Convert(await Write(TabularFormat.Xlsx), "xlsx", "ods", "ods");
        string styles = Entry(ods, "content.xml") + Entry(ods, "styles.xml");

        Assert.Contains("fo:background-color=\"#f8696b\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fo:font-weight=\"bold\"", styles, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsTheFillAndFontOfAnOds()
    {
        byte[] xlsx = LibreOffice.Convert(await Write(TabularFormat.Ods), "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx");
        string styles = Entry(xlsx, "xl/styles.xml");

        Assert.Contains("rgb=\"FFF8696B\"", styles, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("<b( val=\"(true|1)\")?/>", styles);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheFilterOfAnOds()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, writer =>
        {
            writer.BeginSheet("Bob's data", [new("a"), new("b")], new SheetOptions { AutoFilter = true });
            writer.BeginRow();
            writer.Write(1L);
            writer.EndRow();
        });

        Assert.Contains("<autoFilter ref=\"A1:B2\"", Entry(LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx"), "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheFilterOfAnXlsx()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            writer.BeginSheet("Bob's data", [new("a"), new("b")], new SheetOptions { AutoFilter = true });
            writer.BeginRow();
            writer.Write(1L);
            writer.EndRow();
        });

        Assert.Contains("table:display-filter-buttons=\"true\"", Entry(LibreOffice.Convert(xlsx, "xlsx", "ods", "ods"), "content.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheMergesOfAnXlsx()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, MergeTests.Legend);
        string content = Entry(LibreOffice.Convert(xlsx, "xlsx", "ods", "ods"), "content.xml");

        Assert.Contains("table:number-columns-spanned=\"2\"", content, StringComparison.Ordinal);
        Assert.Contains("table:number-rows-spanned=\"2\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheMergesOfAnOds()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, MergeTests.Legend);
        string sheet = Entry(LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx"), "xl/worksheets/sheet1.xml");

        Assert.Contains("<mergeCell ref=\"A2:B2\"/>", sheet, StringComparison.Ordinal);
        Assert.Contains("<mergeCell ref=\"B3:C4\"/>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheMergeOfARowThatEndsEarly()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, MergeTests.EndsEarly);
        string sheet = Entry(LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx"), "xl/worksheets/sheet1.xml");

        Assert.Contains("<mergeCell ref=\"C2:D3\"/>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheMergeAndFillOfAnXlsx()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, FilledMerge);
        byte[] ods = LibreOffice.Convert(xlsx, "xlsx", "ods", "ods");
        string styles = Entry(ods, "content.xml") + Entry(ods, "styles.xml");

        Assert.Contains("table:number-columns-spanned=\"3\"", styles, StringComparison.Ordinal);
        Assert.Contains("fo:background-color=\"#f8696b\"", styles, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LibreOfficeKeepsTheMergeAndFillOfAnOds()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, FilledMerge);
        byte[] xlsx = LibreOffice.Convert(ods, "ods", "xlsx:Calc MS Excel 2007 XML", "xlsx");

        Assert.Contains("<mergeCell ref=\"A2:C2\"/>", Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        Assert.Contains("rgb=\"FFF8696B\"", Entry(xlsx, "xl/styles.xml"), StringComparison.OrdinalIgnoreCase);
    }

    private static void FilledMerge(TabularWriter writer)
    {
        StyleId fill = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0xF8696B) });
        writer.BeginSheet("data", [new("a"), new("b"), new("c")]);
        writer.BeginRow();
        writer.Merge(1, 3);
        writer.Write("filled", fill);
        writer.EndRow();
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx, "xlsx")]
    [InlineData(TabularFormat.Ods, "ods")]
    public async Task ALaidOutWorkbookOpensAndReadsBack(TabularFormat format, string extension)
    {
        CellStyle header = new() { Fill = CellColor.FromRgb(0x1F4E78), Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true } };
        CellStyle[] legend = [new() { Fill = CellColor.FromRgb(0x63BE7B) }, new() { Fill = CellColor.FromRgb(0xFFEB84) }, new() { Fill = CellColor.FromRgb(0xF8696B) }];

        byte[] file = await SheetLayoutTests.Write(format, writer =>
        {
            StyleId[] colours = [.. legend.Select(writer.Style)];
            StyleId title = writer.Style(new CellStyle { Font = new CellFont { Bold = true }, Horizontal = HorizontalAlignment.Center });

            writer.BeginSheet("Data", [new("Id"), new("Score"), new("Date", 12)], new SheetOptions { HeaderStyle = header, FreezeRows = 1, AutoFilter = true });

            for (int i = 1; i <= 100; i++)
            {
                writer.BeginRow();
                writer.Write((long)i);
                writer.Write(i / 10.0, colours[i % 3]);
                writer.Write(new DateOnly(2026, 1, 1).AddDays(i));
                writer.EndRow();
            }

            writer.BeginSheet("Legend", [new("Range"), new("Colour"), new("Meaning")], new SheetOptions { HeaderStyle = header });
            writer.BeginRow();
            writer.Merge(1, 3);
            writer.Write("Score legend", title);
            writer.EndRow();

            for (int i = 0; i < legend.Length; i++)
            {
                writer.BeginRow();
                writer.Write($"{i * 3}–{(i * 3) + 3}");
                writer.WriteEmpty(colours[i]);
                writer.Write(i switch { 0 => "low", 1 => "medium", _ => "high" });
                writer.EndRow();
            }
        });

        if (format == TabularFormat.Xlsx)
        {
            Assert.Empty(OoxmlValidation.Errors(file));
        }

        string[] lines = LibreOffice.ConvertToCsv(file, extension);
        Assert.Equal(101, lines.Length);

        List<RawCell[]> data = SheetLayoutTests.Rows(file, 0);
        Assert.Equal(101, data.Count);
        Assert.Equal(RawCell.FromNumber(5), data[50][1]);

        List<RawCell[]> legendRows = SheetLayoutTests.Rows(file, 1);
        Assert.Equal(RawCell.FromText("Score legend"), legendRows[1][0]);
        Assert.Equal(RawCell.FromText("high"), legendRows[4][2]);
    }
}
