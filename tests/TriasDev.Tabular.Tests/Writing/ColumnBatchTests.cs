using System.Diagnostics.CodeAnalysis;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Writing data given by column: typed vectors in, the same file as row by row out.</summary>
public sealed class ColumnBatchTests
{
    private static readonly CellStyle Low = new() { Fill = CellColor.FromRgb(0x63BE7B) };
    private static readonly CellStyle High = new() { Fill = CellColor.FromRgb(0xF8696B) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly WriteColumn[] Columns = [new("Id"), new("Name"), new("Score"), new("Amount"), new("Start"), new("Active")];

    private sealed record Wrapper(double? Value);

    private static ColumnBatch Batch()
    {
        ColumnBatch batch = new();
        batch.Reset(3);
        batch.Add(new long[] { 1, 2, 3 });
        batch.Add(new List<string?> { "a", null, "c" });
        batch.Add(new Wrapper[] { new(1.5), new(null), new(9.5) }, w => w.Value);
        batch.Add((IReadOnlyList<decimal>)new[] { 1.25m, 2m, 3.5m }.AsReadOnly());
        batch.Add(new DateOnly?[] { new DateOnly(2026, 1, 1), null, new DateOnly(2026, 3, 1) });
        batch.Add(new[] { true, false, true });
        return batch;
    }

    private static void RowByRow(TabularWriter writer)
    {
        object?[][] rows =
        [
            [1L, "a", 1.5, 1.25m, new DateOnly(2026, 1, 1), true],
            [2L, null, null, 2m, null, false],
            [3L, "c", 9.5, 3.5m, new DateOnly(2026, 3, 1), true],
        ];

        foreach (object?[] row in rows)
        {
            writer.BeginRow();
            writer.Write((long)row[0]!);
            writer.Write((string?)row[1]);
            if (row[2] is double d)
            { writer.Write(d); }
            else
            { writer.WriteEmpty(); }
            writer.Write((decimal)row[3]!);
            if (row[4] is DateOnly day)
            { writer.Write(day); }
            else
            { writer.WriteEmpty(); }
            writer.Write((bool)row[5]!);
            writer.EndRow();
        }
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task ABatchWritesTheSameFileAsRows(TabularFormat format)
    {
        byte[] batched = await SheetLayoutTests.Write(format, writer => { writer.BeginSheet("data", Columns); writer.WriteBatch(Batch()); });
        byte[] rows = await SheetLayoutTests.Write(format, writer => { writer.BeginSheet("data", Columns); RowByRow(writer); });

        Assert.Equal(rows, batched);
    }

    [Fact]
    public async Task StylesComeFromAConstantAVectorOrARule()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            StyleId low = writer.Style(Low);
            StyleId high = writer.Style(High);
            writer.BeginSheet("data", [new("constant"), new("vector"), new("rule")]);
            ColumnBatch batch = new();
            batch.Reset(2);
            batch.Add(new[] { 1.0, 2.0 }, low);
            batch.Add(new[] { 1.0, 2.0 }, new[] { low, high });
            batch.Add(new[] { 1.0, 9.0 }, v => v > 5 ? High : null);
            writer.WriteBatch(batch);
        });

        string sheet = SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml");
        Assert.Contains("<c r=\"A2\" s=\"4\"><v>1</v></c><c r=\"B2\" s=\"4\"><v>1</v></c><c r=\"C2\"><v>1</v></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"A3\" s=\"4\"><v>2</v></c><c r=\"B3\" s=\"5\"><v>2</v></c><c r=\"C3\" s=\"5\"><v>9</v></c>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void AVectorOfTheWrongLengthIsRefusedWhenAdded()
    {
        ColumnBatch batch = new();
        batch.Reset(3);
        batch.Add(new long[] { 1, 2, 3 });

        ArgumentException refused = Assert.Throws<ArgumentException>(() => batch.Add(new long[] { 1, 2 }));
        Assert.Contains("column 2", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => batch.Add(new long[] { 1, 2, 3 }, new StyleId[2]));
    }

    [Fact]
    public void AnUnsupportedTypeIsRefusedWhenAdded()
    {
        ColumnBatch batch = new();
        batch.Reset(1);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => batch.Add(new[] { Guid.Empty }));
        Assert.Contains("Guid", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABatchWithTheWrongNumberOfColumnsIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("a"), new("b")]);
        ColumnBatch batch = new();
        batch.Reset(1);
        batch.Add(new[] { 1L });

