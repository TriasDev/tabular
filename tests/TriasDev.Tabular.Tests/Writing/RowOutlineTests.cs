using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The outline a sheet declares, the level a row is begun at, and what the writer refuses.</summary>
public sealed class RowOutlineTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<TabularFormat, int, int?> RefusedLevels()
    {
        TheoryData<TabularFormat, int, int?> data = [];

        foreach (TabularFormat format in new[] { TabularFormat.Csv, TabularFormat.Zip, TabularFormat.Xlsx, TabularFormat.Ods })
        {
            data.Add(format, -1, 2);
            data.Add(format, 3, 2);
            data.Add(format, 1, null);
        }

        return data;
    }

    private static TabularWriter NewWriter(TabularFormat format = TabularFormat.Xlsx) => TabularWriter.Create(new WriteTarget(), format);

    private static SheetOptions Outlined(int levels, int? shown = null) =>
        new() { Outline = new RowOutline { Levels = levels, ShownLevels = shown } };

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-1)]
    public async Task RefusesLevelsOutsideOneToSeven(int levels)
    {
        await using TabularWriter writer = NewWriter();

        ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(() => writer.BeginSheet("data", [new("a")], Outlined(levels)));

        Assert.Equal("options", refused.ParamName);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task RefusesShownLevelsOutsideOneToLevels(int shown, bool accepted)
    {
        await using TabularWriter writer = NewWriter();

        if (accepted)
        {
            writer.BeginSheet("data", [new("a")], Outlined(2, shown));
        }
        else
        {
            Assert.Equal("options", Assert.Throws<ArgumentOutOfRangeException>(() => writer.BeginSheet("data", [new("a")], Outlined(2, shown))).ParamName);
        }
    }

    [Fact]
    public async Task RefusesAnUndefinedSummaryRows()
    {
        await using TabularWriter writer = NewWriter();
        SheetOptions options = new() { Outline = new RowOutline { Levels = 1, SummaryRows = (SummaryRows)7 } };

        Assert.Equal("options", Assert.Throws<ArgumentOutOfRangeException>(() => writer.BeginSheet("data", [new("a")], options)).ParamName);
    }

    [Theory]
    [MemberData(nameof(RefusedLevels))]
    public async Task RefusesALevelOutsideTheOutline(TabularFormat format, int level, int? levels)
    {
        await using TabularWriter writer = NewWriter(format);
        writer.BeginSheet("data", [new("first"), new("second")], levels is { } n ? Outlined(n) : null);
        writer.BeginRow();
        writer.Write("x");
        writer.EndRow();

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => writer.BeginRow(level));

        Assert.Equal(ErrorCodes.Write.OutlineLevel, refused.Code);
        Assert.Equal("write.outline-level", refused.Code);
        Assert.Equal("data", refused.SheetName);
        Assert.Equal(3, refused.RowNumber);        // header 1, one row 2, the refused row 3
        Assert.Equal(0, refused.ColumnIndex);
        Assert.Equal("first", refused.Header);
        Assert.Throws<InvalidOperationException>(() => writer.BeginRow());    // faulted
    }

    [Fact]
    public async Task RowLimitIsJudgedBeforeTheLevel()
    {
        await using TabularWriter writer = NewWriter();
        writer.MaxRows = 3;
        writer.BeginSheet("data", [new("a")], Outlined(1));

        for (int row = 2; row <= 3; row++)
        {
            writer.BeginRow();
            writer.Write(row);
            writer.EndRow();
        }

        Assert.Equal(ErrorCodes.Write.TooManyRows, Assert.Throws<TabularWriteException>(() => writer.BeginRow(5)).Code);
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Zip)]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    public async Task AcceptsEveryLevelUpToLevels(TabularFormat format)
    {
        WriteTarget target = new();
        await using TabularWriter writer = TabularWriter.Create(target, format);
        writer.BeginSheet("data", [new("a")], Outlined(7));

        foreach (int level in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0 })
        {
            writer.BeginRow(level);
            writer.Write(level);
            writer.EndRow();
        }

        await writer.CompleteAsync(Token);

        Assert.NotEmpty(target.ToArray());
    }

    [Theory]
    [InlineData(TabularFormat.Csv)]
    [InlineData(TabularFormat.Zip)]
    public async Task CsvIgnoresTheOutline(TabularFormat format)
    {
        byte[] plain = await Rows(format, null, _ => 0);
        byte[] outlined = await Rows(format, Outlined(2, 1), i => new[] { 0, 1, 2, 1, 0 }[i]);

        Assert.Equal(plain, outlined);
    }

    private static Task<byte[]> Rows(TabularFormat format, SheetOptions? options, Func<int, int> level) =>
        SheetLayoutTests.Write(format, writer =>
        {
            writer.BeginSheet("data", [new("a")], options);

            for (int i = 0; i < 5; i++)
            {
                if (options is null)
                {
                    writer.BeginRow();
                }
                else
                {
                    writer.BeginRow(level(i));
                }

                writer.Write(i);
                writer.EndRow();
            }
        });
}
