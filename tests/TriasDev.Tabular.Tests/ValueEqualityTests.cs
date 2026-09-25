using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// The two value structs compare by what they hold: a raw cell by its kind and contents, a mapped
/// value by its type and value.
/// </summary>
public sealed class ValueEqualityTests
{
    [Fact]
    public void ARawCellOfOneKindIsNotEqualToTheSameTextOfAnother()
    {
        Assert.NotEqual(RawCell.FromText("1"), RawCell.FromNumber(1));
        Assert.NotEqual(RawCell.FromNumber(1), RawCell.FromBoolean(true));
    }

    [Fact]
    public void RawCellsHoldingTheSameAreEqualAndHashAlike()
    {
        Assert.True(RawCell.FromNumber(2.5) == RawCell.FromNumber(2.5));
        Assert.Equal(RawCell.FromNumber(2.5).GetHashCode(), RawCell.FromNumber(2.5).GetHashCode());

        // Text is trimmed on the way in, and whitespace alone is nothing.
        Assert.Equal(RawCell.FromText("a"), RawCell.FromText(" a "));
        Assert.Equal(RawCell.Empty, RawCell.FromText("   "));
        Assert.Equal(RawCell.Empty, default);
    }

    [Fact]
    public void ARawCellHoldingNotANumberEqualsItself()
    {
        // Equality has to be reflexive for a cell to be found in a set at all.
        RawCell nan = RawCell.FromNumber(double.NaN);

        Assert.True(nan.Equals(nan));
        Assert.Contains(nan, new HashSet<RawCell> { nan });
    }

    [Fact]
    public void MappedNumbersCompareByValueNotByHowTheyWereWritten()
    {
        // 1.0 and 1.00 are one amount. Comparing the rendered text as well made them two, and a
        // caller de-duplicating mapped values kept both.
        MappedValue one = MappedValue.FromDecimal(1.0m);
        MappedValue sameAmount = MappedValue.FromDecimal(1.00m);

        Assert.Equal(one, sameAmount);
        Assert.Equal(one.GetHashCode(), sameAmount.GetHashCode());
    }

    [Fact]
    public void MappedValuesOfDifferentTypesAreNotEqual()
    {
        Assert.NotEqual(MappedValue.FromInteger(1), MappedValue.FromDecimal(1m));
        Assert.NotEqual(MappedValue.FromText("1"), MappedValue.FromInteger(1));
        Assert.NotEqual(MappedValue.FromBoolean(false), MappedValue.Absent);
    }

    [Fact]
    public void MappedTextComparesOrdinally()
    {
        Assert.NotEqual(MappedValue.FromText("a"), MappedValue.FromText("A"));
        Assert.Equal(MappedValue.FromText("ä"), MappedValue.FromText("ä"));
    }

    [Fact]
    public void MappedDatesCompareByTheInstantTheyHold()
    {
        DateTime morning = new(2026, 9, 25, 8, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(MappedValue.FromDate(morning), MappedValue.FromDate(morning));
        Assert.NotEqual(MappedValue.FromDate(morning), MappedValue.FromDate(morning.AddHours(1)));
    }
}
