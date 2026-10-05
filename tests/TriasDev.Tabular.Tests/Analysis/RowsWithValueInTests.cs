using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>Which column holds country codes, asked before anything is mapped.</summary>
public sealed class RowsWithValueInTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly FieldConstraint.AllowedValues Countries = new(["DEU", "FRA"], ignoreCase: true);

    [Fact]
    public void CountsRowsNotValues()
    {
        ColumnFacts facts = Facts("code;other\nDEU;1\nDEU;2\nfra;3\nXX;4\n;5\n");

        Assert.Equal(3, facts.RowsWithValueIn(Countries));
    }

    [Fact]
    public void SaysNothingWhenTheDistinctValuesAreIncomplete()
    {
        string csv = "code\n" + string.Concat(Enumerable.Range(0, 30).Select(i => $"V{i}\n"));
        ColumnFacts facts = Facts(csv, new AnalysisOptions { RetainedDistinctValues = 10 });

        Assert.Null(facts.RowsWithValueIn(Countries));
    }

    private static ColumnFacts Facts(string csv, AnalysisOptions? options = null)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv");
        return TabularAnalyzer.Analyze(cursor, options, cancellationToken: TestContext.Current.CancellationToken).Sheets[0].Columns[0].Facts;
    }
}
