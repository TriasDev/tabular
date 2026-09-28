using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// A number written in exponential notation is a decimal, read the same by the profile and the import,
/// from a csv and from an xlsx text cell alike (#44).
/// </summary>
/// <remarks>
/// A naive <c>double.ToString()</c> writes small values this way: 21 of some 98,000 longitudes in a
/// real export were <c>-6.9345e-03</c>, enough to take the column below full confidence and fail those
/// rows on import. Such a value counts as a decimal only, never as an integer, even when it is whole:
/// <c>1E+5</c> is a measurement, and a column of them is not a column of counts.
/// </remarks>
public sealed class ExponentNotationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Longitudes = ["11.5", "-6.9345e-03", "5.4176E-03", "1E+5", "-2.5e2"];

    private static readonly decimal[] Read = [11.5m, -0.0069345m, 0.0054176m, 100000m, -250m];

    private static CsvCursor Csv(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), "t.csv");

    private static string Column(string header, IEnumerable<string> values) => header + "\n" + string.Concat(values.Select(v => v + "\n"));

    private static XlsxCursor XlsxOfTextCells(string header, IEnumerable<string> values)
    {
        string rows = string.Concat(new[] { header }.Concat(values).Select((v, i) =>
            $"""<row r="{i + 1}"><c r="A{i + 1}" t="inlineStr"><is><t>{v}</t></is></c></row>"""));

        return new XlsxCursor(new MemoryStream(new XlsxPackage().WithSheet("S", rows).Build()), cancellationToken: Token);
    }

    public static TheoryData<string> Formats => ["csv", "xlsx"];

    private static ITabularCursor Open(string format, string header, IEnumerable<string> values) =>
        format == "csv" ? Csv(Column(header, values)) : XlsxOfTextCells(header, values);

    [Theory]
    [MemberData(nameof(Formats))]
    public void ProfilesAColumnWithExponentsAsDecimalAtFullConfidence(string format)
    {
        using ITabularCursor cursor = Open(format, "lon", Longitudes);

        ColumnProfile column = TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["en-US"] }, cancellationToken: Token).Sheets[0].Columns[0];

        TypeHypothesis best = column.Hypotheses[0];
        Assert.Equal(ColumnType.Decimal, best.Type);
        Assert.Equal(1.0, best.Confidence);
        Assert.Equal(-250m, column.Facts.MinNumeric);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ImportsExponentsIntoADecimalFieldAsTheProfileProposed(string format)
    {
        DecimalImportField lon = ImportField.Decimal("lon");
        using ITabularCursor cursor = Open(format, "lon", Longitudes);

        ImportResult<decimal?> result = TabularImporter.Import(
            cursor,
            new MappingPlan { Culture = "en-US", Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "lon", Header = "lon" }] },
            new ImportSchema { Fields = [lon] },
            row => row[lon],
            cancellationToken: Token).ReadAll(cancellationToken: Token);

        Assert.Empty(result.Errors);
        Assert.Equal(Read.Cast<decimal?>(), result.Items);
    }

    [Fact]
    public void CountsAWholeValueInExponentsAsADecimalNotAnInteger()
    {
        using CsvCursor cursor = Csv(Column("n", ["1E+5", "2e3", "7E0"]));

        ColumnProfile column = TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["en-US"] }, cancellationToken: Token).Sheets[0].Columns[0];

        Assert.Equal(ColumnType.Decimal, column.Hypotheses[0].Type);
        Assert.DoesNotContain(column.Hypotheses, h => h.Type == ColumnType.Integer);
    }

    [Fact]
    public void ReadsExponentsUnderACommaCultureOnlyWithItsOwnSeparator()
    {
        // Under de-DE "1,5e-3" is a decimal; "1.5e-3" is neither that nor a grouped number.
        using CsvCursor cursor = Csv(Column("n", ["1,5e-3", "2,25E2", "1.5e-3"]));

        ColumnFacts facts = TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["de-DE"] }, cancellationToken: Token).Sheets[0].Columns[0].Facts;
        CultureParseCounts german = facts.ParseCounts.Single(c => c.Culture == "de-DE");

        Assert.Equal(2, german.Decimal);
        Assert.Equal("1.5e-3", Assert.Single(german.NumericOutliers).RawValue);
        Assert.Equal(0.0015m, facts.MinNumeric);
        Assert.Equal(225m, facts.MaxNumeric);
    }

    [Fact]
    public void StillReadsAGroupedNumber()
    {
        // Why the reading is Number with exponents added rather than Float: Float drops thousands.
        using CsvCursor cursor = Csv(Column("n", ["\"1,234.56\"", "\"12,345,678\""]));

        ColumnFacts facts = TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["en-US"] }, cancellationToken: Token).Sheets[0].Columns[0].Facts;

        Assert.Equal(1234.56m, facts.MinNumeric);
        Assert.Equal(12345678m, facts.MaxNumeric);
        Assert.Empty(facts.ParseCounts.Single(c => c.Culture == "en-US").NumericOutliers);
    }
}
