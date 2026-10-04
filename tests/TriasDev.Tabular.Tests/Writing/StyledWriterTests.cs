using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Registering styles and writing styled cells, independent of the format.</summary>
public sealed class StyledWriterTests
{
    private static readonly CellStyle Red = new() { Fill = CellColor.FromRgb(0xFF0000) };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameStyleGetsTheSameId()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        StyleId first = writer.Style(Red);
        StyleId again = writer.Style(new CellStyle { Fill = CellColor.FromRgb(0xFF0000) });
        StyleId other = writer.Style(Red with { Wrap = true });

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
            writer.Style(new CellStyle { Fill = CellColor.FromRgb(i) });
        }

        writer.Style(new CellStyle { Fill = CellColor.FromRgb(0) });       // already registered: no new style
        TabularLimitException refused = Assert.Throws<TabularLimitException>(() => writer.Style(new CellStyle { Fill = CellColor.FromRgb(4096) }));
        Assert.Contains("4096", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUndefinedAlignmentIsRefused()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Style(new CellStyle { Horizontal = (HorizontalAlignment)9 }));
    }

    [Fact]
    public async Task AStyleIdFromAnotherWriterIsRefused()
    {
        await using TabularWriter other = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        other.Style(Red);
        StyleId foreign = other.Style(Red with { Wrap = true });           // index 2 there

        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.Style(Red);                                                  // only index 1 here
        writer.BeginSheet("data", [new("x")]);
        writer.BeginRow();

        Assert.Throws<ArgumentException>(() => writer.Write(1.5, foreign));
    }

    [Fact]
    public async Task AStyleIdFromAnotherWriterWithTheSameIndexIsRefused()
    {
        await using TabularWriter other = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        other.Style(Red);
        StyleId foreign = other.Style(Red with { Wrap = true });           // index 2 there

        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.Style(Red);
        StyleId own = writer.Style(Red with { Wrap = true });               // index 2 here too
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
                StyleId style = styled ? writer.Style(Red with { Number = NumberFormat.Parse("0.00"), Date = DateFormat.Parse("dd/mm/yyyy") }) : default;
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

        Assert.Throws<ArgumentNullException>(() => writer.Style(null!));
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AllocatesNothingPerStyledCell(TabularFormat format)
    {
        CellStyle[] palette = [.. Enumerable.Range(0, 8).Select(i => new CellStyle { Fill = CellColor.FromRgb(i * 0x101010), Number = NumberFormat.Parse("0.00"), Date = DateFormat.Parse("dd/mm/yyyy") })];

        async ValueTask<long> Allocated(int rows)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();

            await using (TabularWriter writer = TabularWriter.Create(Stream.Null, format, new TabularWriterOptions { LeaveOpen = true }))
            {
                StyleId[] styles = [.. palette.Select(writer.Style)];
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
