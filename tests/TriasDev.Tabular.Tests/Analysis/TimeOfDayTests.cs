using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>A time of day is not a date, and is not made one by the dates around it.</summary>
public sealed class TimeOfDayTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static FileProfile Profile(string csv)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return TabularAnalyzer.Analyze(cursor);
    }

    // -- A time of day is not a date -----------------------------------------------------------

    [Fact]
    public void DoesNotReadATimeOfDayAsADate()
    {
        // DateTime.TryParse fills the missing half of "08:30" with today, so a column of shift times
        // read as dates — and the same bytes analysed on two days gave different answers, when the
        // whole promise of the profile is that it is a function of the file.
        ColumnFacts facts = Profile("shift\n08:30\n17:00\n12:15\n").Sheets[0].Columns[0].Facts;

        Assert.Null(facts.MinDate);
        Assert.Null(facts.MaxDate);
        Assert.All(facts.ParseCounts, counts => Assert.Equal(0, counts.Date));
    }

    [Fact]
    public void DoesNotStampADateOnToATimeAmongRealDates()
    {
        // The stray value is text that will not read as a date, not a value silently carrying the day
        // the import happened to run.
        ColumnFacts facts = Profile("when\n2023-01-15\n2023-02-20\n08:30\n").Sheets[0].Columns[0].Facts;

        Assert.Equal(new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Unspecified), facts.MinDate);
        Assert.Equal(new DateTime(2023, 2, 20, 0, 0, 0, DateTimeKind.Unspecified), facts.MaxDate);
    }
}
