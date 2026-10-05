using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>What a run counts about its alternatives: the numbers a review screen shows.</summary>
public sealed class AlternativesReportTests
{
    private const string Header = "id;lat;lon;country;city;street\n";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextImportField Id = ImportField.Text("id").Require();
    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country").AllowedValues(["DEU"]);
    private static readonly TextImportField City = ImportField.Text("city");
    private static readonly TextImportField Street = ImportField.Text("street");

    private static ImportSchema Schema => new()
    {
        Fields = [Id, Lat, Lon, Country, City, Street],
        Alternatives =
        [
            new FieldAlternatives(
                "location",
                [
                    AlternativeGroup.AllOf("coordinates", Lat, Lon),
                    AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country), new AlternativeLevel("locality", City), new AlternativeLevel("street", Street)),
                ]),
        ],
    };

    [Fact]
    public void CountsEachGroupWhereItIsNeeded()
    {
        // Spreadsheet rows 2 to 5: the header is row 1.
        AlternativesReport report = Report(
            "1;48.1;11.5;;;\n"              // row 2: coordinates
            + "2;48.1;;DEU;Munich;Main\n"   // row 3: partial coordinates, full address
            + "3;;;DEU;;\n"                 // row 4: country only
            + "4;;;;Munich;\n");            // row 5: nothing usable

        Assert.Equal(4, report.RowsJudged);

        AlternativeGroupReport coordinates = report.Groups[0];
        Assert.Equal(4, coordinates.RowsNeeding);
        Assert.Equal(1, coordinates.Won);
        Assert.Equal(3, coordinates.Incomplete);
        Assert.Equal(2, coordinates.Empty);
        Assert.Equal([3, 4, 5], coordinates.IncompleteRows);

        AlternativeGroupReport address = report.Groups[1];
        Assert.Equal(3, address.RowsNeeding);              // rows 3, 4, 5: no usable coordinates
        Assert.Equal(2, address.Won);
        Assert.Equal(3, address.MaxReachableLevel);
        Assert.Equal([1, 1, 0, 1], address.RowsAtLevel);   // row 5 at 0, row 4 at 1, row 3 at 3
        Assert.Equal(2, address.Incomplete);
        Assert.Equal([4, 5], address.IncompleteRows);

        Assert.Equal(1, report.Unresolved);
        Assert.Equal([5], report.UnresolvedRows);
        Assert.True(report.UnresolvedRowsComplete);
    }

    [Fact]
    public void CountsInvalidValuesItDidNotNeedToJudge()
    {
        AlternativesReport report = Report("1;48.1;11.5;XX;;\n");

        Assert.Equal(1, report.Groups[1].IgnoredInvalidValues);
        Assert.Equal(0, report.Groups[1].RowsNeeding);
    }

    [Fact]
    public void CountsOnlyRowsThatProduceValues()
    {
        // Row 3 fails on its latitude and is not imported, so it is in the errors, not in the report.
        AlternativesReport report = Report("1;48.1;11.5;;;\n2;abc;11.5;;;\n");

        Assert.Equal(1, report.RowsJudged);
        Assert.Equal(1, report.Groups[0].Won);
    }

    [Fact]
    public void CapsTheRowNumbersItKeeps()
    {
        // Five rows with a city and nothing else, at spreadsheet rows 2 to 6.
        string data = string.Concat(Enumerable.Range(1, 5).Select(i => $"{i};;;;x;\n"));
        AlternativesReport report = Report(data, new ExtractionOptions { MaxReportedRows = 2 });

        Assert.Equal(5, report.Unresolved);
        Assert.Equal([2, 3], report.UnresolvedRows);
        Assert.False(report.UnresolvedRowsComplete);
    }

    [Fact]
    public void ReportsNothingForASchemaWithoutAlternatives()
    {
        using ITabularCursor cursor = Open("id\n1\n");
        ExtractionRun run = TabularExtractor.Extract(
            cursor,
            new MappingPlan { Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "id", FieldName = "id" }] },
            new ImportSchema { Fields = [Id] },
            cancellationToken: TestContext.Current.CancellationToken);

        Drain(run);

        Assert.Empty(run.Alternatives);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void RefusesARowCapOutsideItsRange(int cap)
    {
        using ITabularCursor cursor = Open(Header + "1;;;;;\n");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TabularExtractor.Extract(cursor, Plan(), Schema, new ExtractionOptions { MaxReportedRows = cap }, TestContext.Current.CancellationToken));
    }

    private static AlternativesReport Report(string data, ExtractionOptions? options = null)
    {
        using ITabularCursor cursor = Open(Header + data);
        ExtractionRun run = TabularExtractor.Extract(cursor, Plan(), Schema, options, TestContext.Current.CancellationToken);

        Drain(run);

        return Assert.Single(run.Alternatives);
    }

    private static void Drain(ExtractionRun run)
    {
        int rows = 0;

        while (run.ReadRow(TestContext.Current.CancellationToken))
        {
            rows++;
        }

        Assert.True(rows > 0);
    }

    private static MappingPlan Plan() => new()
    {
        Bindings =
        [
            .. new[] { "id", "lat", "lon", "country", "city", "street" }
                .Select((name, i) => new ColumnBinding { ColumnIndex = i, Header = name, FieldName = name }),
        ],
    };

    private static ITabularCursor Open(string csv) =>
        TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv");
}
