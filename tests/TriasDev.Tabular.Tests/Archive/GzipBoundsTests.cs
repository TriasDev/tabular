using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A gzip file is held to the archive bounds: what it expands to, and the workbook inside it.</summary>
public sealed class GzipBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TabularOpenOptions Bounds(long uncompressed = 8L << 30, long workbook = 256L << 20) =>
        new() { Archive = new ArchiveCursorOptions { MaxUncompressedBytes = uncompressed, MaxEmbeddedWorkbookBytes = workbook } };

    private static void ReadAll(ITabularCursor cursor)
    {
        for (int s = 0; s < cursor.Sheets.Count && cursor.MoveToSheet(s, Token); s++)
        {
            while (cursor.ReadRow(Token))
            {
                // Only the reading matters: the bound is met on the way.
            }
        }
    }

    [Fact]
    public void StopsABombLongBeforeItIsExpanded()
    {
        // Sixteen megabytes of one short line compress a thousandfold.
        byte[] bomb = GzipFile.Of(string.Concat(Enumerable.Repeat("a;b\n", 4 << 20)));

        TabularLimitException error = Assert.Throws<TabularLimitException>(() =>
        {
            using GzipCursor cursor = new(new MemoryStream(bomb), "bomb.csv.gz", Bounds(uncompressed: 1 << 20), Token);
            ReadAll(cursor);
        });

        Assert.Equal(nameof(ArchiveCursorOptions.MaxUncompressedBytes), error.Limit);
    }

    [Fact]
    public void ReadsAFileThatExpandsToExactlyTheBound()
    {
        const string Csv = "a;b\n1;2\n";
        using GzipCursor cursor = new(new MemoryStream(GzipFile.Of(Csv)), "x.csv.gz", Bounds(uncompressed: Csv.Length), Token);

        ReadAll(cursor);
    }

    [Fact]
    public void RefusesAWorkbookLargerThanItsBudgetAndClosesTheStream()
    {
        byte[] xlsx = new XlsxPackage().WithSheet("S", """<row r="1"><c t="inlineStr"><is><t>x</t></is></c></row>""").Build();
        TrackedStream stream = new(GzipFile.Of(xlsx));

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => new GzipCursor(stream, "r.xlsx.gz", Bounds(workbook: 200), Token));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes), error.Limit);
        Assert.True(stream.IsDisposed);
    }
}
