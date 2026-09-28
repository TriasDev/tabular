using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// A value that writes the decimal separator the other way, and can only be read that way, is counted
/// by the profile and — only when a binding asks for it — imported under its own separator (#47).
/// </summary>
/// <remarks>
/// Hand-assembled files mix sources: one <c>34.020367</c> among German decimals cannot be a German
/// grouped number, a group having three digits. Off by default, as everything here proposes and a
/// person disposes; counted in the profile and the run, so the leniency is never silent.
/// </remarks>
public sealed class OtherDecimalSeparatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Latitudes = ["48,183604", "48,860794", "45,85339", "35,332261", "34.020367"];

    private static readonly DecimalImportField Lat = ImportField.Decimal("lat");

    private static CsvCursor Csv(IEnumerable<string> values) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes("id;lat\n" + string.Concat(values.Select((v, i) => $"{i + 1};{v}\n")))), "t.csv");

    private static MappingPlan Plan(bool accept) => new()
    {
        Culture = "de-DE",
        Bindings = [new ColumnBinding { ColumnIndex = 1, FieldName = "lat", Header = "lat", AcceptOtherDecimalSeparator = accept }],
    };

    private static ImportSchema Schema => new() { Fields = [Lat] };

    private static FileProfile Profile(IEnumerable<string> values)
    {
        using CsvCursor cursor = Csv(values);
        return TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["de-DE", "en-US"] }, cancellationToken: Token);
    }

    [Fact]
    public void CountsAValueThatCanOnlyBeReadUnderTheOtherSeparator()
    {
        ColumnFacts facts = Profile(Latitudes).Sheets[0].Columns[1].Facts;
        CultureParseCounts german = facts.ParseCounts.Single(c => c.Culture == "de-DE");
        CultureParseCounts english = facts.ParseCounts.Single(c => c.Culture == "en-US");

        Assert.Equal(4, german.Decimal);
        Assert.Equal(1, german.OtherSeparatorDecimals);
        Assert.Equal("34.020367", Assert.Single(german.NumericOutliers).RawValue);

        // Seen from the other side, the four German values are the ones written the other way.
        Assert.Equal(4, english.OtherSeparatorDecimals);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.5e3")]
    [InlineData("1.234,5")]
    [InlineData("12. 5")]
    [InlineData(".")]
    public void DoesNotCountAValueThatIsNotUnambiguous(string value)
    {
        CultureParseCounts german = Profile(["1,5", value]).Sheets[0].Columns[1].Facts.ParseCounts.Single(c => c.Culture == "de-DE");

        Assert.Equal(0, german.OtherSeparatorDecimals);
    }

    [Fact]
    public void ImportsSuchAValueWhenTheBindingAcceptsIt()
    {
        using CsvCursor cursor = Csv(Latitudes);
        using ImportRun<decimal?> run = TabularImporter.Import(cursor, Plan(accept: true), Schema, row => row[Lat], cancellationToken: Token);

        ImportResult<decimal?> result = run.ReadAll(cancellationToken: Token);

        Assert.Empty(result.Errors);
        Assert.Equal([48.183604m, 48.860794m, 45.85339m, 35.332261m, 34.020367m], result.Items);
        Assert.Equal(1, result.Summary.OtherSeparatorDecimals);
    }

    [Fact]
    public void RefusesSuchAValueByDefault()
    {
        using CsvCursor cursor = Csv(Latitudes);
        using ImportRun<decimal?> run = TabularImporter.Import(cursor, Plan(accept: false), Schema, row => row[Lat], cancellationToken: Token);

        ImportResult<decimal?> result = run.ReadAll(cancellationToken: Token);

        Assert.Equal(ErrorCodes.Value.TypeMismatch, Assert.Single(result.Errors).Code);
        Assert.Equal(0, result.Summary.OtherSeparatorDecimals);
    }

    [Fact]
    public void JudgesThePlanAsTheImportWillRunIt()
    {
        FileProfile profile = Profile(Latitudes);

        Assert.DoesNotContain(MappingPrecheck.Check(Plan(accept: true), Schema, profile).Findings, f => f.Code == ErrorCodes.Value.TypeMismatch);
        Assert.Contains(MappingPrecheck.Check(Plan(accept: false), Schema, profile).Findings, f => f.Code == ErrorCodes.Value.TypeMismatch);
    }
}
