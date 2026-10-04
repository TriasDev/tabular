using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>How a sheet is laid out: header style, frozen panes, filter, merges.</summary>
public sealed class SheetLayoutTests
{
    private static readonly CellStyle Header = new() { Fill = CellColor.FromRgb(0x1F4E78), Font = new CellFont { Color = CellColor.FromRgb(0xFFFFFF), Bold = true } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task<byte[]> Write(TabularFormat format, Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, format))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    internal static string Entry(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        ZipArchiveEntry? entry = archive.GetEntry(name);
        Assert.NotNull(entry);
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static bool HasEntry(byte[] zip, string name)
    {
        using ZipArchive archive = new(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        return archive.GetEntry(name) is not null;
    }

    internal static List<RawCell[]> Rows(byte[] file, int sheet = 0)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(file, writable: false), "file", cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(sheet, Token));
        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static void Frozen(TabularWriter writer)
    {
        writer.BeginSheet("data", [new("a"), new("b"), new("c")], new SheetOptions { HeaderStyle = Header, FreezeRows = 1, FreezeColumns = 1 });
        writer.BeginRow();
        writer.Write("x");
        writer.Write(1L);
        writer.Write(2L);
        writer.EndRow();
        writer.BeginSheet("rows only", [new("a")], new SheetOptions { FreezeRows = 2 });
        writer.BeginSheet("plain", [new("a")]);
    }

    [Fact]
    public async Task XlsxFreezesEachSheetAsAsked()
    {
        byte[] xlsx = await Write(TabularFormat.Xlsx, Frozen);

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("<sheetViews><sheetView workbookViewId=\"0\"><pane xSplit=\"1\" ySplit=\"1\" topLeftCell=\"B2\" activePane=\"bottomRight\" state=\"frozen\"/><selection pane=\"bottomRight\"/></sheetView></sheetViews>", Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        Assert.Contains("<pane ySplit=\"2\" topLeftCell=\"A3\" activePane=\"bottomLeft\" state=\"frozen\"/><selection pane=\"bottomLeft\"/>", Entry(xlsx, "xl/worksheets/sheet2.xml"), StringComparison.Ordinal);
        Assert.DoesNotContain("<sheetViews>", Entry(xlsx, "xl/worksheets/sheet3.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsFreezesEachSheetInItsSettings()
    {
        byte[] ods = await Write(TabularFormat.Ods, Frozen);
        string settings = Entry(ods, "settings.xml");

        Assert.Contains("<config:config-item-map-entry config:name=\"data\"><config:config-item config:name=\"HorizontalSplitMode\" config:type=\"short\">2</config:config-item><config:config-item config:name=\"VerticalSplitMode\" config:type=\"short\">2</config:config-item><config:config-item config:name=\"HorizontalSplitPosition\" config:type=\"int\">1</config:config-item><config:config-item config:name=\"VerticalSplitPosition\" config:type=\"int\">1</config:config-item>", settings, StringComparison.Ordinal);
        Assert.Contains("config:name=\"rows only\"><config:config-item config:name=\"HorizontalSplitMode\" config:type=\"short\">0</config:config-item><config:config-item config:name=\"VerticalSplitMode\" config:type=\"short\">2</config:config-item>", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("config:name=\"plain\"", settings, StringComparison.Ordinal);
        Assert.Contains("manifest:full-path=\"settings.xml\"", Entry(ods, "META-INF/manifest.xml"), StringComparison.Ordinal);

        // The "ooo:" in the view-settings name is a QName; without its declaration LibreOffice ignores the set.
        Assert.Contains("xmlns:ooo=\"http://openoffice.org/2004/office\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsWithoutFrozenPanesHasNoSettings()
    {
        byte[] ods = await Write(TabularFormat.Ods, writer => writer.BeginSheet("plain", [new("a")]));

        Assert.False(HasEntry(ods, "settings.xml"));
        Assert.DoesNotContain("settings.xml", Entry(ods, "META-INF/manifest.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    [InlineData(TabularFormat.Csv)]
    public async Task AStyledHeaderReadsBackAsTheSameHeader(TabularFormat format)
    {
        byte[] styled = await Write(format, writer =>
        {
            writer.BeginSheet("data", [new("Name"), new("Count")], new SheetOptions { HeaderStyle = Header });
            writer.BeginRow();
            writer.Write("x");
            writer.Write(1L);
            writer.EndRow();
        });

        List<RawCell[]> rows = Rows(styled);
        Assert.Equal([RawCell.FromText("Name"), RawCell.FromText("Count")], rows[0]);
        Assert.Equal(format == TabularFormat.Csv ? RawCell.FromText("1") : RawCell.FromNumber(1), rows[1][1]);
    }

    [Fact]
    public async Task TheXlsxHeaderCarriesItsStyle()
    {
        byte[] xlsx = await Write(TabularFormat.Xlsx, writer => writer.BeginSheet("data", [new("Name")], new SheetOptions { HeaderStyle = Header }));

        Assert.Contains("<row r=\"1\"><c s=\"4\" t=\"inlineStr\">", Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        Assert.Contains("<fgColor rgb=\"FF1F4E78\"/>", Entry(xlsx, "xl/styles.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOdsHeaderCarriesItsStyle()
    {
        byte[] ods = await Write(TabularFormat.Ods, writer => writer.BeginSheet("data", [new("Name")], new SheetOptions { HeaderStyle = Header }));

        Assert.Contains("<table:table-cell table:style-name=\"ts1\" office:value-type=\"string\">", Entry(ods, "content.xml"), StringComparison.Ordinal);
        Assert.Contains("fo:background-color=\"#1F4E78\"", Entry(ods, "styles.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 4)]
    [InlineData(1_048_576, 0)]
    public async Task AFreezeOutsideTheSheetIsRefused(int rows, int columns)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.BeginSheet("data", [new("a"), new("b"), new("c")], new SheetOptions { FreezeRows = rows, FreezeColumns = columns }));
    }

    [Fact]
    public async Task CsvIgnoresTheLayout()
    {
        byte[] plain = await Write(TabularFormat.Csv, writer => writer.BeginSheet("data", [new("a"), new("b")]));
        byte[] laidOut = await Write(TabularFormat.Csv, writer => writer.BeginSheet("data", [new("a"), new("b")], new SheetOptions { HeaderStyle = Header, FreezeRows = 1, FreezeColumns = 1, AutoFilter = true }));

        Assert.Equal(plain, laidOut);
    }

    private static void Filtered(TabularWriter writer)
    {
        writer.BeginSheet("Bob's data", [new("a"), new("b"), new("c")], new SheetOptions { AutoFilter = true });

        for (int i = 0; i < 3; i++)
        {
            writer.BeginRow();
            writer.Write((long)i);
            writer.EndRow();
        }

        writer.BeginSheet("plain", [new("a")]);
        writer.BeginSheet("second", [new("a"), new("b")], new SheetOptions { AutoFilter = true });
    }

    [Fact]
    public async Task XlsxFiltersTheHeaderThroughTheLastRow()
    {
        byte[] xlsx = await Write(TabularFormat.Xlsx, Filtered);

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("</sheetData><autoFilter ref=\"A1:C4\"/></worksheet>", Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
        Assert.DoesNotContain("autoFilter", Entry(xlsx, "xl/worksheets/sheet2.xml"), StringComparison.Ordinal);
        Assert.Contains("<autoFilter ref=\"A1:B1\"/>", Entry(xlsx, "xl/worksheets/sheet3.xml"), StringComparison.Ordinal);

        string workbook = Entry(xlsx, "xl/workbook.xml");
        Assert.Contains("<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'Bob''s data'!$A$1:$C$4</definedName><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"2\" hidden=\"1\">'second'!$A$1:$B$1</definedName></definedNames>", workbook, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsFiltersTheHeaderThroughTheLastRow()
    {
        byte[] ods = await Write(TabularFormat.Ods, Filtered);
        string content = Entry(ods, "content.xml");

        Assert.Contains("</table:table><table:database-ranges><table:database-range table:name=\"__Anonymous_Sheet_DB__0\" table:target-range-address=\"'Bob''s data'.A1:'Bob''s data'.C4\" table:display-filter-buttons=\"true\"/><table:database-range table:name=\"__Anonymous_Sheet_DB__2\" table:target-range-address=\"'second'.A1:'second'.B1\" table:display-filter-buttons=\"true\"/></table:database-ranges></office:spreadsheet>", content, StringComparison.Ordinal);
        Assert.Equal(4, Rows(ods).Count);
    }
}
