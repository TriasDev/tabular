using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>
/// Coordinates first, an address where a row has none: which group locates each row, and how a later
/// group's values are validated only where it is needed.
/// </summary>
public sealed class FieldAlternativesExtractionTests
{
    private const string Header = "id;lat;lon;country;city;street;streetAndHouse\n";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextImportField Id = ImportField.Text("id").Require();
    private static readonly DecimalImportField Lat = ImportField.Decimal("lat").AtLeast(-90).AtMost(90);
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon").AtLeast(-180).AtMost(180);
    private static readonly TextImportField Country = ImportField.Text("country").AllowedValues(["DEU", "FRA"], ignoreCase: true);
    private static readonly TextImportField City = ImportField.Text("city");
    private static readonly TextImportField Street = ImportField.Text("street");
    private static readonly TextImportField StreetAndHouse = ImportField.Text("streetAndHouse");

    [Fact]
    public void CoordinatesWinWhereBothArePresent()
    {
        AlternativeResolution resolution = Single("1;48.1;11.5;;;;\n").Resolution;

        Assert.True(resolution.IsResolved);
        Assert.Equal("coordinates", resolution.Group);
        Assert.Equal(0, resolution.GroupIndex);
        Assert.Equal(2, resolution.Level);
        Assert.Equal("lon", resolution.LevelName);
    }

    [Fact]
    public void FallsBackToTheAddressWhereACoordinateIsMissing()
    {
        AlternativeResolution resolution = Single("1;48.1;;DEU;Munich;;\n").Resolution;

        Assert.Equal("address", resolution.Group);
        Assert.Equal(2, resolution.Level);
        Assert.Equal("locality", resolution.LevelName);
    }

    [Fact]
    public void AGapEndsTheLadder()
    {
        // A street without its city locates nothing finer than the country.
        Assert.Equal(1, Single("1;;;DEU;;Main Street;\n").Resolution.Level);
    }

    [Fact]
    public void OneFieldCanSatisfyTwoLevels()
    {
        Assert.Equal(4, Single("1;;;DEU;Munich;;Main Street 5\n").Resolution.Level);
    }

    [Fact]
    public void KeepsAnInvalidAddressWhereCoordinatesWon()
    {
        // The address is not needed here, so it is not judged — and it is kept, because a caller may
        // store it even when it locates nothing.
        Row row = Single("1;48.1;11.5;XX;;;\n");

        Assert.Empty(row.Errors);
        Assert.Equal("XX", row.CountryText);
    }

    [Fact]
    public void RefusesAnInvalidAddressWhereItIsNeeded()
    {
        RowError error = Assert.Single(Single("1;;;XX;Munich;;\n").Errors);

        Assert.Equal("value.not-allowed", error.Code);
        Assert.Equal("country", error.FieldName);
    }

    [Fact]
    public void RefusesAnInvalidCoordinateEvenWithAGoodAddress()
    {
        // The first group is always judged: "abc" cannot be stored as a latitude.
        RowError error = Assert.Single(Single("1;abc;11.5;DEU;Munich;;\n").Errors);

        Assert.Equal("value.type-mismatch", error.Code);
        Assert.Equal("lat", error.FieldName);
    }

    [Fact]
    public void AnOutOfRangeCoordinateDoesNotLocateTheRow()
    {
        // 91 reads as a number but breaks the latitude's range, so it locates nothing: the address is
        // needed, and its own fault is reported in the same pass rather than on the next upload.
        RowError[] errors = Single("1;91;11.5;XX;Munich;;\n").Errors;

        Assert.Equal(["lat:value.out-of-range", "country:value.not-allowed"], errors.Select(e => $"{e.FieldName}:{e.Code}"));
    }

    [Fact]
    public void AnOutOfRangeCoordinateLeavesARowUnresolved()
    {
        RowError[] errors = Single("1;91;11.5;;;;\n", unresolvedRowFails: true).Errors;

        Assert.Equal(["lat:value.out-of-range", "location:group.unresolved"], errors.Select(e => $"{e.FieldName}:{e.Code}"));
    }

    [Fact]
    public void ImportsARowNoGroupLocatesByDefault()
    {
        Row row = Single("1;;;;Munich;;\n");

        Assert.Empty(row.Errors);
        Assert.False(row.Resolution.IsResolved);
        Assert.Equal(-1, row.Resolution.GroupIndex);
    }

    [Fact]
    public void RefusesARowNoGroupLocatesWhenTheSetSaysSo()
    {
        RowError error = Assert.Single(Single("1;;;;Munich;;\n", unresolvedRowFails: true).Errors);

        Assert.Equal("group.unresolved", error.Code);
        Assert.Equal("location", error.FieldName);
    }

    private static ImportSchema Schema(bool unresolvedRowFails = false) => new()
    {
        Fields = [Id, Lat, Lon, Country, City, Street, StreetAndHouse],
        Alternatives =
        [
            new FieldAlternatives(
                "location",
                [
                    AlternativeGroup.AllOf("coordinates", Lat, Lon),
                    AlternativeGroup.Ladder(
                        "address",
                        1,
                        new AlternativeLevel("country", Country),
                        new AlternativeLevel("locality", City),
                        new AlternativeLevel("street", Street, StreetAndHouse),
                        new AlternativeLevel("house", StreetAndHouse)),
                ],
                unresolvedRowFails),
        ],
    };

    private static MappingPlan Plan() => new()
    {
        Bindings =
        [
            .. new[] { "id", "lat", "lon", "country", "city", "street", "streetAndHouse" }
                .Select((name, i) => new ColumnBinding { ColumnIndex = i, Header = name, FieldName = name }),
        ],
    };

    private static Row Single(string data, bool unresolvedRowFails = false)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(Header + data), writable: false), "test.csv");
        ExtractionRun run = TabularExtractor.Extract(cursor, Plan(), Schema(unresolvedRowFails));

        Assert.True(run.ReadRow(TestContext.Current.CancellationToken));

        Row row = new(
            [.. run.CurrentErrors],
            run.CurrentRowHasErrors ? default : run.CurrentResolutions[0],
            run.CurrentRowHasErrors ? null : run.CurrentValues[3].Text);

        Assert.False(run.ReadRow(TestContext.Current.CancellationToken));
        return row;
    }

    private sealed record Row(RowError[] Errors, AlternativeResolution Resolution, string? CountryText);
}
