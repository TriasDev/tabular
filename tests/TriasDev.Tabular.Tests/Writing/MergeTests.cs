using System.Text.RegularExpressions;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Merged cells: declared before their top-left cell, covered positions skipped and written by the writer.</summary>
public sealed class MergeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WriteColumn[] Four = [new("a"), new("b"), new("c"), new("d")];

    /// <summary>
    /// Row 2: a 1 × 2 title in a–b, then c, d. Rows 3–4: a 2 × 2 block in b–c (b3 top-left), a and d around it.
    /// </summary>
    internal static void Legend(TabularWriter writer)
    {
        writer.BeginSheet("legend", Four);
        writer.BeginRow();
        writer.Merge(1, 2);
        writer.Write("title");
        writer.Write("c2");
        writer.Write("d2");
        writer.EndRow();
        writer.BeginRow();
        writer.Write("a3");
        writer.Merge(2, 2);
        writer.Write("block");
        writer.Write("d3");
        writer.EndRow();
        writer.BeginRow();
        writer.Write("a4");
        writer.Write("d4");                 // b4 and c4 are covered: this lands in d
        writer.EndRow();
    }

    [Fact]
    public async Task CsvWritesTheValueInTheTopLeftCellAndEmptiesElsewhere()
    {
        byte[] csv = await SheetLayoutTests.Write(TabularFormat.Csv, Legend);
        List<RawCell[]> rows = SheetLayoutTests.Rows(csv);

        Assert.Equal(["title", "", "c2", "d2"], rows[1].Select(c => c.Text ?? string.Empty));
        Assert.Equal(["a3", "block", "", "d3"], rows[2].Select(c => c.Text ?? string.Empty));
        Assert.Equal(["a4", "", "", "d4"], rows[3].Select(c => c.Text ?? string.Empty));
    }

    /// <summary>
    /// Row 2: a2, an unstyled empty, then a 2 × 2 "wide" in c–d. Row 3 ends after a3: b3 is an empty and
    /// c3–d3 are covered, all written by EndRow.
    /// </summary>
    internal static void EndsEarly(TabularWriter writer)
    {
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Write("a2");
        writer.WriteEmpty();
        writer.Merge(2, 2);
        writer.Write("wide");
        writer.EndRow();
        writer.BeginRow();
        writer.Write("a3");
        writer.EndRow();
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task ARowThatEndsEarlyStillCoversItsMergedColumns(TabularFormat format)
    {
        byte[] file = await SheetLayoutTests.Write(format, EndsEarly);
        List<RawCell[]> rows = SheetLayoutTests.Rows(file);

        Assert.Equal(RawCell.FromText("wide"), rows[1][2]);
        Assert.Equal(RawCell.FromText("a3"), rows[2][0]);

        // A reader may drop a row's trailing empties; whatever it keeps after a3 is empty.
        Assert.All(rows[2].Skip(1), cell => Assert.True(cell.IsEmpty));

        if (format == TabularFormat.Xlsx)
        {
            Assert.Empty(OoxmlValidation.Errors(file));
        }

        if (format == TabularFormat.Ods)
        {
            string content = SheetLayoutTests.Entry(file, "content.xml");
            Assert.Contains("<text:p>a2</text:p></table:table-cell><table:table-cell/><table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"2\" office:value-type=\"string\"><text:p>wide</text:p></table:table-cell><table:covered-table-cell/>", content, StringComparison.Ordinal);
            Assert.Contains("<text:p>a3</text:p></table:table-cell><table:table-cell/><table:covered-table-cell/><table:covered-table-cell/></table:table-row>", content, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(-1, 2)]
    public async Task AnEmptyOrSingleCellRangeIsRefused(int rows, int columns)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Merge(rows, columns));
    }

    [Fact]
    public async Task ASecondMergeBeforeACellIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Merge(1, 2);

        Assert.Throws<InvalidOperationException>(() => writer.Merge(1, 2));
    }

    [Fact]
    public async Task AMergeNoCellFollowedIsRefusedAtTheEndOfTheRow()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Merge(1, 2);

        Assert.Throws<InvalidOperationException>(writer.EndRow);
    }

    [Fact]
    public async Task ARangePastTheLastColumnIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Write("a");
        writer.Write("b");
        writer.Merge(1, 3);

        Assert.Throws<InvalidOperationException>(() => writer.Write("c"));
    }

    [Fact]
    public async Task ARangeOverlappingAnotherIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Write("a2");
        writer.Write("b2");
        writer.Merge(3, 1);                 // c2–c4
        writer.Write("tall");
        writer.EndRow();
        writer.BeginRow();
        writer.Merge(1, 3);                 // a3–c3 would cover c3, which the tall range covers

        Assert.Throws<InvalidOperationException>(() => writer.Write("wide"));
    }

    [Fact]
    public async Task ASheetEndedWhileARangeStillCoversRowsIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Merge(3, 1);
        writer.Write("tall");
        writer.EndRow();

        Assert.Throws<InvalidOperationException>(() => writer.BeginSheet("next", Four));
    }

    [Fact]
    public async Task CompletingWhileARangeStillCoversRowsIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);
        writer.BeginRow();
        writer.Merge(2, 1);
        writer.Write("tall");
        writer.EndRow();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Token));
    }

    [Fact]
    public async Task AMergeOutsideARowIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", Four);

        Assert.Throws<InvalidOperationException>(() => writer.Merge(1, 2));
    }

    [Fact]
    public async Task AWriteIntoAFullyCoveredRowIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("a"), new("b")]);
        writer.BeginRow();
        writer.Merge(2, 2);
        writer.Write("all");
        writer.EndRow();
        writer.BeginRow();

        Assert.Throws<InvalidOperationException>(() => writer.Write("x"));
    }

    [Fact]
    public async Task XlsxListsTheRangesAfterTheData()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, Legend);
        string sheet = SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml");

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("</sheetData><mergeCells count=\"2\"><mergeCell ref=\"A2:B2\"/><mergeCell ref=\"B3:C4\"/></mergeCells></worksheet>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"C2\" t=\"inlineStr\"><is><t>c2</t></is></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"D4\" t=\"inlineStr\"><is><t>d4</t></is></c>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task XlsxPutsTheRangesAfterTheFilter()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            writer.BeginSheet("data", Four, new SheetOptions { AutoFilter = true });
            writer.BeginRow();
            writer.Merge(1, 2);
            writer.Write("x");
            writer.EndRow();
        });

        Assert.Empty(OoxmlValidation.Errors(xlsx));
        Assert.Contains("<autoFilter ref=\"A1:D2\"/><mergeCells count=\"1\"><mergeCell ref=\"A2:B2\"/></mergeCells>", SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    [InlineData(TabularFormat.Csv)]
    public async Task TheImportReadsAMergeAsItsValueAndEmpties(TabularFormat format)
    {
        byte[] file = await SheetLayoutTests.Write(format, Legend);
        List<RawCell[]> rows = SheetLayoutTests.Rows(file);

        Assert.Equal(RawCell.FromText("title"), rows[1][0]);
        Assert.True(rows[1][1].IsEmpty);
        Assert.Equal(RawCell.FromText("c2"), rows[1][2]);
        Assert.Equal(RawCell.FromText("block"), rows[2][1]);
        Assert.True(rows[3][1].IsEmpty);
        Assert.True(rows[3][2].IsEmpty);
        Assert.Equal(RawCell.FromText("d4"), rows[3][3]);
    }

    [Fact]
    public async Task XlsxWritesTheCoveredPositionsOfABorderedRangeInTheTopLeftStyle()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            StyleId boxed = writer.Style(new CellStyle { Border = CellBorder.Thin(CellColor.FromRgb(0x000000)) });
            writer.BeginSheet("data", Four);
            writer.BeginRow();
            writer.Write("a2");
            writer.Merge(2, 2);
            writer.Write("box", boxed);
            writer.Write("d2");
            writer.EndRow();
            writer.BeginRow();
            writer.Write("a3");
            writer.EndRow();
        });

        string sheet = SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml");
        Match top = Regex.Match(sheet, "<c r=\"B2\" s=\"(\\d+)\" t=\"inlineStr\">");

        Assert.True(top.Success);
        string s = top.Groups[1].Value;
        Assert.Contains($"<c r=\"C2\" s=\"{s}\"/>", sheet, StringComparison.Ordinal);
        Assert.Contains($"<c r=\"B3\" s=\"{s}\"/><c r=\"C3\" s=\"{s}\"/>", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));

        List<RawCell[]> rows = SheetLayoutTests.Rows(xlsx);
        Assert.Equal(RawCell.FromText("box"), rows[1][1]);
        Assert.True(rows[1][2].IsEmpty);
        Assert.True(rows[2][1].IsEmpty);
        Assert.True(rows[2][2].IsEmpty);
    }

    [Fact]
    public async Task XlsxLeavesTheCoveredPositionsOfAnUnstyledRangeOut()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            StyleId boxed = writer.Style(new CellStyle { Border = CellBorder.Thin(CellColor.FromRgb(0x000000)) });
            writer.BeginSheet("data", Four);
            writer.BeginRow();
            writer.Merge(2, 2);
            writer.Write("box", boxed);         // a2–b3
            writer.EndRow();
            writer.BeginRow();
            writer.EndRow();
            writer.BeginRow();
            writer.Merge(2, 2);                 // a4–b5, unstyled: the boxed range's style must not leak into it
            writer.Write("plain");
            writer.EndRow();
            writer.BeginRow();
            writer.Write("c5");
            writer.EndRow();
        });

        string sheet = SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml");
        Assert.Contains("<row r=\"5\"><c r=\"C5\" t=\"inlineStr\"><is><t>c5</t></is></c></row>", sheet, StringComparison.Ordinal);
        Assert.Empty(OoxmlValidation.Errors(xlsx));
    }

    [Fact]
    public async Task TheMergeLimitIsTheFormats()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("a"), new("b")]);

        for (int i = 0; i < 65_536; i++)
        {
            writer.BeginRow();
            writer.Merge(1, 2);
            writer.Write(i);
            writer.EndRow();
        }

        writer.BeginRow();
        Assert.Throws<TabularLimitException>(() => writer.Merge(1, 2));
    }

    [Fact]
    public async Task OdsSpansTheTopLeftCellAndCoversTheRest()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, Legend);
        string content = SheetLayoutTests.Entry(ods, "content.xml");

        Assert.Contains("<table:table-row><table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"1\" office:value-type=\"string\"><text:p>title</text:p></table:table-cell><table:covered-table-cell/>", content, StringComparison.Ordinal);
        Assert.Contains("<table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"2\" office:value-type=\"string\"><text:p>block</text:p></table:table-cell><table:covered-table-cell/>", content, StringComparison.Ordinal);
        Assert.Contains("<text:p>a4</text:p></table:table-cell><table:covered-table-cell/><table:covered-table-cell/><table:table-cell office:value-type=\"string\"><text:p>d4</text:p>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsSpansAStyledAndAnEmptyTopLeftCell()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, writer =>
        {
            StyleId fill = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0xF8696B) });
            writer.BeginSheet("data", Four);
            writer.BeginRow();
            writer.Merge(1, 2);
            writer.Write(5.5, fill);
            writer.Merge(1, 2);
            writer.WriteEmpty(fill);
            writer.EndRow();
        });

        string content = SheetLayoutTests.Entry(ods, "content.xml");
        Assert.Contains("<table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"1\" table:style-name=\"ts1\" office:value-type=\"float\" office:value=\"5.5\"/><table:covered-table-cell/>", content, StringComparison.Ordinal);
        Assert.Contains("<table:table-cell table:number-columns-spanned=\"2\" table:number-rows-spanned=\"1\" table:style-name=\"ts1\"/><table:covered-table-cell/>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OdsWritesARunOfEmptyCellsAsOne()
    {
        byte[] ods = await SheetLayoutTests.Write(TabularFormat.Ods, writer =>
        {
            writer.BeginSheet("data", [new("a"), new("b"), new("c"), new("d"), new("e")]);
            writer.BeginRow();
            writer.Write("a2");
            writer.WriteEmpty();
            writer.WriteEmpty();
            writer.WriteEmpty();
            writer.Write("e2");
            writer.EndRow();
            writer.BeginRow();
            writer.Write("a3");
            writer.EndRow();
        });

        string content = SheetLayoutTests.Entry(ods, "content.xml");
        Assert.Contains("<text:p>a2</text:p></table:table-cell><table:table-cell table:number-columns-repeated=\"3\"/><table:table-cell office:value-type=\"string\"><text:p>e2</text:p>", content, StringComparison.Ordinal);
        Assert.Contains("<text:p>a3</text:p></table:table-cell><table:table-cell table:number-columns-repeated=\"4\"/></table:table-row>", content, StringComparison.Ordinal);

        List<RawCell[]> rows = SheetLayoutTests.Rows(ods);
        Assert.Equal(RawCell.FromText("e2"), rows[1][4]);
        Assert.True(rows[1][2].IsEmpty);
    }
}
