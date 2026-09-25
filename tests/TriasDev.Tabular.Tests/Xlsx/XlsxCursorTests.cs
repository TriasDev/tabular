using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// Pins how the xlsx cursor presents a workbook: which sheets it finds, how it numbers rows, and
/// what it refuses.
/// </summary>
/// <remarks>
/// What a cell reads as is pinned by <see cref="GoldenFixtureTests"/>, which runs the same fixtures
/// that decided the parser choice.
/// </remarks>
public sealed class XlsxCursorTests
{
    private static byte[] TwoSheets() =>
        new XlsxPackage()
            .WithSheet("First", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>b</t></is></c></row>""")
            .WithSheet("Second", """<row r="1"><c r="A1" t="inlineStr"><is><t>c</t></is></c></row>""")
            .Build();

    [Fact]
    public void PresentsEverySheetInWorkbookOrderUnderItsOwnName()
    {
        using MemoryStream stream = new(TwoSheets(), writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.Equal(TabularFormat.Xlsx, cursor.Format);
        Assert.Equal(2, cursor.Sheets.Count);
        Assert.Equal("First", cursor.Sheets[0].Name);
        Assert.Equal("Second", cursor.Sheets[1].Name);
        Assert.Equal([0, 1], cursor.Sheets.Select(s => s.Index));
    }

    [Fact]
    public void ReadsTheSheetItWasMovedTo()
    {
        using MemoryStream stream = new(TwoSheets(), writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.MoveToSheet(1));
        Assert.Equal(1, cursor.CurrentSheetIndex);
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("c", cursor.CurrentRow[0].Text);
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesToMoveToASheetThatIsNotThere()
    {
        using MemoryStream stream = new(TwoSheets(), writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.False(cursor.MoveToSheet(2));
        Assert.False(cursor.MoveToSheet(-1));
    }

    [Fact]
    public void StartsOnTheFirstSheetWithoutBeingAsked()
    {
        using MemoryStream stream = new(TwoSheets(), writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.Equal(0, cursor.CurrentSheetIndex);
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("a", cursor.CurrentRow[0].Text);
    }

    [Fact]
    public void NumbersRowsBySheetPositionRatherThanByHowManyWereRead()
    {
        // Rows 1 and 4 exist; 2 and 3 do not. A reader that counted its own reads would call the
        // second row 2, and every message about it would point a user at the wrong line.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row><row r="4"><c r="A4" t="inlineStr"><is><t>d</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        List<int> numbers = [];

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            numbers.Add(cursor.CurrentRowNumber);
        }

        Assert.Equal([1, 4], numbers);
    }

    [Fact]
    public void RewindsToTheStartOfASheetItIsMovedToAgain()
    {
        using MemoryStream stream = new(TwoSheets(), writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.MoveToSheet(0));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("a", cursor.CurrentRow[0].Text);
    }

    [Fact]
    public void RefusesAPackageThatExpandsBeyondItsBudget()
    {
        // A megabyte of one repeated byte compresses to almost nothing, which is the whole trick: a
        // small upload that becomes a large allocation.
        byte[] bomb = BuildOversizedPackage();

        using MemoryStream stream = new(bomb, writable: false);

        XlsxCursorOptions options = new() { MaxUncompressedBytes = 64 * 1024 };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new XlsxCursor(stream, options));

        Assert.Contains("65536", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesSomethingThatIsNotAWorkbook()
    {
        using MemoryStream stream = new("not a zip at all"u8.ToArray(), writable: false);

        Assert.Throws<InvalidDataException>(() => new XlsxCursor(stream));
    }

    [Fact]
    public void RefusesAZipThatHoldsNoWorkbook()
    {
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry("readme.txt");
            using Stream content = entry.Open();
            content.Write("nothing to see"u8);
        }

        using MemoryStream stream = new(buffer.ToArray(), writable: false);

        Assert.Throws<InvalidDataException>(() => new XlsxCursor(stream));
    }

    [Fact]
    public void ReadsASheetLargerThanItsBuffer()
    {
        // The scanner reads in 64 KB blocks, so a sheet bigger than one block exercises the path
        // where the buffer is compacted and refilled mid-scan. Small fixtures never reach it, which
        // is how a defect there survived every one of them and appeared only on a real file.
        StringBuilder rows = new();

        for (int i = 1; i <= 5_000; i++)
        {
            rows.Append("<row r=\"").Append(i).Append("\"><c r=\"A").Append(i)
                .Append("\" t=\"inlineStr\"><is><t>value-").Append(i).Append("</t></is></c><c r=\"B").Append(i)
                .Append("\"><v>").Append(i).Append("</v></c></row>");
        }

        byte[] content = new XlsxPackage().WithSheet("Sheet1", rows.ToString()).Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        int read = 0;

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            read++;

            Assert.Equal($"value-{read}", cursor.CurrentRow[0].Text);
            Assert.Equal(read, cursor.CurrentRow[1].Number);
            Assert.Equal(read, cursor.CurrentRowNumber);
        }

        Assert.Equal(5_000, read);
    }

    [Fact]
    public void ReadsACellLongerThanTheWholeBuffer()
    {
        // One value that cannot fit in a block at all, so the buffer has to grow rather than compact.
        string long_ = new('x', 200_000);

        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", $"""<row r="1"><c r="A1" t="inlineStr"><is><t>{long_}</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(long_, cursor.CurrentRow[0].Text);
    }

    [Fact]
    public void ResolvesEntitiesInCellText()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a &amp; b &lt;c&gt; &quot;d&quot; &#65;</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("a & b <c> \"d\" A", cursor.CurrentRow[0].Text);
    }

    [Fact]
    public void IsRecognisedByItsBytesRatherThanItsName()
    {
        using MemoryStream stream = new(TwoSheets(), writable: false);

        Assert.Equal(TabularFormat.Xlsx, TabularFile.Detect(stream));

        // And detection leaves the stream where it found it, so the cursor can still read it.
        using ITabularCursor cursor = TabularFile.Open(stream, "anything.csv", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TabularFormat.Xlsx, cursor.Format);
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    private static byte[] BuildOversizedPackage()
    {
        StringBuilder rows = new();

        for (int i = 1; i <= 20_000; i++)
        {
            rows.Append("<row r=\"").Append(i).Append("\"><c r=\"A").Append(i)
                .Append("\" t=\"inlineStr\"><is><t>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</t></is></c></row>");
        }

        return new XlsxPackage().WithSheet("Sheet1", rows.ToString()).Build();
    }
}
