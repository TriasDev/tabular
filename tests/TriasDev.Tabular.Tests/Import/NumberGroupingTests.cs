using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// A group separator is followed by exactly three digits — in the import as in the profile.
/// </summary>
public sealed class NumberGroupingTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static List<ImportOutcome<decimal?>> Import(TargetField field, string culture, params string[] values)
    {
        // A second column fixes the delimiter as ';', so "1,5" is one value, not two columns.
        string csv = "value;other\n" + string.Join('\n', values.Select(v => v + ";x")) + "\n";
        using CsvCursor cursor = new(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv");

        using ImportRun<decimal?> run = TabularImporter.Import(
            cursor,
            new MappingPlan
            {
                Culture = culture,
                Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "value", TargetFieldName = field.Name }],
            },
            new TargetSchema { Fields = [field] },
            row => field switch
            {
                DecimalField d => row[d],
                IntegerField i => row[i],
                _ => null,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        return [.. run];
    }

    [Fact]
    public void RefusesAnAmericanDecimalUnderAGermanPlanInsteadOfReadingItAsThousands()
    {
        // .NET does not check group sizes: under de-DE "19.99" parses as 1999. The profiler refused
        // it and the precheck blocked it, while the import wrote 1999 — a silently wrong amount.
        List<ImportOutcome<decimal?>> outcomes = Import(ImportField.Decimal("amount"), "de-DE", "19.99", "1.234,50", "7,5");

        Assert.Equal("value.type-mismatch", Assert.Single(outcomes[0].Errors).Code);
        Assert.Equal(1234.50m, outcomes[1].Value);
        Assert.Equal(7.5m, outcomes[2].Value);
    }

    [Fact]
    public void RefusesAMisgroupedWholeNumber()
    {
        List<ImportOutcome<decimal?>> outcomes = Import(ImportField.Integer("count"), "de-DE", "1.5", "1.500", "12.345.678");

        Assert.Equal("value.type-mismatch", Assert.Single(outcomes[0].Errors).Code);
        Assert.Equal(1500m, outcomes[1].Value);
        Assert.Equal(12_345_678m, outcomes[2].Value);
    }

    [Fact]
    public void RefusesAMisgroupedNumberUnderTheInvariantCultureToo()
    {
        List<ImportOutcome<decimal?>> outcomes = Import(ImportField.Decimal("amount"), string.Empty, "1,5", "1,500.25");

        Assert.Equal("value.type-mismatch", Assert.Single(outcomes[0].Errors).Code);
        Assert.Equal(1500.25m, outcomes[1].Value);
    }
}
