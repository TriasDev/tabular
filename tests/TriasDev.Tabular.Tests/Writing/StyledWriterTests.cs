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
}
