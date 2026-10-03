using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>How an ods writer writes numbers and dates, and which it refuses.</summary>
public sealed class OdsValueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<RawCell> One(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            write(writer);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        using OdsCursor cursor = new(new MemoryStream(target.ToArray(), writable: false), cancellationToken: Token);
        Assert.True(cursor.ReadRow(Token));
        Assert.True(cursor.ReadRow(Token));
        return cursor.CurrentRow[0];
    }

    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0, int second = 0, int millisecond = 0) =>
        new(year, month, day, hour, minute, second, millisecond, DateTimeKind.Unspecified);

    public static TheoryData<string, Action<TabularWriter>, RawCell> Written => new()
    {
        { "long", w => w.Write(42L), RawCell.FromNumber(42) },
        { "2^60, exact in a double", w => w.Write(1L << 60), RawCell.FromNumber(1L << 60) },
        { "long.MinValue", w => w.Write(long.MinValue), RawCell.FromNumber(long.MinValue) },
        { "decimal", w => w.Write(1234.5m), RawCell.FromNumber(1234.5) },
        { "decimal of 15 digits", w => w.Write(123_456_789_012.345m), RawCell.FromNumber(123_456_789_012.345) },
        { "double", w => w.Write(-0.1), RawCell.FromNumber(-0.1) },
        { "large double", w => w.Write(1e20), RawCell.FromNumber(1e20) },
        { "date", w => w.Write(At(2026, 10, 3)), RawCell.FromDate(At(2026, 10, 3)) },
        { "date-time", w => w.Write(At(2026, 10, 3, 14, 5, 6, 789)), RawCell.FromDate(At(2026, 10, 3, 14, 5, 6, 789)) },
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
    public async Task WritesDatesXlsxCannotHold()
    {
        DateTime[] dates = [At(1899, 12, 31), At(1900, 2, 28, 23, 59, 59, 999), At(1066, 10, 14, 9, 0, 0), At(2, 1, 1), DateTime.MinValue];

        foreach (DateTime date in dates)
        {
            Assert.Equal(RawCell.FromDate(date), await One(w => w.Write(date)));
        }
    }

    public static TheoryData<string, Action<TabularWriter>> Refused => new()
    {
        { "2^53 + 1", w => w.Write(9_007_199_254_740_993L) },
        { "long.MaxValue", w => w.Write(long.MaxValue) },
        { "16 significant digits", w => w.Write(1_234_567_890_123.456m) },
        { "decimal.MaxValue", w => w.Write(decimal.MaxValue) },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task RefusesANumberADoubleWouldNotGiveBack(string kind, Action<TabularWriter> write)
    {
        Assert.NotEmpty(kind);
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("Id"), new("Amount")]);
        writer.BeginRow();
        writer.Write("x");

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => write(writer));

        Assert.Equal(ErrorCodes.Write.PrecisionLoss, refused.Code);
        Assert.Equal(1, refused.ColumnIndex);
    }

    [Fact]
    public async Task StylesDatesAndDateTimesForLibreOffice()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("d"), new("t")]);
            writer.BeginRow();
            writer.Write(At(2026, 10, 3));
            writer.Write(At(2026, 10, 3, 14, 5, 6));
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        string content = Content(target.ToArray());

        Assert.Contains("table:style-name=\"ce1\" office:value-type=\"date\" office:date-value=\"2026-10-03\"", content, StringComparison.Ordinal);
        Assert.Contains("table:style-name=\"ce2\" office:value-type=\"date\" office:date-value=\"2026-10-03T14:05:06.000\"", content, StringComparison.Ordinal);
    }

    private static string Content(byte[] ods)
    {
        using System.IO.Compression.ZipArchive archive = new(new MemoryStream(ods, writable: false), System.IO.Compression.ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry("content.xml")!.Open());
        return reader.ReadToEnd();
    }
}
