using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// A number grouped with an apostrophe — <c>1'239.09</c>, as Swiss formats write it — is a number under a
/// culture that groups that way, in the profile and the import alike, whichever apostrophe it is typed
/// with (#126).
/// </summary>
/// <remarks>
/// The profile's filter let no apostrophe through, so <c>de-CH</c> in the tried cultures changed nothing
/// for exactly the numbers it writes, while the import read them. And which apostrophe <c>de-CH</c> groups
/// with depends on the runtime's ICU data — <c>'</c> on one system, <c>’</c> on another — so each value
/// here is tried with both: one of the two is always the other one.
/// </remarks>
public sealed class ApostropheGroupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DecimalImportField Amount = ImportField.Decimal("amount");

    private static readonly IntegerImportField Count = ImportField.Integer("count");

    public static TheoryData<char> Apostrophes => ['\'', '’'];

    private static CsvCursor Csv(IEnumerable<string> values) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes("v\n" + string.Concat(values.Select(v => v + "\n")))), "t.csv");

    private static string[] Grouped(char apostrophe, params string[] values) => [.. values.Select(v => v.Replace('\'', apostrophe))];

    private static ColumnProfile Profile(IEnumerable<string> values, params string[] cultures)
    {
        using CsvCursor cursor = Csv(values);
        return TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = cultures }, cancellationToken: Token).Sheets[0].Columns[0];
    }

    private static ImportResult<T> Import<T>(IEnumerable<string> values, ImportField field, TabularRowMapper<T> map)
    {
        using CsvCursor cursor = Csv(values);
        using ImportRun<T> run = TabularImporter.Import(
            cursor,
            new MappingPlan { Culture = "de-CH", Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = field.Name, Header = "v" }] },
            new ImportSchema { Fields = [field] },
            map,
            cancellationToken: Token);

        return run.ReadAll(cancellationToken: Token);
    }

    [Theory]
    [MemberData(nameof(Apostrophes))]
    public void ProfilesApostropheGroupedDecimalsAsDecimalUnderASwissCulture(char apostrophe)
    {
        ColumnProfile column = Profile(Grouped(apostrophe, "1'239.09", "1'234'567.89", "-2'345.5"), "", "de-DE", "en-US", "de-CH");

        TypeHypothesis best = column.Hypotheses[0];
        Assert.Equal(ColumnType.Decimal, best.Type);
        Assert.Equal("de-CH", best.Culture);
        Assert.Equal(1.0, best.Confidence);
        Assert.Equal(-2345.5m, column.Facts.MinNumeric);
        Assert.Equal(1234567.89m, column.Facts.MaxNumeric);
    }

    [Theory]
    [MemberData(nameof(Apostrophes))]
    public void ProfilesApostropheGroupedWholeNumbersAsIntegers(char apostrophe)
    {
        ColumnProfile column = Profile(Grouped(apostrophe, "1'234", "12'345'678"), "de-CH");

        Assert.Equal(ColumnType.Integer, column.Hypotheses[0].Type);
        Assert.Equal(12345678m, column.Facts.MaxNumeric);
    }

    [Theory]
    [MemberData(nameof(Apostrophes))]
    public void ImportsApostropheGroupedDecimalsAsTheProfileProposed(char apostrophe)
    {
        ImportResult<decimal?> result = Import(Grouped(apostrophe, "1'239.09", "1'234'567.89", "-2'345.5"), Amount, row => row[Amount]);

        Assert.Empty(result.Errors);
        Assert.Equal([1239.09m, 1234567.89m, -2345.5m], result.Items);
    }

    [Theory]
    [MemberData(nameof(Apostrophes))]
    public void ImportsApostropheGroupedIntegers(char apostrophe)
    {
        ImportResult<long?> result = Import(Grouped(apostrophe, "1'234", "12'345'678"), Count, row => row[Count]);

        Assert.Empty(result.Errors);
        Assert.Equal([1234L, 12345678L], result.Items);
    }

    [Theory]
    [MemberData(nameof(Apostrophes))]
    public void HoldsAnApostropheGroupToThreeDigits(char apostrophe)
    {
        // The grouping rule every separator answers to: 12'34 is no number, under either apostrophe.
        ColumnProfile column = Profile(Grouped(apostrophe, "1'234.5", "12'34.5"), "de-CH");

        CultureParseCounts swiss = Assert.Single(column.Facts.ParseCounts);
        Assert.Equal(1, swiss.Decimal);
        Assert.Equal(Grouped(apostrophe, "12'34.5")[0], Assert.Single(swiss.NumericOutliers).RawValue);
    }

    [Theory]
    [MemberData(nameof(Apostrophes))]
    public void LeavesApostropheGroupsTextUnderCulturesThatDoNotGroupSo(char apostrophe)
    {
        ColumnProfile column = Profile(Grouped(apostrophe, "1'239.09", "1'234"), "", "de-DE", "en-US");

        Assert.Equal(ColumnType.Text, column.Hypotheses[0].Type);
        Assert.All(column.Facts.ParseCounts, counts => Assert.Equal(0, counts.Integer + counts.Decimal));
    }
}
