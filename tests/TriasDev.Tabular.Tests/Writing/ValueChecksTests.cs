using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>What a workbook's number cell — a double — gives back exactly.</summary>
public sealed class ValueChecksTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(9_007_199_254_740_992L)]
    [InlineData(1L << 60)]
    [InlineData(long.MinValue)]
    public void TakesALongADoubleHolds(long value)
    {
        Assert.Null(ValueChecks.LongInDouble(value));
    }

    [Theory]
    [InlineData(9_007_199_254_740_993L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 100)]
    public void RefusesALongADoubleDoesNotHold(long value)
    {
        Assert.Equal(ErrorCodes.Write.PrecisionLoss, ValueChecks.LongInDouble(value));
    }

    [Fact]
    public void TakesADecimalOfFifteenDigitsAndRefusesMore()
    {
        Assert.Null(ValueChecks.DecimalInDouble(123_456_789_012.345m));
        Assert.Null(ValueChecks.DecimalInDouble(0.1m));
        Assert.Null(ValueChecks.DecimalInDouble(-0.000001m));
        Assert.Equal(ErrorCodes.Write.PrecisionLoss, ValueChecks.DecimalInDouble(1_234_567_890_123.456m));
        Assert.Equal(ErrorCodes.Write.PrecisionLoss, ValueChecks.DecimalInDouble(decimal.MaxValue));
    }
}
