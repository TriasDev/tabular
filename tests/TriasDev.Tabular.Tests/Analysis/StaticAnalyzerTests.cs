using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>Analysis is a static call, as import and extraction are, with its options passed in.</summary>
public sealed class StaticAnalyzerTests
{
    [Fact]
    public void AnalysesThroughAStaticCallWithItsOptionsPassedIn()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("amount;name\n1.250,50;a\n2,00;b\n")), "t.csv");

        FileProfile profile = TabularAnalyzer.Analyze(
            cursor, new AnalysisOptions { Cultures = ["de-DE"] }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ColumnType.Decimal, profile.Sheets[0].Columns[0].Hypotheses[0].Type);
        Assert.True(typeof(TabularAnalyzer).IsAbstract && typeof(TabularAnalyzer).IsSealed);
    }

    [Fact]
    public void RefusesAnOptionThatCannotWorkWhereItIsHandedOver()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("a\n1\n")), "t.csv");

        Assert.Throws<ArgumentOutOfRangeException>(() => TabularAnalyzer.Analyze(
            cursor, new AnalysisOptions { DistinctTrackingBudget = -1 }, cancellationToken: TestContext.Current.CancellationToken));
    }
}
