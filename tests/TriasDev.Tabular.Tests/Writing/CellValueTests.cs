using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Writing a typed value without boxing, and turning a rule's style into an id.</summary>
[Collection(AllocationMeasurementCollection.Name)]
public sealed class CellValueTests
{
    private static readonly CellStyle Red = new() { Fill = CellColor.FromRgb(0xFF0000) };

    [Fact]
    public void KnowsTheSupportedTypes()
    {
        Assert.True(CellValue<string>.Supported);
        Assert.True(CellValue<int?>.Supported);
        Assert.True(CellValue<DateOnly>.Supported);
        Assert.True(CellValue<decimal?>.Supported);
        Assert.False(CellValue<float>.Supported);
        Assert.False(CellValue<Guid>.Supported);
        Assert.False(CellValue<object>.Supported);
    }

    [Fact]
    public async Task WritesEachTypeAsTheTypedWriteDoes()
    {
        static void Row(TabularWriter writer, bool typed)
        {
            writer.BeginRow();

            if (typed)
            {
                CellValue<string?>.Write(writer, "text", default);
                CellValue<int>.Write(writer, 7, default);
                CellValue<short?>.Write(writer, (short)3, default);
                CellValue<long?>.Write(writer, null, default);
                CellValue<double>.Write(writer, 2.5, default);
                CellValue<decimal>.Write(writer, 1.25m, default);
                CellValue<bool?>.Write(writer, true, default);
                CellValue<DateTime>.Write(writer, new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified), default);
                CellValue<DateOnly?>.Write(writer, new DateOnly(2026, 10, 4), default);
            }
            else
            {
                writer.Write("text");
                writer.Write(7L);
                writer.Write(3L);
                writer.WriteEmpty();
                writer.Write(2.5);
                writer.Write(1.25m);
                writer.Write(true);
                writer.Write(new DateTime(2026, 10, 4, 9, 5, 0, DateTimeKind.Unspecified));
                writer.Write(new DateOnly(2026, 10, 4));
            }

            writer.EndRow();
        }

        foreach (TabularFormat format in new[] { TabularFormat.Csv, TabularFormat.Xlsx, TabularFormat.Ods })
        {
            WriteColumn[] columns = [.. Enumerable.Range(1, 9).Select(i => new WriteColumn($"c{i}"))];
            byte[] typed = await SheetLayoutTests.Write(format, writer => { writer.BeginSheet("data", columns); Row(writer, typed: true); });
            byte[] plain = await SheetLayoutTests.Write(format, writer => { writer.BeginSheet("data", columns); Row(writer, typed: false); });

            Assert.Equal(plain, typed);
        }
    }

    [Fact]
    public async Task AStyledNullIsAStyledEmptyCell()
    {
        byte[] xlsx = await SheetLayoutTests.Write(TabularFormat.Xlsx, writer =>
        {
            StyleId red = writer.Style(Red);
            writer.BeginSheet("data", [new("a"), new("b")]);
            writer.BeginRow();
            CellValue<double?>.Write(writer, null, red);
            CellValue<string?>.Write(writer, null, red);
            writer.EndRow();
        });

        string sheet = SheetLayoutTests.Entry(xlsx, "xl/worksheets/sheet1.xml");
        Assert.Contains("<row r=\"2\"><c s=\"4\"/><c s=\"4\"/></row>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StyleForGivesOneIdPerStyleAndNoneForNull()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        StyleId first = writer.StyleFor(Red);
        Assert.Equal(first, writer.StyleFor(Red));
        Assert.Equal(first, writer.StyleFor(Red with { }));           // another instance, same value
        Assert.Equal(first, writer.Style(Red));
        Assert.Equal(default, writer.StyleFor(null));
    }

    [Fact]
    public async Task StyleForKeepsItsCacheBoundedWhenARuleAllocatesPerCell()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);

        StyleId id = default;

        for (int i = 0; i < 100_000; i++)
        {
            id = writer.StyleFor(new CellStyle { Fill = CellColor.FromRgb(0xFF0000) });
        }

        Assert.Equal(writer.Style(Red), id);
        Assert.True(writer.StyleCacheCount <= TabularWriter.StyleCacheLimit, $"{writer.StyleCacheCount} cached styles");
    }

    [Fact]
    public async Task StyleForAllocatesNothingOnAHit()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        CellStyle blue = new() { Fill = CellColor.FromRgb(0x0000FF) };
        writer.StyleFor(Red);
        writer.StyleFor(blue);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100_000; i++)
        {
            writer.StyleFor(i % 2 == 0 ? Red : blue);
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 99_999);
    }

    [Fact]
    public async Task StyleForKeepsTheLastHitPerColumnSoAlternatingStylesAcrossARowAllocateNothing()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        CellStyle blue = new() { Fill = CellColor.FromRgb(0x0000FF) };
        writer.BeginSheet("data", [new("a"), new("b")]);
        StyleId red = writer.Style(Red);
        StyleId blueId = writer.Style(blue);

        bool same = Row(writer, red, blueId);
        same &= Row(writer, red, blueId);

        Assert.True(same);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1_000; i++)
        {
            same &= Row(writer, red, blueId);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(same);
        Assert.InRange(allocated, 0, 99_999);

        bool Row(TabularWriter w, StyleId first, StyleId second)
        {
            w.BeginRow();
            bool one = w.StyleFor(Red) == first;
            w.Write(1L);
            bool two = w.StyleFor(blue) == second;
            w.Write(2L);
            w.EndRow();
            return one && two;
        }
    }
}
