using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// A column whose every day is twelve or less reads as a date under cultures that disagree on which
/// number is the month. The reading the values' own separator belongs to goes first, and the facts say
/// that the readings disagree, so nobody takes a coin toss for an answer (#58).
/// </summary>
public sealed class AmbiguousDateTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTime JanuaryEleventh = new(2018, 1, 11, 0, 0, 0, DateTimeKind.Unspecified);

    private static ColumnProfile Profile(params string[] values)
    {
        string csv = "id;signed\n" + string.Concat(values.Select((v, i) => $"{i + 1};{v}\n"));
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "t.csv");

        return TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["", "de-DE", "en-US"] }, cancellationToken: Token).Sheets[0].Columns[1];
    }

    [Fact]
    public void PutsTheCultureWhoseSeparatorTheDatesUseFirst()
    {
        ColumnProfile column = Profile("11.01.2018", "11.02.2018", "11.03.2018");

        TypeHypothesis best = column.Hypotheses[0];
        Assert.Equal(ColumnType.Date, best.Type);
        Assert.Equal("de-DE", best.Culture);
        Assert.Equal(JanuaryEleventh, column.Facts.MinDate);
        Assert.Equal(new DateTime(2018, 3, 11, 0, 0, 0, DateTimeKind.Unspecified), column.Facts.MaxDate);
    }

    [Fact]
    public void KeepsTheOrderForSlashesWhichTheCulturesShare()
    {
        ColumnProfile column = Profile("11/01/2018", "11/02/2018", "11/03/2018");

        Assert.Equal(ColumnType.Date, column.Hypotheses[0].Type);
        Assert.NotEqual("de-DE", column.Hypotheses[0].Culture);
    }

    [Theory]
    [InlineData("11.01.2018", "11.02.2018", "11.03.2018")]
    [InlineData("11/01/2018", "11/02/2018", "11/03/2018")]
    [InlineData("01.02.2018", "02.01.2018")]
    public void SaysTheDateReadingsDisagreeWhenDayAndMonthCanSwap(params string[] values)
    {
        Assert.True(Profile(values).Facts.DateReadingsDisagree);
    }

    [Theory]
    [InlineData("2018-01-11", "2018-02-11")]
    [InlineData("25.01.2018", "11.02.2018")]
    [InlineData("11.11.2018", "12.12.2018")]
    public void SaysNothingWhereTheReadingsAgreeOrOnlyOneReadsEveryValue(params string[] values)
    {
        Assert.False(Profile(values).Facts.DateReadingsDisagree);
    }
}
