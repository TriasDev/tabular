using System.IO.Compression;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>A move stopped or failed part-way never reads the wrong sheet, and the next move reads the right one.</summary>
public sealed class MoveToSheetRetryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Row(string text) =>
        $"<table:table-row><table:table-cell office:value-type=\"string\"><text:p>{text}</text:p></table:table-cell></table:table-row>";

    private static byte[] Without(byte[] zip, string entryName)
    {
        ZipArchiveBuilder builder = new();

        using ZipArchive source = new(new MemoryStream(zip), ZipArchiveMode.Read);

        foreach (ZipArchiveEntry entry in source.Entries.Where(e => e.FullName != entryName))
        {
            using Stream content = entry.Open();
            using MemoryStream copy = new();
            content.CopyTo(copy);
            builder.With(entry.FullName, copy.ToArray());
        }

        return builder.Build();
    }

    [Fact]
    public void MovesAgainCleanlyAfterAnOdsMoveWasStoppedAtAnyNode()
    {
        // A move stopped by the token used to lose the node it had just read; when that node opened or
        // closed a table, the next move counted the tables wrongly and called a good file corrupt. The
        // padding walks the stop — the scan's first checkpoint, 4096 nodes in — across the nodes around
        // the second table's start.
        List<int> stopped = [];

        for (int padding = 4075; padding <= 4095; padding++)
        {
            string columns = string.Concat(Enumerable.Repeat("<table:table-column/>", padding));
            byte[] package = new OdsPackage()
                .WithRawTable($"""<table:table table:name="A">{columns}{Row("a")}</table:table>""")
                .WithTable("B", Row("b"))
                .WithTable("C", Row("c"))
                .Build();

            using CancellationTokenSource cancel = new();
            CancellingStream stream = new(package, cancel);
            using OdsCursor cursor = new(stream);
            Assert.True(cursor.MoveToSheet(2, Token));

            // Moving back reopens the part from the top; its first bytes cancel the token, after the
            // move's entry check and before the scan's first checkpoint.
            stream.CancelAfter(1);
            bool reached;

            try
            {
                reached = cursor.MoveToSheet(1, cancel.Token);   // B lies before the first checkpoint
            }
            catch (OperationCanceledException)
            {
                reached = false;
                stopped.Add(padding);

                InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(Token));
                Assert.Contains("move", refused.Message, StringComparison.OrdinalIgnoreCase);
            }

            if (!reached)
            {
                Assert.True(cursor.MoveToSheet(1, Token), $"padding {padding}");
            }

            Assert.True(cursor.ReadRow(Token));
            Assert.Equal("b", cursor.CurrentRow[0].AsText());
        }

        // The checkpoint landed on the second table's start at 4083 when this was found.
        Assert.Contains(4083, stopped);
    }

    [Fact]
    public void RefusesToReadAfterAMoveInsideAWorkbookOfAnArchiveFailed()
    {
        // The workbook declares two sheets and holds the part of the first only. Moving to the second
        // opened the workbook and then failed inside it; the archive went on reading the workbook's
        // first sheet under the index of the sheet it had left.
        byte[] workbook = Without(
            new XlsxPackage()
                .WithSheet("One", """<row r="1"><c t="inlineStr"><is><t>one</t></is></c></row>""")
                .WithSheet("Two", """<row r="1"><c t="inlineStr"><is><t>two</t></is></c></row>""")
                .Build(),
            "xl/worksheets/sheet2.xml");

        using ArchiveCursor cursor = new(new MemoryStream(new ZipArchiveBuilder().With("a.csv", "h\n1\n").With("b.xlsx", workbook).Build()), null, Token);
        Assert.Equal(["a", "One", "Two"], cursor.Sheets.Select(s => s.Name));

        Assert.Throws<TabularFormatException>(() => cursor.MoveToSheet(2, Token));
        Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(Token));
    }

    [Fact]
    public void SaysAMoveWasStoppedWhenRefusingToReadAfterIt()
    {
        byte[] package = new OdsPackage().WithTable("A", Row("a")).WithTable("B", Row("b")).Build();
        using OdsCursor cursor = new(new MemoryStream(package));

        Assert.ThrowsAny<OperationCanceledException>(() => cursor.MoveToSheet(1, new CancellationToken(canceled: true)));

        // Stopped at its entry, the move changed nothing, and the cursor reads on where it was.
        Assert.True(cursor.ReadRow(Token));
        Assert.Equal("a", cursor.CurrentRow[0].AsText());
    }
}
