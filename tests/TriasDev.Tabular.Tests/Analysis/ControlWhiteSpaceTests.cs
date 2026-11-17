using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// A tab, line feed, vertical tab, form feed or carriage return between a decimal and its trailing
/// sign is white space to the import's number parsing, so the profile counts such a value as a number
/// too, and the precheck does not refuse a plan for values that import (#154, A-G18).
/// </summary>
/// <remarks>
/// Leading and trailing white space never gets this far: every value is trimmed as it is read. Inside
/// a value it does: the decimal styles take white space before a trailing sign. The profile's quick
/// filter let only the space and the two non-breaking spaces through, so <c>12&lt;TAB&gt;-</c> was text to the profile and the precheck while a decimal field
/// imported it as -12. Integers take no trailing sign, so they were never affected.
/// </remarks>
public sealed class ControlWhiteSpaceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DecimalImportField Amount = ImportField.Decimal("amount");

    public static TheoryData<char> Whites => ['\t', '\n', '\v', '\f', '\r'];

    private static CsvCursor Csv(char white) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes($"v\n\"12{white}-\"\n3\n")), "t.csv",
            new CsvCursorOptions { Delimiter = ';' });

    [Theory]
    [MemberData(nameof(Whites))]
    public void ProfilesANumberWithControlWhiteSpaceBeforeItsTrailingSignAsANumber(char white)
    {
        using CsvCursor cursor = Csv(white);
        ColumnProfile column = TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["en-US"] }, cancellationToken: Token)
            .Sheets[0].Columns[0];

        CultureParseCounts us = Assert.Single(column.Facts.ParseCounts);
        Assert.Equal((1, 1), (us.Integer, us.Decimal));
        Assert.Empty(us.NumericOutliers);
        Assert.Equal(-12m, column.Facts.MinNumeric);
    }

    [Theory]
    [MemberData(nameof(Whites))]
    public void ImportsTheValueTheProfileCounted(char white)
    {
        using CsvCursor cursor = Csv(white);
        using ImportRun<decimal?> run = TabularImporter.Import(
            cursor,
            new MappingPlan { Culture = "en-US", Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = Amount.Name, Header = "v" }] },
            new ImportSchema { Fields = [Amount] },
            row => row[Amount],
            cancellationToken: Token);

        ImportResult<decimal?> result = run.ReadAll(cancellationToken: Token);

        Assert.Empty(result.Errors);
        Assert.Equal([-12m, 3m], result.Items);
    }
}