        Assert.Throws<ArgumentException>(() => writer.WriteBatch(batch));
        await AssertFaulted(writer);
    }

    [Fact]
    public async Task ABatchOutsideASheetOrInsideARowIsRefused()
    {
        ColumnBatch batch = new();
        batch.Reset(0);

        await using (TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv))
        {
            Assert.Throws<InvalidOperationException>(() => writer.WriteBatch(batch));
            await AssertFaulted(writer);
        }

        await using (TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv))
        {
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            Assert.Throws<InvalidOperationException>(() => writer.WriteBatch(batch));
            await AssertFaulted(writer);
        }
    }

    [Fact]
    public async Task AValueTheFormatRefusesIsReportedLikeARowWrite()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("Id"), new("Score")]);
        ColumnBatch batch = new();
        batch.Reset(2);
        batch.Add(new[] { 1L, 2L });
        batch.Add(new[] { 1.5, 0.1 + 0.2 });                // 0.30000000000000004: 17 significant digits

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => writer.WriteBatch(batch));
        Assert.Equal(3, refused.RowNumber);
        Assert.Equal(1, refused.ColumnIndex);
        Assert.Equal("Score", refused.Header);
        Assert.Throws<InvalidOperationException>(() => writer.WriteBatch(batch));      // faulted
    }

    [Fact]
    public async Task ReusedSourceListsWriteWhatTheyHeldWhenWritten()
    {
        byte[] csv = await SheetLayoutTests.Write(TabularFormat.Csv, writer =>
        {
            writer.BeginSheet("data", [new("n")]);
            ColumnBatch batch = new();
            List<long> values = [];

            for (int chunk = 0; chunk < 3; chunk++)
            {
                values.Clear();
                values.AddRange([chunk * 10, (chunk * 10) + 1]);
                batch.Reset(values.Count);
                batch.Add(values);
                writer.WriteBatch(batch);
            }
        });

        Assert.Equal(["n", "0", "1", "10", "11", "20", "21"], SheetLayoutTests.Rows(csv).Select(r => r[0].Text));
    }

    [Fact]
    [SuppressMessage("Sonar", "S1215", Justification = "The test is about what survives a collection.")]
    public void ResetDropsTheReferencesItHeld()
    {
        ColumnBatch batch = new();
        WeakReference held = Hold(batch);
        batch.Reset(0);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(held.IsAlive);

        static WeakReference Hold(ColumnBatch batch)
        {
            long[] values = new long[1_000];
            batch.Reset(values.Length);
            batch.Add(values);
            return new WeakReference(values);
        }
    }

    [Fact]
    public async Task WriteBatchAsyncFlushesInsideALargeBatch()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv))
        {
            writer.BeginSheet("data", [new("text")]);
            string[] values = [.. Enumerable.Range(0, 100_000).Select(i => $"value number {i}")];
            ColumnBatch batch = new();
            batch.Reset(values.Length);
            batch.Add(values);
            await writer.WriteBatchAsync(batch, Token);
            Assert.True(target.AsyncWrites > 1, $"{target.AsyncWrites} writes reached the target during the batch");
            await writer.CompleteAsync(Token);
        }
    }

    [Fact]
    public async Task WriteBatchAsyncHonoursCancellation()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("n")]);
        ColumnBatch batch = new();
        batch.Reset(10);
        batch.Add(new long[10]);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.WriteBatchAsync(batch, cancelled.Token));
        await AssertFaulted(writer);
    }

    /// <summary>A faulted writer refuses everything, completing included.</summary>
    private static async Task AssertFaulted(TabularWriter writer)
    {
        Assert.Contains("failed earlier", Assert.Throws<InvalidOperationException>(() => writer.BeginRow()).Message, StringComparison.Ordinal);
        InvalidOperationException completing = await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Token));
        Assert.Contains("failed earlier", completing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelectorThatThrowsFaultsTheWriter()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("n")]);
        ColumnBatch batch = new();
        batch.Reset(3);
        batch.Add(new[] { 1, 2, 3 }, i => i == 2 ? throw new NotSupportedException("boom") : (long)i);

        Assert.Throws<NotSupportedException>(() => writer.WriteBatch(batch));
        await AssertFaulted(writer);
    }

    [Fact]
    public async Task AStyleRuleThatThrowsFaultsTheWriterInTheAsyncWriteToo()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("n")]);
        ColumnBatch batch = new();
        batch.Reset(3);
        batch.Add(new long[] { 1, 2, 3 }, v => v == 2 ? throw new NotSupportedException("boom") : Low);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await writer.WriteBatchAsync(batch, Token));
        await AssertFaulted(writer);
    }

    [Fact]
    public async Task AListThatShrankAfterAddFaultsTheWriter()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("n")]);
        List<long> values = [1, 2, 3];
        ColumnBatch batch = new();
        batch.Reset(3);
        batch.Add(values);
        values.Clear();

        Assert.ThrowsAny<ArgumentException>(() => writer.WriteBatch(batch));
        await AssertFaulted(writer);
    }

    [Fact]
    public async Task ABatchOverRowsAMergeStillCoversIsRefusedUpFront()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("a"), new("b")]);
        writer.BeginRow();
        writer.Merge(2, 1);
        writer.Write("x");
        writer.Write("y");
        writer.EndRow();
        ColumnBatch batch = new();
        batch.Reset(1);
        batch.Add(new[] { 1L });
        batch.Add(new[] { 2L });

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => writer.WriteBatch(batch));
        Assert.Contains("still covers rows below", refused.Message, StringComparison.Ordinal);
        await AssertFaulted(writer);
    }
}
