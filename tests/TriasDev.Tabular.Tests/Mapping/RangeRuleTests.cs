using System.Text;

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

    [Fact]
    public void ARangeRuleAcceptsAValueEqualToItsBound()
    {
        // "At least 18" includes 18, and "at most 100" includes 100.
        Assert.True(new FieldConstraint.MinValue(18).IsSatisfiedBy(MappedValue.FromInteger(18)));
        Assert.True(new FieldConstraint.MaxValue(100).IsSatisfiedBy(MappedValue.FromInteger(100)));

        IntegerImportField age = ImportField.Integer("age").AtLeast(18);
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "age", FieldName = "age" }] };

        using ImportRun<long?> run = TabularImporter.Import(
            new MemoryStream(Encoding.UTF8.GetBytes("age\n18\n"), writable: false),
            "test.csv",
            plan,
            new ImportSchema { Fields = [age] },
            row => row[age],
            cancellationToken: TestContext.Current.CancellationToken);

        ImportResult<long?> result = run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Equal([18L], result.Items);
    }
}
