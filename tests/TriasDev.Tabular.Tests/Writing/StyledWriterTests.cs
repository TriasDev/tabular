using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Registering styles and writing styled cells, independent of the format.</summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class StyledWriterTests
{
    private static readonly CellStyle Red = new() { Fill = CellColor.FromRgb(0xFF0000) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameStyleGetsTheSameId()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        StyleId first = writer.RegisterStyle(Red);
        StyleId again = writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0xFF0000) });
        StyleId other = writer.RegisterStyle(Red with { Wrap = true });

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(default, first);
    }

    [Fact]
    public async Task TheStyleAfterTheLimitIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        for (int i = 0; i < 4096; i++)
        {
            writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(i) });
        }

        writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0) });       // already registered: no new style

        // 4,097 styles registered by hand is a defect in the calling code, not a file too large:
        // an InvalidOperationException, not the reader's TabularLimitException (a 413 to a host).
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(4096) }));
        Assert.Contains("4096", refused.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(0) }));   // faulted, as for any refused style
    }

    [Fact]
    public async Task AHeaderStyleAfterTheLimitIsRefusedAsRegisterStyleRefusesIt()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        for (int i = 0; i < 4096; i++)
        {
            writer.RegisterStyle(new CellStyle { Fill = CellColor.FromRgb(i) });
        }

        Assert.Throws<InvalidOperationException>(() => writer.BeginSheet("data", [new("a")], new SheetOptions { HeaderStyle = new CellStyle { Wrap = true } }));
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AStyleRuleThatOutgrowsTheLimitIsAWriteErrorAtItsCell(TabularFormat format)
    {
        // A rule turns data into styles, so the 4,097th distinct one is the data's doing: a located
        // TabularWriteException, like any value the format cannot hold.
        TabularExport<int> export = TabularExport.For<int>()
            .Column("n", i => (long)i)
            .Column("shade", i => (long)i, style: v => new CellStyle { Fill = CellColor.FromRgb((int)v) })
            .Build();

        TabularWriteException refused = await Assert.ThrowsAsync<TabularWriteException>(
            async () => await export.WriteAsync(new WriteTarget(), format, "data", Enumerable.Range(0, 5000), cancellationToken: Token));

        Assert.Equal(ErrorCodes.Write.TooManyStyles, refused.Code);
        Assert.Equal("data", refused.SheetName);
        Assert.Equal(4098, refused.RowNumber);       // item 4096 (the 4,097th style) is row 4098 under the header
        Assert.Equal(1, refused.ColumnIndex);
        Assert.Equal("shade", refused.Header);
    }

    [Fact]
    public async Task ABatchStyleRuleThatOutgrowsTheLimitIsAWriteErrorAtItsCell()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("n"), new("shade")]);
        long[] values = [.. Enumerable.Range(0, 5000).Select(i => (long)i)];
        ColumnBatch batch = new();
        batch.Reset(values.Length);
        batch.Add(values);
        batch.Add(values, v => new CellStyle { Fill = CellColor.FromRgb((int)v) });

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => writer.WriteBatch(batch));

        Assert.Equal(ErrorCodes.Write.TooManyStyles, refused.Code);
        Assert.Equal(4098, refused.RowNumber);
        Assert.Equal(1, refused.ColumnIndex);
        Assert.Equal("shade", refused.Header);
    }

    [Fact]
    public async Task AnUndefinedAlignmentIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.RegisterStyle(new CellStyle { Horizontal = (CellHorizontalAlignment)9 }));
    }

    [Fact]
    public async Task AStyleIdFromAnotherWriterIsRefused()
    {
        await using TabularWriter other = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        other.RegisterStyle(Red);
        StyleId foreign = other.RegisterStyle(Red with { Wrap = true });           // index 2 there

        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.RegisterStyle(Red);                                                  // only index 1 here
        writer.BeginSheet("data", [new("x")]);
        writer.BeginRow();

        Assert.Throws<ArgumentException>(() => writer.Write(1.5, foreign));
    }

    [Fact]
    public async Task AStyleIdFromAnotherWriterWithTheSameIndexIsRefused()
    {
        await using TabularWriter other = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        other.RegisterStyle(Red);
        StyleId foreign = other.RegisterStyle(Red with { Wrap = true });           // index 2 there

        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.RegisterStyle(Red);
        StyleId own = writer.RegisterStyle(Red with { Wrap = true });               // index 2 here too
        writer.BeginSheet("data", [new("x")]);
        writer.BeginRow();

        Assert.NotEqual(own, foreign);
        Assert.Throws<ArgumentException>(() => writer.Write(1.5, foreign));
    }

    [Fact]
    public async Task TheDefaultStyleIdIsTheUnstyledCellOnEveryWriter()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("x")]);
        writer.BeginRow();

        writer.Write(1.5, default(StyleId));
        writer.EndRow();

        Assert.Equal(default, default(StyleId));
    }

    [Fact]
    public async Task CsvIgnoresStyles()
    {
        async Task<byte[]> Write(bool styled)
        {
            WriteTarget target = new();

            await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv))
            {
                StyleId style = styled ? writer.RegisterStyle(Red with { NumberFormat = NumberFormat.Parse("0.00"), DateFormat = DateFormat.Parse("dd/mm/yyyy") }) : default;
                writer.BeginSheet("data", [new("text"), new("long"), new("decimal"), new("double"), new("date"), new("day"), new("flag"), new("none")]);
                writer.BeginRow();
                writer.Write("a", style);
                writer.Write(12L, style);
                writer.Write(1.25m, style);
                writer.Write(2.5, style);
                writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), style);
                writer.Write(new DateOnly(2026, 10, 4), style);
                writer.Write(true, style);
                writer.WriteEmpty(style);
                writer.EndRow();
                await writer.CompleteAsync(Token);
            }

            return target.ToArray();
        }

        Assert.Equal(await Write(styled: false), await Write(styled: true));
    }

    [Fact]
    public async Task ANullStyleIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentNullException>(() => writer.RegisterStyle(null!));
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AllocatesNothingPerStyledCell(TabularFormat format)
    {
        CellStyle[] palette = [.. Enumerable.Range(0, 8).Select(i => new CellStyle { Fill = CellColor.FromRgb(i * 0x101010), NumberFormat = NumberFormat.Parse("0.00"), DateFormat = DateFormat.Parse("dd/mm/yyyy") })];

        async ValueTask<long> Allocated(int rows)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();

            await using (TabularWriter writer = TabularWriter.Create(Stream.Null, format, new TabularWriterOptions { LeaveOpen = true }))
            {
                StyleId[] styles = [.. palette.Select(writer.RegisterStyle)];
                writer.BeginSheet("data", [new("text"), new("number"), new("integer"), new("date"), new("flag")]);

                for (int i = 0; i < rows; i++)
                {
                    StyleId style = styles[i % styles.Length];
                    writer.BeginRow();
                    writer.Write("text", style);
                    writer.Write(i * 0.5, style);
                    writer.Write((long)i, style);
                    writer.Write(new DateOnly(2026, 10, 4), style);
                    writer.Write(i % 2 == 0, style);
                    writer.EndRow();

                    if (writer.FlushRecommended)
                    {
                        await writer.FlushAsync(Token);
                    }
                }

                await writer.CompleteAsync(Token);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Warm-up at the full size: static state, JIT, and the array pool reaching the 1 MB working set one flush holds.
        await Allocated(100_000);
        long tenThousand = await Allocated(10_000);
        long hundredThousand = await Allocated(100_000);

        // 90,000 more rows of five styled cells: what grows with them is under a byte a row.
        Assert.True(hundredThousand - tenThousand < 90_000, $"{format}: {tenThousand:N0} bytes for 10k rows, {hundredThousand:N0} for 100k");
    }
}
