using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>How an xlsx writer writes numbers and dates, and which it refuses.</summary>
public sealed class XlsxValueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<RawCell> One(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            write(writer);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        byte[] xlsx = target.ToArray();
        Assert.Empty(OoxmlValidation.Errors(xlsx));

        using XlsxCursor cursor = new(new MemoryStream(xlsx, writable: false), cancellationToken: Token);
        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.ReadRow(Token));
        return cursor.CurrentRow[0];
    }

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    public static TheoryData<string, Action<TabularWriter>, RawCell> Written => new()
    {
        { "long", w => w.Write(42L), RawCell.FromNumber(42) },
        { "2^53", w => w.Write(9_007_199_254_740_992L), RawCell.FromNumber(9_007_199_254_740_992d) },
        { "2^60, exact in a double", w => w.Write(1L << 60), RawCell.FromNumber(1L << 60) },
        { "long.MinValue", w => w.Write(long.MinValue), RawCell.FromNumber(long.MinValue) },
        { "decimal", w => w.Write(1234.5m), RawCell.FromNumber(1234.5) },
        { "decimal of 15 digits", w => w.Write(123_456_789_012.345m), RawCell.FromNumber(123_456_789_012.345) },
        { "double", w => w.Write(-0.1), RawCell.FromNumber(-0.1) },
        { "date", w => w.Write(At(2026, 10, 3)), RawCell.FromDate(At(2026, 10, 3)) },
        { "date-time", w => w.Write(At(2026, 10, 3, 14, 5, 6, 789)), RawCell.FromDate(At(2026, 10, 3, 14, 5, 6, 789)) },
        { "16:00, a fraction stored a hair low", w => w.Write(At(2026, 10, 3, 16, 0, 0)), RawCell.FromDate(At(2026, 10, 3, 16, 0, 0)) },
        { "first day", w => w.Write(At(1900, 1, 1)), RawCell.FromDate(At(1900, 1, 1)) },
        { "max value", w => w.Write(DateTime.MaxValue), RawCell.FromDate(At(9999, 12, 31, 23, 59, 59, 999)) },
        { "date only", w => w.Write(new DateOnly(2026, 10, 3)), RawCell.FromDate(At(2026, 10, 3)) },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public async Task WritesEachKindOfValueAsTheCursorReadsIt(string kind, Action<TabularWriter> write, RawCell expected)
    {
        Assert.NotEmpty(kind);
        Assert.Equal(expected, await One(write));
    }

    [Fact]
    public async Task ReadsBackDatesAcrossTheLeapYearBug()
    {
        DateTime[] dates =
        [
            At(1900, 2, 28),
            At(1900, 2, 28, 23, 59, 59, 999),
            At(1900, 3, 1),
            At(1900, 3, 1, 0, 0, 0, 1),
            At(1904, 1, 1),
        ];

        foreach (DateTime date in dates)
        {
            Assert.Equal(RawCell.FromDate(date), await One(w => w.Write(date)));
        }
    }

    public static TheoryData<string, Action<TabularWriter>, string> Refused => new()
    {
        { "2^53 + 1", w => w.Write(9_007_199_254_740_993L), ErrorCodes.Write.PrecisionLoss },
        { "long.MaxValue", w => w.Write(long.MaxValue), ErrorCodes.Write.PrecisionLoss },
        { "16 significant digits", w => w.Write(1_234_567_890_123.456m), ErrorCodes.Write.PrecisionLoss },
        { "decimal.MaxValue", w => w.Write(decimal.MaxValue), ErrorCodes.Write.PrecisionLoss },
        { "before 1900", w => w.Write(At(1899, 12, 31)), ErrorCodes.Write.DateOutOfRange },
        { "date only before 1900", w => w.Write(new DateOnly(1899, 12, 31)), ErrorCodes.Write.DateOutOfRange },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task RefusesAValueAWorkbookWouldNotGiveBack(string kind, Action<TabularWriter> write, string code)
    {
        Assert.NotEmpty(kind);
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("Id"), new("Amount")]);
        writer.BeginRow();
        writer.Write("x");

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => write(writer));

        Assert.Equal(code, refused.Code);
        Assert.Equal(1, refused.ColumnIndex);
        Assert.Equal(2, refused.RowNumber);
    }
}
