using System.Runtime.CompilerServices;
using System.Text;

using TriasDev.Tabular.Csv;
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
        Item[] items = [.. Enumerable.Range(1, 60_000).Select(i => new Item(i, new string('x', 40), i))];

        await Export.WriteAsync(target, TabularFormat.Csv, "data", Chunks(items, items.Length, Token), NoBom, Token);

        Assert.True(target.AsyncWrites >= 3, $"{target.AsyncWrites} writes reached the target for one 3 MB chunk");
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

        async IAsyncEnumerable<IReadOnlyList<Item>> Source([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (IReadOnlyList<Item> chunk in Chunks(Items(1_000), 100, cancellationToken))
            {
                if (++chunksSeen == 3)
                {
                    await cancel.CancelAsync();
                }

                yield return chunk;
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Export.WriteAsync(target, TabularFormat.Csv, "data", Source(cancel.Token), NoBom, cancel.Token));

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

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Export.WriteAsync(new WriteTarget(), TabularFormat.Csv, "data", WithANullChunk(), cancellationToken: Token));
    }
}
