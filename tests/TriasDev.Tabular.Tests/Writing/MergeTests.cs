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

    [Fact]
    public async Task ARowThatEndsEarlyStillCoversItsMergedColumns()
    {
        byte[] csv = await SheetLayoutTests.Write(TabularFormat.Csv, writer =>
        {
            writer.BeginSheet("data", Four);
            writer.BeginRow();
            writer.Write("a2");
            writer.Merge(2, 3);
            writer.Write("wide");
            writer.EndRow();
            writer.BeginRow();
            writer.Write("a3");
            writer.EndRow();                // b3–d3 covered, written by EndRow
        });

        Assert.Equal(["a3", "", "", ""], SheetLayoutTests.Rows(csv)[2].Select(c => c.Text ?? string.Empty));
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
}
