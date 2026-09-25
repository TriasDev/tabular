using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Mapping;

using Xunit;

namespace TriasDev.Tabular.Tests.Mapping;

/// <summary>Pins what is wrong with a plan, decided without opening a file.</summary>
public sealed class MappingPlanValidatorTests
{
    private static TargetSchema Schema =>
        new()
        {
            Fields =
            [
                new TargetField { Name = "countryIso3", Type = ColumnType.Text, Required = true, Constraints = [new FieldConstraint.ExactLength(3)] },
                new TargetField { Name = "amount", Type = ColumnType.Decimal },
            ],
        };

    private static ColumnBinding Bind(int index, string field, string header = "h") =>
        new() { SourceColumnIndex = index, SourceHeader = header, TargetFieldName = field };

    [Fact]
    public void AcceptsAPlanThatCoversTheSchema()
    {
        MappingPlan plan = new() { Bindings = [Bind(0, "countryIso3"), Bind(1, "amount")] };

        Assert.Empty(MappingPlanValidator.Validate(plan, Schema));
    }

    [Fact]
    public void AcceptsAPlanThatLeavesAnOptionalFieldUnmapped()
    {
        MappingPlan plan = new() { Bindings = [Bind(0, "countryIso3")] };

        Assert.Empty(MappingPlanValidator.Validate(plan, Schema));
    }

    [Fact]
    public void RejectsAPlanThatLeavesARequiredFieldUnmapped()
    {
        MappingPlan plan = new() { Bindings = [Bind(1, "amount")] };

        MappingFault fault = Assert.Single(MappingPlanValidator.Validate(plan, Schema));

        Assert.Equal("mapping.required-field-unmapped", fault.Code);
        Assert.Equal("countryIso3", fault.TargetFieldName);
    }

    [Fact]
    public void RejectsTwoColumnsFeedingOneField()
    {
        MappingPlan plan = new() { Bindings = [Bind(0, "countryIso3"), Bind(2, "countryIso3")] };

        MappingFault fault = Assert.Single(MappingPlanValidator.Validate(plan, Schema));

        Assert.Equal("mapping.duplicate-binding", fault.Code);
        Assert.Equal(2, fault.SourceColumnIndex);
    }

    [Fact]
    public void RejectsABindingToAFieldTheSchemaDoesNotDeclare()
    {
        MappingPlan plan = new() { Bindings = [Bind(0, "countryIso3"), Bind(1, "nonsense")] };

        MappingFault fault = Assert.Single(MappingPlanValidator.Validate(plan, Schema));

        Assert.Equal("mapping.unknown-field", fault.Code);
        Assert.Equal("nonsense", fault.TargetFieldName);
    }

    [Fact]
    public void RejectsANegativeColumnIndex()
    {
        MappingPlan plan = new() { Bindings = [Bind(-1, "countryIso3")] };

        Assert.Contains(MappingPlanValidator.Validate(plan, Schema), f => f.Code == "mapping.invalid-column");
    }

    [Fact]
    public void RejectsACultureThatDoesNotExist()
    {
        MappingPlan plan = new() { Culture = "xx-ZZ-nonsense", Bindings = [Bind(0, "countryIso3")] };

        Assert.Contains(MappingPlanValidator.Validate(plan, Schema), f => f.Code == "mapping.unknown-culture");
    }

    [Fact]
    public void AcceptsACultureThatDoes()
    {
        MappingPlan plan = new() { Culture = "de-DE", Bindings = [Bind(0, "countryIso3")] };

        Assert.Empty(MappingPlanValidator.Validate(plan, Schema));
    }

    [Fact]
    public void ReportsEveryFaultAtOnce()
    {
        // A user who fixes one fault and is then told about the next has to upload again to learn
        // about the third.
        MappingPlan plan = new()
        {
            HeaderRowIndex = -1,
            SheetIndex = -2,
            Bindings = [Bind(0, "nonsense"), Bind(-3, "amount")],
        };

        IReadOnlyList<MappingFault> faults = MappingPlanValidator.Validate(plan, Schema);

        Assert.Contains(faults, f => f.Code == "mapping.unknown-field");
        Assert.Contains(faults, f => f.Code == "mapping.invalid-column");
        Assert.Contains(faults, f => f.Code == "mapping.required-field-unmapped");
        Assert.Contains(faults, f => f.Code == "mapping.invalid-header-row");
        Assert.Contains(faults, f => f.Code == "mapping.invalid-sheet");
    }
}
