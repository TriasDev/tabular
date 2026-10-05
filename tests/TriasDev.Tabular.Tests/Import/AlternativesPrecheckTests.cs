using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>What the mapping alone says about a set of alternatives, before any row is read again.</summary>
public sealed class AlternativesPrecheckTests
{
    private const string Csv = "lat;lon;country;city;house\n48.1;11.5;XX;Munich;5\n";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");
    private static readonly DecimalImportField Lon = ImportField.Decimal("lon");
    private static readonly TextImportField Country = ImportField.Text("country").AllowedValues(["DEU"]);
    private static readonly TextImportField City = ImportField.Text("city");
    private static readonly TextImportField House = ImportField.Text("house");

    [Fact]
    public void WarnsWhereTheMappingEndsALadderEarly()
    {
        PrecheckFinding finding = Assert.Single(Check(Schema(), "lat", "lon", "country", "house").Findings, f => f.Code == "group.level-unmapped");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.Equal("address", finding.FieldName);
        Assert.Equal("locality", finding.Arguments["level"]);
        Assert.Equal("1", finding.Arguments["reachableLevel"]);
        Assert.Equal("location", finding.Arguments["alternatives"]);
        Assert.Equal("address", finding.Arguments["group"]);
    }

    [Fact]
    public void SaysNothingAboutAFullyMappedSet()
    {
        PrecheckResult result = Check(Schema(), "lat", "lon", "country", "city", "house");

        Assert.DoesNotContain(result.Findings, f => f.Code.StartsWith("group.", StringComparison.Ordinal));
    }

    [Fact]
    public void WarnsWhenNoGroupCanLocateARow()
    {
        PrecheckResult result = Check(Schema(), "lat", "city");
        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "group.unresolved");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.Equal("location", finding.FieldName);
        Assert.Equal(1, finding.AffectedRows);
        Assert.True(result.CanImport);
    }

    [Fact]
    public void BlocksWhenNoGroupCanLocateARowAndThatFailsIt()
    {
        PrecheckResult result = Check(Schema(unresolvedRowFails: true), "lat", "city");

        Assert.Equal(PrecheckSeverity.Blocking, Assert.Single(result.Findings, f => f.Code == "group.unresolved").Severity);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void NeverBlocksOnAFieldOfALaterGroup()
    {
        // Every country is wrong — which blocks a plain field — but only rows without coordinates need
        // one, and the profile cannot say which those are.
        PrecheckFinding finding = Assert.Single(Check(Schema(), "lat", "lon", "country", "city", "house").Findings, f => f.Code == "value.not-allowed");

        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void StillBlocksOnTheSameFieldOutsideAnySet()
    {
        // The control for the test above: the same column and rule, no alternatives.
        ImportSchema plain = new() { Fields = [Lat, Lon, Country, City, House] };

        PrecheckFinding finding = Assert.Single(Check(plain, "country").Findings, f => f.Code == "value.not-allowed");

        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
    }

    private static ImportSchema Schema(bool unresolvedRowFails = false) => new()
    {
        Fields = [Lat, Lon, Country, City, House],
        Alternatives =
        [
            new FieldAlternatives(
                "location",
                [
                    AlternativeGroup.AllOf("coordinates", Lat, Lon),
                    AlternativeGroup.Ladder("address", 1, new AlternativeLevel("country", Country), new AlternativeLevel("locality", City), new AlternativeLevel("house", House)),
                ],
                unresolvedRowFails),
        ],
    };

    private static PrecheckResult Check(ImportSchema schema, params string[] mapped)
    {
        string[] headers = ["lat", "lon", "country", "city", "house"];
        MappingPlan plan = new()
        {
            Bindings =
            [
                .. headers.Select((h, i) => (Header: h, Index: i)).Where(c => mapped.Contains(c.Header))
                    .Select(c => new ColumnBinding { ColumnIndex = c.Index, Header = c.Header, FieldName = c.Header }),
            ],
        };

        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(Csv), writable: false), "test.csv");
        return MappingPrecheck.Check(plan, schema, TabularAnalyzer.Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken));
    }
}
