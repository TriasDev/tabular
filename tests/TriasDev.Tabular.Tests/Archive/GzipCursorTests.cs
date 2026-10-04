using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A gzip file read as the file inside it: a csv streamed, a workbook copied out.</summary>
public sealed class GzipCursorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GzipCursor Open(byte[] file, string name = "data.csv.gz", TabularOpenOptions? options = null) =>
        new(new MemoryStream(file, writable: false), name, options, Token);

    private static List<string?[]> ReadAll(ITabularCursor cursor)
    {
        List<string?[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            string?[] row = new string?[cursor.CurrentRow.Length];

            for (int i = 0; i < row.Length; i++)
            {
                row[i] = cursor.CurrentRow[i].AsText();
            }

            rows.Add(row);
        }

        return rows;
    }

    private static string Lines(int count) => "id;name\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"{i};Name {i}\n"));

    private static string InlineRow(int number, params string[] values) =>
        $"<row r=\"{number}\">" + string.Concat(values.Select(v => $"<c t=\"inlineStr\"><is><t>{v}</t></is></c>")) + "</row>";

    private static byte[] Workbook(params (string Sheet, string Rows)[] sheets)
    {
        XlsxPackage package = new();

        foreach ((string sheet, string rows) in sheets)
        {
            package.WithSheet(sheet, rows);
        }

        return package.Build();
    }

    private static byte[] Spreadsheet(string table, string text) =>
        new OdsPackage().WithTable(table,
            $"<table:table-row><table:table-cell office:value-type=\"string\"><text:p>{text}</text:p></table:table-cell></table:table-row>").Build();

    [Fact]
    public void ReadsACompressedCsvAsTheSameRowsAsTheFileItself()
    {
        const string Csv = "id;city\n1;Köln\n2;\"Bonn; am Rhein\"\n";
        using GzipCursor cursor = Open(GzipFile.Of(Csv));
        using CsvCursor plain = new(new MemoryStream(Encoding.UTF8.GetBytes(Csv)), "data.csv");

        Assert.Equal(TabularFormat.Gzip, cursor.Format);
        SheetInfo sheet = Assert.Single(cursor.Sheets);
        Assert.Equal("data.csv", sheet.Name);
        Assert.Null(sheet.Source);
        Assert.Equal(TabularFormat.Csv, sheet.Format);
        Assert.Equal(';', cursor.Dialect!.Delimiter);
        Assert.Equal(ReadAll(plain), ReadAll(cursor));
    }

    [Fact]
    public void TakesTheInnerFileNameFromTheHeaderWhenItStoresOne()
    {
        using GzipCursor cursor = Open(GzipFile.Of("a;b\n", "Export 2025.csv"), "upload.bin");

        Assert.Equal("Export 2025.csv", cursor.Sheets[0].Name);
        Assert.Equal("Export 2025.csv", cursor.Sheets[0].Source);
    }

    [Theory]
    [InlineData("data.csv.gz", "data.csv")]
    [InlineData("DATA.CSV.GZ", "DATA.CSV")]
    [InlineData("upload", "upload")]
    [InlineData(".gz", ".gz")]
    public void NamesTheSheetAfterTheFileWithoutItsGzExtension(string name, string expected)
    {
        using GzipCursor cursor = Open(GzipFile.Of("a;b\n"), name);

        Assert.Equal(expected, cursor.Sheets[0].Name);
    }

    [Fact]
    public void ReadsTheSheetAgainFromItsFirstRow()
    {
        using GzipCursor cursor = Open(GzipFile.Of("a\n1\n2\n"));

        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.MoveToSheet(0, Token));

        string?[][] expected = [["a"], ["1"], ["2"]];
        Assert.Equal(expected, ReadAll(cursor));
        Assert.False(cursor.MoveToSheet(1, Token));
    }

    [Fact]
    public void ReadsEveryMemberOfAConcatenatedFileAsOneCsv()
    {
        using GzipCursor cursor = Open([.. GzipFile.Of("id\n1\n"), .. GzipFile.Of("2\n3\n")]);

        string?[][] expected = [["id"], ["1"], ["2"], ["3"]];

        Assert.Equal(expected, ReadAll(cursor));
    }

    [Fact]
    public void ReportsProgressThroughTheCompressedFile()
    {
        using GzipCursor cursor = Open(GzipFile.Of(Lines(200_000)));

        for (int i = 0; i < 100_000; i++)
        {
            Assert.True(cursor.ReadRow(Token));
        }

        Assert.InRange(cursor.ReadFraction!.Value, 0.2, 0.8);

        ReadAll(cursor);
        Assert.Equal(1d, cursor.ReadFraction);
    }

    [Fact]
    public void ReadsACompressedWorkbookWithItsOwnSheetNames()
    {
        byte[] xlsx = Workbook(("Orders", InlineRow(1, "x")), ("Notes", InlineRow(1, "y") + InlineRow(2, "z")));
        using GzipCursor cursor = Open(GzipFile.Of(xlsx, "report.xlsx"), "upload.gz");
        using XlsxCursor plain = new(new MemoryStream(xlsx));

        Assert.Equal(["Orders", "Notes"], cursor.Sheets.Select(s => s.Name));
        Assert.All(cursor.Sheets, s => Assert.Equal(("report.xlsx", TabularFormat.Xlsx), (s.Source, s.Format)));
        Assert.Null(cursor.Dialect);

        for (int i = 0; i < 2; i++)
        {
            Assert.True(plain.MoveToSheet(i, Token));
            Assert.True(cursor.MoveToSheet(i, Token));
            Assert.Equal(ReadAll(plain), ReadAll(cursor));
        }
    }

    [Fact]
    public void ReadsACompressedOpenDocumentSpreadsheet()
    {
        using GzipCursor cursor = Open(GzipFile.Of(Spreadsheet("Tabelle1", "Köln")), "table.ods.gz");

        SheetInfo sheet = Assert.Single(cursor.Sheets);
        Assert.Equal(("Tabelle1", TabularFormat.Ods), (sheet.Name, sheet.Format));
        string?[][] expected = [["Köln"]];
        Assert.Equal(expected, ReadAll(cursor));
    }

    [Fact]
    public void RefusesACompressedZipArchive()
    {
        byte[] archive = new ZipArchiveBuilder().With("a.csv", "a\n1\n").Build();

        Assert.Equal(TabularFormatException.Unsupported, Assert.Throws<TabularFormatException>(() => Open(GzipFile.Of(archive))).Code);
    }

    [Fact]
    public void RefusesAFileCompressedTwice()
    {
        Assert.Equal(TabularFormatException.Unsupported, Assert.Throws<TabularFormatException>(() => Open(GzipFile.Of(GzipFile.Of("a\n")))).Code);
    }

    [Fact]
    public void RefusesACompressedLegacyWorkbookAsItWouldOnItsOwn()
    {
        byte[] ole2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[512]];

        TabularFormatException error = Assert.Throws<TabularFormatException>(() => Open(GzipFile.Of(ole2)));

        Assert.Equal(TabularFormatException.Unsupported, error.Code);
        Assert.Contains(".xls", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesACompressedXmlDocument()
    {
        Assert.Equal(TabularFormatException.Unsupported,
            Assert.Throws<TabularFormatException>(() => Open(GzipFile.Of("<?xml version=\"1.0\"?><office:document/>"))).Code);
    }

    [Fact]
    public void TreatsACompressedEmptyFileAsTheEmptyFileItself()
    {
        Exception? plain = Record.Exception(() => new CsvCursor(new MemoryStream([]), "data.csv").Dispose());
        Exception? compressed = Record.Exception(() => Open(GzipFile.Of([])).Dispose());

        Assert.Equal(plain?.GetType(), compressed?.GetType());
    }

    [Fact]
    public void RefusesAFileCutOffWhereverTheCutIs()
    {
        byte[] file = GzipFile.Of(Lines(2_000));

        foreach (int cut in new[] { 10, file.Length / 4, file.Length / 2, file.Length - 9, file.Length - 1 })
        {
            TabularFormatException error = Assert.Throws<TabularFormatException>(() =>
            {
                using GzipCursor cursor = Open(file[..cut]);
                ReadAll(cursor);
            });

            Assert.True(error.Code is TabularFormatException.Truncated or TabularFormatException.Corrupt, $"cut {cut}: {error.Code}");
        }
    }

    [Fact]
    public void RefusesACutPastTheDialectProbeWhileReading()
    {
        // Two megabytes of text: the probe reads the first 64 KB and opens fine; the cut is met by a row.
        byte[] file = GzipFile.Of(Lines(100_000));
        using GzipCursor cursor = Open(file[..(file.Length * 3 / 4)]);

        TabularFormatException error = Assert.Throws<TabularFormatException>(() => ReadAll(cursor));

        Assert.Equal(TabularFormatException.Truncated, error.Code);
    }

    [Fact]
    public void StopsAReadWhenTheTokenIsCancelled()
    {
        using GzipCursor cursor = Open(GzipFile.Of("a\n1\n"));

        Assert.ThrowsAny<OperationCanceledException>(() => cursor.ReadRow(new CancellationToken(canceled: true)));
    }
}
