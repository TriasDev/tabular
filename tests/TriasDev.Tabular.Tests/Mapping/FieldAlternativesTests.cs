using Xunit;

namespace TriasDev.Tabular.Tests.Mapping;

/// <summary>
/// Groups of fields in priority order: a row needs one usable group, and a later group matters only
/// where the earlier ones fell short. The declaration's own rules are programmer errors.
/// </summary>
public sealed class FieldAlternativesTests
{
    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country");
    private static readonly TextImportField City = ImportField.Text("city");

    private static MappingPlan NoBindings => new() { Bindings = [] };

    private static FieldAlternatives Location() => new(
        "location",
        [
            AlternativeGroup.AllOf("coordinates", Lat, Lon),
            AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country), new AlternativeLevel("locality", City)),
        ]);

    [Fact]
    public void AllOfMakesOneLevelPerFieldAndNeedsThemAll()
    {
        AlternativeGroup group = AlternativeGroup.AllOf("coordinates", Lat, Lon);

        Assert.Equal(["lat", "lon"], group.Levels.Select(l => l.Name));
        Assert.Equal(2, group.RequiredLevels);
    }

    [Fact]
    public void AcceptsASchemaThatDeclaresEveryField()
    {
        ImportSchema schema = new() { Fields = [Lat, Lon, Country, City], Alternatives = [Location()] };

        Assert.Empty(MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Fact]
    public void RefusesAFieldTheSchemaDoesNotDeclare()
    {
        ImportSchema schema = new() { Fields = [Lat, Lon, Country], Alternatives = [Location()] };

        ArgumentException error = Assert.Throws<ArgumentException>(() => MappingPlanValidator.Validate(NoBindings, schema));
        Assert.Contains("'city'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesARequiredField()
    {
        // Required says every row carries it; an alternative says a row may do without it. One field
        // cannot mean both.
        ImportSchema schema = new() { Fields = [Lat, Lon, Country.Require(), City], Alternatives = [Location()] };

        Assert.Throws<ArgumentException>(() => MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Fact]
    public void RefusesAFieldInTwoGroups()
    {
        FieldAlternatives twice = new(
            "location",
            [AlternativeGroup.AllOf("a", Lat), AlternativeGroup.AllOf("b", Lat)]);

        ImportSchema schema = new() { Fields = [Lat], Alternatives = [twice] };

        Assert.Throws<ArgumentException>(() => MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Fact]
    public void AllowsOneFieldInTwoLevelsOfOneGroup()
    {
        // A column holding "Main Street 5" satisfies the street and the house number at once.
        TextImportField streetAndHouse = ImportField.Text("streetAndHouse");
        FieldAlternatives address = new(
            "location",
            [AlternativeGroup.Ladder("address", 1, new AlternativeLevel("street", streetAndHouse), new AlternativeLevel("house", streetAndHouse))]);

        ImportSchema schema = new() { Fields = [streetAndHouse], Alternatives = [address] };

        Assert.Empty(MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Fact]
    public void RefusesTwoSetsWithOneName()
    {
        ImportSchema schema = new()
        {
            Fields = [Lat, Lon],
            Alternatives = [new("x", [AlternativeGroup.AllOf("a", Lat)]), new("x", [AlternativeGroup.AllOf("b", Lon)])],
        };

        Assert.Throws<ArgumentException>(() => MappingPlanValidator.Validate(NoBindings, schema));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void RefusesRequiredLevelsOutsideTheLadder(int required) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AlternativeGroup.Ladder("address", required, new AlternativeLevel("country", Country), new AlternativeLevel("locality", City)));

    [Fact]
    public void RefusesEmptyDeclarations()
    {
        Assert.Throws<ArgumentException>(() => new FieldAlternatives("location", []));
        Assert.Throws<ArgumentException>(() => AlternativeGroup.AllOf("coordinates"));
        Assert.Throws<ArgumentException>(() => new AlternativeLevel("country"));
        Assert.Throws<ArgumentException>(() => new FieldAlternatives("location", [AlternativeGroup.AllOf("a", Lat), AlternativeGroup.AllOf("a", Lon)]));
    }
}
