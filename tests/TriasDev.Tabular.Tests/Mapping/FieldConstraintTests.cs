using TriasDev.Tabular.Mapping;

using Xunit;

namespace TriasDev.Tabular.Tests.Mapping;

/// <summary>Pins each rule a mapped value can be held to.</summary>
public sealed class FieldConstraintTests
{
    [Theory]
    [InlineData("DEU", true)]
    [InlineData("DE", false)]
    [InlineData("DEUX", false)]
    public void ExactLengthCountsCharacters(string value, bool satisfied) =>
        Assert.Equal(satisfied, new FieldConstraint.ExactLength(3).IsSatisfiedBy(MappedValue.FromText(value)));

    [Theory]
    [InlineData("abc", true)]
    [InlineData("ab", false)]
    public void MinLengthCountsCharacters(string value, bool satisfied) =>
        Assert.Equal(satisfied, new FieldConstraint.MinLength(3).IsSatisfiedBy(MappedValue.FromText(value)));

    [Theory]
    [InlineData("abc", true)]
    [InlineData("abcd", false)]
    public void MaxLengthCountsCharacters(string value, bool satisfied) =>
        Assert.Equal(satisfied, new FieldConstraint.MaxLength(3).IsSatisfiedBy(MappedValue.FromText(value)));

    [Fact]
    public void MinValueComparesNumbers()
    {
        Assert.True(new FieldConstraint.MinValue(0).IsSatisfiedBy(MappedValue.FromDecimal(0.5m)));
        Assert.False(new FieldConstraint.MinValue(0).IsSatisfiedBy(MappedValue.FromDecimal(-1m)));
    }

    [Fact]
    public void MaxValueComparesNumbers()
    {
        Assert.True(new FieldConstraint.MaxValue(100).IsSatisfiedBy(MappedValue.FromInteger(100)));
        Assert.False(new FieldConstraint.MaxValue(100).IsSatisfiedBy(MappedValue.FromInteger(101)));
    }

    [Fact]
    public void AllowedValuesComparesAgainstASet()
    {
        FieldConstraint constraint = new FieldConstraint.AllowedValues(["DEU", "AUT"]);

        Assert.True(constraint.IsSatisfiedBy(MappedValue.FromText("DEU")));
        Assert.False(constraint.IsSatisfiedBy(MappedValue.FromText("CHE")));
        Assert.False(constraint.IsSatisfiedBy(MappedValue.FromText("deu")));
    }

    [Fact]
    public void AllowedValuesMayDisregardCase()
    {
        FieldConstraint constraint = new FieldConstraint.AllowedValues(["DEU"], ignoreCase: true);

        Assert.True(constraint.IsSatisfiedBy(MappedValue.FromText("deu")));
    }

    [Fact]
    public void PatternMatches()
    {
        FieldConstraint constraint = new FieldConstraint.Pattern("^[A-Z]{3}$");

        Assert.True(constraint.IsSatisfiedBy(MappedValue.FromText("DEU")));
        Assert.False(constraint.IsSatisfiedBy(MappedValue.FromText("De1")));
    }

    [Fact]
    public void APatternThatCouldRunAwayFailsTheValueRatherThanTheRun()
    {
        // The classic catastrophic pattern against input built to defeat it. Bounded, this returns
        // false in milliseconds; unbounded it would not return at all, and it arrives as data from a
        // calling domain applied to every row of a file a user chose.
        FieldConstraint constraint = new FieldConstraint.Pattern("^(a+)+$");

        bool satisfied = constraint.IsSatisfiedBy(MappedValue.FromText(new string('a', 40) + "!"));

        Assert.False(satisfied);
    }

    [Fact]
    public void EveryRuleRejectsAnAbsentValue()
    {
        // Whether an absent value is allowed is what "required" decides. A rule about the value
        // cannot be satisfied when there is no value.
        List<FieldConstraint> constraints =
        [
            new FieldConstraint.ExactLength(3),
            new FieldConstraint.MinLength(1),
            new FieldConstraint.AllowedValues(["DEU"]),
            new FieldConstraint.Pattern("^.*$"),
        ];

        Assert.All(constraints, c => Assert.False(c.IsSatisfiedBy(MappedValue.Absent)));
    }
}
