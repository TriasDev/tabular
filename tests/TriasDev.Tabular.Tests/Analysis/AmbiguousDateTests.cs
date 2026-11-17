using System.Globalization;
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

    private static ColumnProfile Profile(params string[] values) => Profile(["", "de-DE", "en-US"], values);

    private static ColumnProfile Profile(string[] cultures, params string[] values)
    {
        string csv = "id;signed\n" + string.Concat(values.Select((v, i) => $"{i + 1};{v}\n"));
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "t.csv");

        return TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = cultures }, cancellationToken: Token).Sheets[0].Columns[1];
    }

    private static CultureParseCounts CountsOf(ColumnProfile column, string culture) =>
        column.Facts.ParseCounts.Single(c => c.Culture == culture);

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

    [Fact]
    public void PublishesTheEarliestAndLatestDateOfTheReadingThatTakesThemAll()
    {
        // The later date comes first in the column: the minimum is the earlier one, not the first read.
        ColumnFacts facts = Profile("25.12.2018", "24.12.2018").Facts;

        Assert.Equal(new DateTime(2018, 12, 24, 0, 0, 0, DateTimeKind.Unspecified), facts.MinDate);
        Assert.Equal(new DateTime(2018, 12, 25, 0, 0, 0, DateTimeKind.Unspecified), facts.MaxDate);
    }

    [Theory]
    [InlineData("de-DE", "15.01.2024", "16.02.2024", "17.03.2024")]
    [InlineData("en-US", "1/15/2024", "2/16/2024", "3/17/2024")]
    public void CountsTheDatesWrittenWithAOneCharacterSeparator(string culture, params string[] values)
    {
        CultureParseCounts counts = CountsOf(Profile([culture], values), culture);

        Assert.Equal(values.Length, counts.Date);
        Assert.Equal(values.Length, counts.DatesWithOwnSeparator);
    }

    [Fact]
    public void CountsTheDatesWrittenWithTheHungarianSeparatorOfDotAndSpace()
    {
        // hu-HU separates the parts of a date with two characters, ". ", which a one-character match
        // never looked for: its own dates were counted as written with no separator of its own (#184).
        Assert.Equal(". ", CultureInfo.GetCultureInfo("hu-HU").DateTimeFormat.DateSeparator);

        CultureParseCounts counts = CountsOf(Profile(["hu-HU"], "2024. 01. 15.", "2024. 02. 16.", "2024. 03. 17."), "hu-HU");

        Assert.Equal(3, counts.Date);
        Assert.Equal(3, counts.DatesWithOwnSeparator);
    }

    [Fact]
    public void PutsTheHungarianReadingFirstForDatesWrittenTheHungarianWay()
    {
        ColumnProfile column = Profile(["en-US", "hu-HU"], "2024. 01. 15.", "2024. 02. 16.", "2024. 03. 17.");

        Assert.Equal(ColumnType.Date, column.Hypotheses[0].Type);
        Assert.Equal("hu-HU", column.Hypotheses[0].Culture);
    }

    [Fact]
    public void CountsTheDatesWrittenWithTheArabicSeparatorOfMarkAndSlash()
    {
        // ar-EG writes a right-to-left mark before its slash. Which separator a culture has comes from
        // the host's ICU data, so the dates are written with whatever separator this host gives it.
        string separator = CultureInfo.GetCultureInfo("ar-EG").DateTimeFormat.DateSeparator;
        Assert.SkipWhen(separator.Length < 2, $"This host's ar-EG date separator is one character ('{separator}').");

        string[] values = [.. new[] { 15, 16, 17 }.Select(day => string.Join(separator, day, 1, 2024))];
        CultureParseCounts counts = CountsOf(Profile(["ar-EG"], values), "ar-EG");
        Assert.SkipWhen(counts.Date != values.Length, "This host does not read ar-EG dates written with its own separator.");

        Assert.Equal(values.Length, counts.DatesWithOwnSeparator);
    }
}
