using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Moving to a sheet can be stopped — it reads forward through an ods part, or copies a workbook out
/// of an archive — and a move stopped half-way never leaves a cursor that reads the wrong rows.
/// </summary>
public sealed class MoveToSheetCancellationTests
{
    private static CancellationToken Cancelled => new(canceled: true);

    private static string Row(string text) =>
        $"<table:table-row><table:table-cell office:value-type=\"string\"><text:p>{text}</text:p></table:table-cell></table:table-row>";

    private static byte[] TwoSheetOds(int firstSheetRows) =>
        new OdsPackage()
            .WithTable("Big", string.Concat(Enumerable.Range(0, firstSheetRows).Select(i => Row($"b{i}"))))
            .WithTable("Small", Row("s"))
            .Build();

    public static TheoryData<string> Formats => new() { "csv", "xlsx", "ods", "zip", "gzip", "tar", "targz" };

    private static ITabularCursor Open(string format) => format switch
    {
        "csv" => new CsvCursor(new MemoryStream(Encoding.UTF8.GetBytes("a\n1\n")), "t.csv"),
        "xlsx" => new XlsxCursor(new MemoryStream(new XlsxPackage().WithSheet("S", """<row r="1"><c t="inlineStr"><is><t>a</t></is></c></row>""").Build())),
        "ods" => new OdsCursor(new MemoryStream(TwoSheetOds(1))),
        "gzip" => new GzipCursor(new MemoryStream(GzipFile.Of("a\n1\n")), "a.csv.gz"),
        "tar" => new ArchiveCursor(new MemoryStream(TarArchive.Of(System.Formats.Tar.TarEntryFormat.Pax, ("a.csv", "a\n1\n")))),
        "targz" => new ArchiveCursor(new MemoryStream(GzipFile.Of(TarArchive.Of(System.Formats.Tar.TarEntryFormat.Pax, ("a.csv", "a\n1\n"))))),
        _ => new ArchiveCursor(new MemoryStream(new ZipArchiveBuilder().With("a.csv", "a\n1\n").Build())),
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void StopsAMoveWhenTheTokenIsCancelled(string format)
    {
        using ITabularCursor cursor = Open(format);

        Assert.ThrowsAny<OperationCanceledException>(() => cursor.MoveToSheet(0, Cancelled));
    }

    [Fact]
    public void RefusesToReadAfterAnOdsMoveStoppedHalfWayAndMovesAgainCleanly()
    {
        // The token is cancelled while the scan is inside the first sheet on its way to the second:
        // the scanner stands in rows that belong to neither the sheet it left nor the one it sought.
        byte[] package = TwoSheetOds(20_000);
        using CancellationTokenSource cancel = new();
        CancellingStream stream = new(package, cancel);
        using OdsCursor cursor = new(stream);

        // Counted from here: opening read the whole part for the sheet names already.
        stream.CancelAfter(package.Length / 4);

        Assert.ThrowsAny<OperationCanceledException>(() => cursor.MoveToSheet(1, cancel.Token));
        Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("s", cursor.CurrentRow[0].AsText());
    }

    [Fact]
    public void RefusesToReadAfterAnArchiveMoveStoppedWhileCopyingAWorkbook()
    {
        StringBuilder rows = new();

        for (int r = 1; r <= 3000; r++)
        {
            rows.Append($"<row r=\"{r}\"><c t=\"inlineStr\"><is><t>value {r} of a row</t></is></c></row>");
        }

        byte[] archive = new ZipArchiveBuilder()
            .With("a.csv", "h\n1\n")
            .With("b.xlsx", new XlsxPackage().WithSheet("S", rows.ToString()).Build(), System.IO.Compression.CompressionLevel.NoCompression)
            .Build();
        using CancellationTokenSource cancel = new();
        CancellingStream stream = new(archive, cancel);
        using ArchiveCursor cursor = new(stream);

        stream.CancelAfter(archive.Length / 3);

        Assert.ThrowsAny<OperationCanceledException>(() => cursor.MoveToSheet(1, cancel.Token));
        Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.True(cursor.MoveToSheet(0, TestContext.Current.CancellationToken));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("h", cursor.CurrentRow[0].AsText());   // a cursor hands out the header row too
    }

    [Fact]
    public void StopsAnAnalysisBeforeItReadsWhenItsTokenIsCancelled()
    {
        using ITabularCursor cursor = Open("ods");

        Assert.ThrowsAny<OperationCanceledException>(() => TabularAnalyzer.Analyze(cursor, cancellationToken: Cancelled));
    }
}
