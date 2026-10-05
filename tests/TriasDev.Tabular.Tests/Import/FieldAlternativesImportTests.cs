using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>A mapper reads which group locates its row; the run carries the report.</summary>
public sealed class FieldAlternativesImportTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country");

    private static readonly FieldAlternatives Location = new(
        "location",
        [AlternativeGroup.AllOf("coordinates", Lat, Lon), AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country))]);

    private static ImportSchema Schema => new() { Fields = [Lat, Lon, Country], Alternatives = [Location] };

    [Fact]
    public void HandsTheMapperTheWinningGroup()
    {
        using ImportRun<string?> run = Run("lat;lon;country\n48.1;11.5;\n;;DEU\n", row => row.Resolution(Location).Group);

        Assert.Equal(["coordinates", "address"], run.ReadAll(cancellationToken: TestContext.Current.CancellationToken).Items);
    }

    [Fact]
    public void CarriesTheReportOnTheRun()
    {
        // A latitude alone locates nothing. (A row with every mapped cell empty is not judged at all:
        // the run skips it as padding.)
        using ImportRun<int> run = Run("lat;lon;country\n48.1;11.5;\n48.1;;\n", row => row.RowNumber);

        run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

        AlternativesReport report = Assert.Single(run.Alternatives);
        Assert.Equal(1, report.Unresolved);
    }

    [Fact]
    public void RefusesASetTheSchemaDoesNotDeclare()
    {
        FieldAlternatives other = new("other", [AlternativeGroup.AllOf("x", Lat)]);
        using ImportRun<int> run = Run("lat;lon;country\n48.1;11.5;\n", row => row.Resolution(other).Level);

        Assert.Throws<ArgumentException>(() => run.ReadAll(cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ImportRun<T> Run<T>(string csv, TabularRowMapper<T> mapper) =>
        TabularImporter.Import(
            new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false),
            "test.csv",
            new MappingPlan
            {
                Bindings =
                [
                    new ColumnBinding { ColumnIndex = 0, Header = "lat", FieldName = "lat" },
                    new ColumnBinding { ColumnIndex = 1, Header = "lon", FieldName = "lon" },
                    new ColumnBinding { ColumnIndex = 2, Header = "country", FieldName = "country" },
                ],
            },
            Schema,
            mapper,
            cancellationToken: TestContext.Current.CancellationToken);
}
