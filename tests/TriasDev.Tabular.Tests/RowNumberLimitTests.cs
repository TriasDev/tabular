using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Row numbers are <see cref="int"/> on both sides. A cursor that counts its rows refuses the row
/// past <see cref="int.MaxValue"/> rather than wrapping to a negative number, and the csv writers
/// refuse to write a row the reader could not number — what is written reads back. Nobody writes
/// 2^31 rows in a test: the limit is injected, internally, at a small value.
/// </summary>
public sealed class RowNumberLimitTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ACsvCursorNumbersRowsUpToTheLargestInt()
    {
        using CsvCursor cursor = new(new MemoryStream("a\n1\n"u8.ToArray()), "t.csv", cancellationToken: Token);

        Assert.Equal(int.MaxValue, cursor.MaxRowNumber);
    }

    [Theory]
    [InlineData("a\n1\n2\n3\n4\n")]
    [InlineData("a\r1\r2\r3\r4")]            // the other way a record ends: at the end of the stream
    public void ACsvCursorRefusesTheRowPastItsLimitInsteadOfWrapping(string content)
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(content)), "t.csv", cancellationToken: Token) { MaxRowNumber = 3 };

        for (int row = 1; row <= 3; row++)
        {
            Assert.True(cursor.ReadRow(Token));
            Assert.Equal(row, cursor.CurrentRowNumber);
        }

        TabularLimitException refused = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token));
        Assert.Equal("MaxRows", refused.Limit);
        Assert.Equal(3, refused.Maximum);
    }

    [Fact]
    public void AnXlsxCursorRefusesAnUnnumberedRowPastItsLimitInsteadOfWrapping()
    {
        // Rows without an r attribute are counted, not read: the count is what could wrap.
        byte[] workbook = new XlsxPackage()
            .WithSheet("Sheet1", string.Concat(Enumerable.Repeat("<row><c><v>1</v></c></row>", 5)))
            .Build();
        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: Token) { MaxRowNumber = 3 };

        for (int row = 1; row <= 3; row++)
        {
            Assert.True(cursor.ReadRow(Token));
            Assert.Equal(row, cursor.CurrentRowNumber);
        }

        TabularLimitException refused = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token));
        Assert.Equal("MaxRows", refused.Limit);
        Assert.Equal(3, refused.Maximum);
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Zip)]
    public async Task TheCsvWritersRowLimitIsTheLargestRowNumberTheReaderGives(TabularFormat format)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), format);

        Assert.Equal(int.MaxValue, writer.MaxRows);
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Zip)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task TheRowPastTheLimitIsAWriteErrorNotAReaderBound(TabularFormat format)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), format);
        writer.MaxRows = 3;
        writer.BeginSheet("data", [new("a"), new("b")]);

        for (int row = 2; row <= 3; row++)
        {
            writer.BeginRow();
            writer.Write(row);
            writer.EndRow();
        }

        TabularWriteException refused = Assert.Throws<TabularWriteException>(writer.BeginRow);

        Assert.Equal(ErrorCodes.Write.TooManyRows, refused.Code);
        Assert.Equal("data", refused.SheetName);
        Assert.Equal(3, refused.RowNumber);        // the last row the sheet holds: the next one is refused
        Assert.Equal(0, refused.ColumnIndex);
        Assert.Equal("a", refused.Header);
        Assert.Throws<InvalidOperationException>(writer.BeginRow);    // faulted
    }

    [Fact]
    public async Task AnExportCountsItsRowsAsTheReaderDoes()
    {
        TabularExport<int> export = TabularExport.For<int>().Column("n", i => (long)i).Build();

        int rows = await export.WriteAsync(new WriteTarget(), TabularFormat.Csv, "data", Enumerable.Range(0, 7), cancellationToken: Token);

        Assert.Equal(7, rows);
    }
}
