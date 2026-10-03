using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>How a csv writer writes each kind of value, and which values it refuses.</summary>
public sealed class CsvValueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> One(Action<TabularWriter> write, string? culture = null)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = culture, ByteOrderMark = false } }))
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            write(writer);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        return Encoding.UTF8.GetString(target.ToArray())["v\r\n".Length..^2];
    }

    public static TheoryData<string, Action<TabularWriter>, string?, string> Written => new()
    {
        { "long", w => w.Write(-9_007_199_254_740_993L), null, "-9007199254740993" },
        { "long, de-DE", w => w.Write(-42L), "de-DE", "-42" },
        { "int as long", w => w.Write(7), null, "7" },
        { "decimal", w => w.Write(1234.50m), null, "1234.50" },
        { "decimal, de-DE", w => w.Write(-1234.5m), "de-DE", "-1234,5" },
        { "decimal past double", w => w.Write(12345678901234567.891m), null, "12345678901234567.891" },
        { "double", w => w.Write(0.1), null, "0.1" },
        { "double, de-DE", w => w.Write(-2.5), "de-DE", "-2,5" },
        { "date", w => w.Write(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Unspecified)), null, "2026-10-03" },
        { "date-time", w => w.Write(new DateTime(2026, 10, 3, 14, 5, 6, 789, DateTimeKind.Unspecified)), null, "2026-10-03T14:05:06.789" },
        { "date-time, de-DE", w => w.Write(new DateTime(2026, 10, 3, 14, 5, 6, DateTimeKind.Unspecified)), "de-DE", "03.10.2026 14:05:06.000" },
        { "date-time, en-US", w => w.Write(new DateTime(2026, 10, 3, 14, 5, 6, DateTimeKind.Unspecified)), "en-US", "10/3/2026 14:05:06.000" },
        { "sub-millisecond truncated", w => w.Write(new DateTime(2026, 10, 3, 23, 59, 59, 999, DateTimeKind.Unspecified).AddTicks(9_999)), null, "2026-10-03T23:59:59.999" },
        { "max value", w => w.Write(DateTime.MaxValue), null, "9999-12-31T23:59:59.999" },
        { "a time only in sub-milliseconds is a date", w => w.Write(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(5)), null, "2026-10-03" },
        { "date only", w => w.Write(new DateOnly(2026, 10, 3)), null, "2026-10-03" },
        { "year 2", w => w.Write(new DateTime(2, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)), null, "0002-01-01" },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public async Task WritesEachKindOfValue(string kind, Action<TabularWriter> write, string? culture, string expected)
    {
        Assert.NotEmpty(kind);
        Assert.Equal(expected, await One(write, culture));
    }

    public static TheoryData<string, Action<TabularWriter>, string> Refused => new()
    {
        { "NaN", w => w.Write(double.NaN), ErrorCodes.Write.NotFinite },
        { "infinity", w => w.Write(double.PositiveInfinity), ErrorCodes.Write.NotFinite },
        { "negative infinity", w => w.Write(double.NegativeInfinity), ErrorCodes.Write.NotFinite },
        { "more than 15 digits", w => w.Write(0.1 + 0.2), ErrorCodes.Write.PrecisionLoss },
        { "too small for decimal", w => w.Write(1e-30), ErrorCodes.Write.PrecisionLoss },
        { "too large for decimal", w => w.Write(1e30), ErrorCodes.Write.PrecisionLoss },
        { "year 1", w => w.Write(DateTime.MinValue), ErrorCodes.Write.DateOutOfRange },
        { "year 1, late", w => w.Write(new DateTime(1, 12, 31, 0, 0, 0, DateTimeKind.Unspecified)), ErrorCodes.Write.DateOutOfRange },
        { "date only, year 1", w => w.Write(DateOnly.MinValue), ErrorCodes.Write.DateOutOfRange },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task RefusesAValueTheImportWouldNotReadBackAsWritten(string kind, Action<TabularWriter> write, string code)
    {
        Assert.NotEmpty(kind);
        WriteTarget target = new();
        await using TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv);
        writer.BeginSheet("data", [new("Id"), new("Amount")]);
        writer.BeginRow();
        writer.Write(1);

        TabularWriteException refused = Assert.Throws<TabularWriteException>(() => write(writer));

        Assert.Equal(code, refused.Code);
        Assert.Equal("data", refused.SheetName);
        Assert.Equal(2, refused.RowNumber);
        Assert.Equal(1, refused.ColumnIndex);
        Assert.Equal("Amount", refused.Header);
        Assert.Throws<InvalidOperationException>(() => writer.WriteEmpty());
    }

    [Fact]
    public async Task HoldsLongsAndDecimalsExactlyWhereXlsxWouldNot()
    {
        // csv writes digits, so neither check that protects a double-based format applies.
        Assert.Equal("9007199254740993", await One(w => w.Write(9_007_199_254_740_993L)));
        Assert.Equal("0.1234567890123456789", await One(w => w.Write(0.1234567890123456789m)));
    }
}
