using System.Globalization;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// The conversion of a workbook number — a double — into a decimal: the 15 significant digits a
/// double holds, as the runtime's cast gave them up to .NET 10, on every runtime (#133).
/// </summary>
public sealed class WorkbookNumberConversionTests
{
    /// <summary>Doubles and the decimal each converts to, as .NET 10's <c>(decimal)double</c> gave it.</summary>
    public static TheoryData<double, string> Converted => new()
    {
        { 0.1, "0.1" },
        { 1.0 / 3, "0.333333333333333" },
        { 2.0 / 3, "0.666666666666667" },
        { 123456789.012345, "123456789.012345" },
        { 1e-7, "0.0000001" },
        { 1e-15, "0.000000000000001" },
        { 0.00012345678901234567, "0.000123456789012346" },
        { 1.5, "1.5" },
        { 2.5, "2.5" },
        { 0.125, "0.125" },
        { -2.5, "-2.5" },
        { 1e15, "1000000000000000" },
        { 999999999999999.9, "1000000000000000" },
        { 123456789012345.67, "123456789012346" },
        { 1.2345678901234568E+17, "123456789012346000" },
        { 1e20, "100000000000000000000" },
        { 1e28, "10000000000000000000000000000" },
        { -1e28, "-10000000000000000000000000000" },
        { 1e-28, "0.0000000000000000000000000001" },
        // Below decimal's smallest step: zero, not an error.
        { 5e-29, "0" },
        { 1e-300, "0" },
        { double.Epsilon, "0" },
        { 0.0, "0" },
        { -0.0, "0" },
    };

    [Theory]
    [MemberData(nameof(Converted))]
    public void KeepsTheFifteenDigitsADoubleHolds(double value, string expected)
    {
        // As text, so the scale is checked too: 0.1, not 0.10.
        Assert.Equal(expected, WorkbookNumber.ToDecimal(value).ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(7.922816251426434E+28)] // decimal.MaxValue as a double: 2^96, one past it
    [InlineData(-7.922816251426434E+28)]
    [InlineData(1e29)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.NaN)]
    public void RefusesWhatDecimalCannotHoldAsTheCastDid(double value)
    {
        Assert.Throws<OverflowException>(() => WorkbookNumber.ToDecimal(value));
    }

    /// <summary>
    /// Bit for bit what the runtime's cast gives where it still rounds to 15 digits — every runtime
    /// before .NET 11 — over doubles of every magnitude decimal can hold, and over short decimals
    /// written as doubles, whose last digit is where rounding goes wrong.
    /// </summary>
    [Fact]
    public void AgreesWithTheCastOfTheRuntimesThatRoundToFifteenDigits()
    {
        Assert.SkipWhen(Environment.Version.Major >= 11, "From .NET 11 the runtime's cast converts the exact binary value.");

        var random = new Random(133);
        for (int i = 0; i < 200_000; i++)
        {
            double value = i % 2 == 0 ? AnyDouble(random) : ShortDecimal(random);

            Assert.Equal(Cast(value), Convert(value));
        }
    }

    private static double AnyDouble(Random random)
    {
        // Biased exponents 1022 - 100 to 1022 + 100: from well below decimal's smallest step to
        // past its largest value.
        ulong exponent = (ulong)random.Next(922, 1123);
        ulong mantissa = (ulong)random.NextInt64(0, 1L << 52);
        ulong sign = random.Next(2) == 0 ? 0 : 1UL << 63;

        return BitConverter.UInt64BitsToDouble(sign | (exponent << 52) | mantissa);
    }

    private static double ShortDecimal(Random random)
    {
        long digits = random.NextInt64(1, 100_000_000_000_000_000);
        int scale = random.Next(0, 30);

        return double.Parse($"{digits}E-{scale}", CultureInfo.InvariantCulture);
    }

    private static string Cast(double value)
    {
        try
        {
            return string.Join(",", decimal.GetBits((decimal)value));
        }
        catch (OverflowException)
        {
            return "overflow";
        }
    }

    private static string Convert(double value)
    {
        try
        {
            return string.Join(",", decimal.GetBits(WorkbookNumber.ToDecimal(value)));
        }
        catch (OverflowException)
        {
            return "overflow";
        }
    }
}
