using System.Globalization;
using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// A column whose distinct values are all kept also says how many rows hold each, and the precheck
/// turns that into the exact number of rows a rule fails (#63).
/// </summary>
public sealed class DistinctValueCountsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static FileProfile Profile(string csv, AnalysisOptions? options = null)
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "t.csv");
        return TabularAnalyzer.Analyze(cursor, options, cancellationToken: Token);
    }

    private const string Buildings = "id;material;storeys\n1;Wood;2\n2;wood;3\n3;Steel;40\n4;Concrete;5\n5;Straw;2\n6;;1\n7;Wood;-1\n";

    [Fact]
    public void CountsTheRowsOfEveryDistinctValueInTheOrderFirstSeen()
    {
        ColumnFacts material = Profile(Buildings).Sheets[0].Columns[1].Facts;

        Assert.True(material.DistinctValuesAreComplete);
        Assert.Equal(
            [("Wood", 2), ("wood", 1), ("Steel", 1), ("Concrete", 1), ("Straw", 1)],
            material.DistinctValueCounts.Select(v => (v.Value, v.Count)));
        Assert.Equal(material.NonEmptyCount, material.DistinctValueCounts.Sum(v => v.Count));
    }

    [Fact]
    public void CountsBeyondTheFrequencySampleWhileTheSetIsComplete()
    {
        // 300 distinct values, each twice: past the 64-key sample, within the 1,000 kept.
        StringBuilder csv = new("code\n");

        for (int i = 0; i < 600; i++)
        {
            csv.Append("c").Append((i % 300).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        ColumnFacts code = Profile(csv.ToString()).Sheets[0].Columns[0].Facts;

        Assert.Equal(300, code.DistinctValueCounts.Count);
        Assert.All(code.DistinctValueCounts, v => Assert.Equal(2, v.Count));
    }

    [Fact]
    public void GivesNoCountsOnceTheSetIsIncomplete()
    {
        ColumnFacts material = Profile(Buildings, new AnalysisOptions { RetainedDistinctValues = 3 }).Sheets[0].Columns[1].Facts;

        Assert.False(material.DistinctValuesAreComplete);
        Assert.Empty(material.DistinctValueCounts);
    }

    [Fact]
    public void ReportsTheExactRowsARuleFails()
    {
        TextImportField material = ImportField.Text("material").AllowedValues(["wood", "steel", "concrete"], ignoreCase: true);
        IntegerImportField storeys = ImportField.Integer("storeys").AtLeast(0).AtMost(30);
        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { ColumnIndex = 1, FieldName = "material", Header = "material" },
                new ColumnBinding { ColumnIndex = 2, FieldName = "storeys", Header = "storeys" },
            ],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, new ImportSchema { Fields = [material, storeys] }, Profile(Buildings));

        PrecheckFinding notAllowed = Assert.Single(result.Findings, f => f.Code == ErrorCodes.Value.NotAllowed);
        Assert.Equal(1, notAllowed.AffectedRows);
        Assert.Equal(RowCountBound.Exact, notAllowed.AffectedRowsBound);

        // One row below the floor, one above the ceiling: a finding for each rule.
        PrecheckFinding[] outOfRange = [.. result.Findings.Where(f => f.Code == ErrorCodes.Value.OutOfRange)];
        Assert.Equal([1, 1], outOfRange.Select(f => f.AffectedRows));
        Assert.All(outOfRange, f => Assert.Equal(RowCountBound.Exact, f.AffectedRowsBound));
    }
}
