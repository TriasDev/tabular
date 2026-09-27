using Xunit;

namespace TriasDev.Tabular.Tests.Mapping;

/// <summary>A range rule judges numbers, and refuses what is not one.</summary>
public sealed class RangeRuleTests
{

    // -- A rule about numbers cannot be satisfied by something that is not one -------------------

    [Theory]
    [InlineData("Acme GmbH")]
    [InlineData("")]
    public void ARangeRuleRefusesAValueThatIsNotANumber(string text)
    {
        // MappedValue.Number reads anything that is not a number as zero, so MaxValue(100) used to
        // accept a company name and MinValue(0) accepted everything.
        MappedValue value = text.Length == 0 ? MappedValue.Absent : MappedValue.FromText(text);

        Assert.False(new FieldConstraint.MaxValue(100).IsSatisfiedBy(value));
        Assert.False(new FieldConstraint.MinValue(0).IsSatisfiedBy(value));
        Assert.False(new FieldConstraint.MinValue(-5).IsSatisfiedBy(value));
    }

    [Fact]
    public void ARangeRuleRefusesADate()
    {
        MappedValue date = MappedValue.FromDate(new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Unspecified));

        Assert.False(new FieldConstraint.MaxValue(decimal.MaxValue).IsSatisfiedBy(date));
    }

    [Fact]
    public void ARangeRuleStillJudgesNumbers()
    {
        Assert.True(new FieldConstraint.MinValue(18).IsSatisfiedBy(MappedValue.FromInteger(21)));
        Assert.False(new FieldConstraint.MinValue(18).IsSatisfiedBy(MappedValue.FromInteger(17)));
        Assert.True(new FieldConstraint.MaxValue(100).IsSatisfiedBy(MappedValue.FromDecimal(99.5m)));
        Assert.False(new FieldConstraint.MaxValue(100).IsSatisfiedBy(MappedValue.FromDecimal(100.5m)));
    }
}
