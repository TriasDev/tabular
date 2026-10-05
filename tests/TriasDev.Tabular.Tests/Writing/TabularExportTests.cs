using System.Runtime.CompilerServices;
using System.Text;

using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Writing an export: every kind of source, one sheet or a whole file, flushed as it goes.</summary>
public sealed class TabularExportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Item(long Id, string Name, decimal Amount);

    private static readonly TabularExport<Item> Export = TabularExport.For<Item>()
        .Column("Id", i => i.Id)
        .Column("Name", i => i.Name)
        .Column("Amount", i => i.Amount)
        .Build();

    private static readonly TabularWriterOptions NoBom = new() { Csv = new CsvWriterOptions { ByteOrderMark = false } };

    private static Item[] Items(int count) => [.. Enumerable.Range(1, count).Select(i => new Item(i, $"item {i}", i / 4m))];

    private static async IAsyncEnumerable<IReadOnlyList<Item>> Chunks(Item[] items, int size, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int at = 0; at < items.Length; at += size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return items[at..Math.Min(items.Length, at + size)];
        }
    }

    private static async IAsyncEnumerable<Item> OneByOne(Item[] items)
    {
        foreach (Item item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static string Csv(WriteTarget target) => Encoding.UTF8.GetString(target.ToArray());

    [Fact]
    public async Task WritesAHeaderAndARowPerItem()
    {
        WriteTarget target = new();

        long rows = await Export.WriteAsync(target, TabularFormat.Csv, "data", Items(2), NoBom, Token);

        Assert.Equal(2, rows);
        Assert.Equal("Id,Name,Amount\r\n1,item 1,0.25\r\n2,item 2,0.5\r\n", Csv(target));
        Assert.True(target.IsDisposed);
        Assert.False(target.DisposedSynchronously);
    }

    [Fact]
    public async Task EveryKindOfSourceWritesTheSameFile()
    {
        Item[] items = Items(5_000);

        WriteTarget chunked = new();
        WriteTarget streamed = new();
        WriteTarget listed = new();
        WriteTarget selected = new();

        Assert.Equal(5_000, await Export.WriteAsync(chunked, TabularFormat.Csv, "data", Chunks(items, 700, Token), NoBom, Token));
        Assert.Equal(5_000, await Export.WriteAsync(streamed, TabularFormat.Csv, "data", OneByOne(items), NoBom, Token));
        Assert.Equal(5_000, await Export.WriteAsync(listed, TabularFormat.Csv, "data", items, NoBom, Token));
        Assert.Equal(5_000, await Export.WriteAsync(selected, TabularFormat.Csv, "data", Chunks(items, 700, Token), chunk => chunk, NoBom, Token));

        string expected = Csv(listed);
        Assert.Equal(expected, Csv(chunked));
        Assert.Equal(expected, Csv(streamed));
        Assert.Equal(expected, Csv(selected));
    }

    [Fact]
    public async Task FlushesAfterEveryChunk()
    {
        WriteTarget target = new();

        await Export.WriteAsync(target, TabularFormat.Csv, "data", Chunks(Items(1_000), 100, Token), NoBom, Token);

        // Ten chunks, each flushed as it is written, and the file's completion.
        Assert.True(target.AsyncWrites >= 10, $"{target.AsyncWrites} writes reached the target");
    }

    [Fact]
    public async Task FlushesInsideALargeChunkWhenTheWriterRecommendsIt()
    {
        WriteTarget target = new();
        long reachedTheTarget = -1;
        TabularExport<Item> export = TabularExport.For<Item>()
            .Column("Id", i =>
            {
                if (i.Id == 40_000)
                {
                    reachedTheTarget = target.Length;
                }

                return i.Id;
            })
            .Column("Name", i => i.Name)
            .Build();
        Item[] items = [.. Enumerable.Range(1, 60_000).Select(i => new Item(i, new string('x', 40), i))];

        await export.WriteAsync(target, TabularFormat.Csv, "data", Chunks(items, items.Length, Token), NoBom, Token);

        // The target receives bytes only on a flush: some arrived before the chunk ended.
        Assert.True(reachedTheTarget > 0, $"{reachedTheTarget} bytes had reached the target by row 40,000 of one chunk");
    }

    [Fact]
    public async Task AFailureInsideASheetLeavesTheCallersWriterUnusable()
    {
        TabularExport<Item> export = TabularExport.For<Item>()
            .Column("Id", i => i.Id > 4 ? throw new InvalidOperationException("boom") : i.Id)
            .Build();
        WriteTarget target = new();

        await using TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await export.WriteSheetAsync(writer, "data", Items(10), Token));

        Assert.Throws<InvalidOperationException>(writer.EndRow);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Token));
    }

    [Fact]
    public async Task WritesSeveralSheetsIntoOneWorkbook()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            Assert.Equal(3, await Export.WriteSheetAsync(writer, "First", Items(3), Token));
            Assert.Equal(2, await Export.WriteSheetAsync(writer, "Second", Chunks(Items(2), 1, Token), Token));
            await writer.CompleteAsync(Token);
        }

        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(target.ToArray(), writable: false), "t.xlsx", cancellationToken: Token);
        Assert.Equal(["First", "Second"], cursor.Sheets.Select(s => s.Name));
    }

    [Fact]
    public async Task AValueTheFormatRefusesClosesTheStreamAndLeavesNoValidFile()
    {
        TabularExport<Item> export = TabularExport.For<Item>().Column("Id", i => i.Id).Column("Amount", i => i.Amount).Build();
        Item[] items = [.. Items(50_000)];
        items[39_999] = items[39_999] with { Amount = 1_234_567_890_123.456m };
        WriteTarget target = new();

        TabularWriteException refused = await Assert.ThrowsAsync<TabularWriteException>(async () =>
            await export.WriteAsync(target, TabularFormat.Xlsx, "data", Chunks(items, 10_000, Token), cancellationToken: Token));

        Assert.Equal(ErrorCodes.Write.PrecisionLoss, refused.Code);
        Assert.Equal(40_001, refused.RowNumber);
        Assert.Equal("Amount", refused.Header);
        Assert.True(target.IsDisposed);
        Assert.False(target.DisposedSynchronously);
        Assert.ThrowsAny<TabularException>(() => new TriasDev.Tabular.Xlsx.XlsxCursor(new MemoryStream(target.ToArray(), writable: false), cancellationToken: Token));
    }

    [Fact]
    public async Task CancellationStopsBetweenChunksAndClosesTheStream()
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        WriteTarget target = new();
        int chunksSeen = 0;

        bool tokenFlowed = false;

        // Deliberately called without a token: it can only arrive through the export's WithCancellation.
        async IAsyncEnumerable<IReadOnlyList<Item>> Source([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            tokenFlowed = cancellationToken == cancel.Token;

            await foreach (IReadOnlyList<Item> chunk in Chunks(Items(1_000), 100, CancellationToken.None))
            {
                if (++chunksSeen == 3)
                {
                    await cancel.CancelAsync();
                }

                yield return chunk;
            }
        }

#pragma warning disable xUnit1051, S8949 // The source is called without a token on purpose: the export must flow its own through WithCancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Export.WriteAsync(target, TabularFormat.Csv, "data", Source(), NoBom, cancel.Token));
#pragma warning restore xUnit1051, S8949

        Assert.True(tokenFlowed, "the export did not enumerate the source WithCancellation");
        Assert.True(chunksSeen < 10);
        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task LeavesTheStreamOpenWhenAsked()
    {
        WriteTarget target = new();

        await Export.WriteAsync(target, TabularFormat.Csv, "data", Items(1), new TabularWriterOptions { LeaveOpen = true }, Token);

        Assert.False(target.IsDisposed);
    }

    [Fact]
    public async Task RefusesANullSourceAndStillClosesTheStream()
    {
        WriteTarget target = new();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await Export.WriteAsync(target, TabularFormat.Csv, "data", (IEnumerable<Item>)null!, cancellationToken: Token));

        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task RefusesANullChunk()
    {
        static async IAsyncEnumerable<IReadOnlyList<Item>> WithANullChunk()
        {
            await Task.Yield();
            yield return null!;
        }

        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Export.WriteAsync(new WriteTarget(), TabularFormat.Csv, "data", WithANullChunk(), cancellationToken: Token));
        Assert.Equal("chunks", refused.ParamName);
    }

    [Fact]
    public async Task ASelectorThatThrowsOrReturnsNullFaultsTheWriteAndClosesTheStream()
    {
        static async IAsyncEnumerable<int> Messages()
        {
            for (int i = 0; i < 3; i++)
            {
                await Task.Yield();
                yield return i;
            }
        }

        WriteTarget throwing = new();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Export.WriteAsync(throwing, TabularFormat.Csv, "data", Messages(), m => m == 1 ? throw new InvalidOperationException("boom") : Items(2), NoBom, Token));
        Assert.True(throwing.IsDisposed);

        WriteTarget nulls = new();
        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Export.WriteAsync(nulls, TabularFormat.Csv, "data", Messages(), m => m == 1 ? null! : Items(2), NoBom, Token));
        Assert.Equal("itemsOf", refused.ParamName);
        Assert.True(nulls.IsDisposed);

        await using TabularWriter first = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Export.WriteSheetAsync(first, "data", Messages(), m => m == 1 ? throw new InvalidOperationException("boom") : Items(2), Token));
        Assert.Throws<InvalidOperationException>(first.EndRow);

        await using TabularWriter second = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Export.WriteSheetAsync(second, "data", Messages(), m => m == 1 ? null! : Items(2), Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await second.CompleteAsync(Token));
    }

    [Fact]
    public async Task CancellationThroughPerItemAsyncSourceStopsTheWriteAndClosesTheStream()
    {
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        WriteTarget target = new();
        int seen = 0;

        async IAsyncEnumerable<Item> Source([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (Item item in Items(1_000))
            {
                if (++seen == 5)
                {
                    await cancel.CancelAsync();
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }

#pragma warning disable xUnit1051, S8949 // The source is called without a token on purpose: the export must flow its own through WithCancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Export.WriteAsync(target, TabularFormat.Csv, "data", Source(), NoBom, cancel.Token));
#pragma warning restore xUnit1051, S8949

        Assert.True(seen < 100);
        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task ACompletedWritersFileIsNotReportedAsFailed()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Csv);
        await Export.WriteSheetAsync(writer, "data", Items(2), Token);
        await writer.CompleteAsync(Token);

        InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Export.WriteSheetAsync(writer, "more", Items(1), Token));
        InvalidOperationException later = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Export.WriteSheetAsync(writer, "more", Items(1), Token));

        // "incomplete" also contains "complete": the faulted message must be ruled out by name.
        Assert.StartsWith("The file is complete", first.Message, StringComparison.Ordinal);
        Assert.StartsWith("The file is complete", later.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed earlier", later.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExportThatFailsKeepsItsException()
    {
        WriteTarget target = new() { FailWith = new IOException("client went away"), FailOnDispose = new IOException("close failed") };
        TabularExport<long> export = TabularExport.For<long>().Column("n", n => n).Build();

        IOException surfaced = await Assert.ThrowsAsync<IOException>(async () =>
            await export.WriteAsync(target, TabularFormat.Csv, "data", Enumerable.Range(0, 100_000).Select(i => (long)i), cancellationToken: Token));

        Assert.Equal("client went away", surfaced.Message);
    }
}
