using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>A value naming half a date is not completed into one, by analysis or by extraction.</summary>
public sealed class PartialDateTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static FileProfile Profile(string csv, AnalysisOptions? options = null)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return TabularAnalyzer.Analyze(cursor, options);
    }

    // -- A value that does not name a date completely is not a date -------------------------------

    [Theory]
    [InlineData("Jan 5", "en-US")]
    [InlineData("3/15", "en-US")]
    [InlineData("15.01", "de-DE")]
    [InlineData("January 2023", "en-US")]
    [InlineData("08:30", "en-US")]
    [InlineData("1.5", "en-US")]
    public void DoesNotInventTheMissingHalfOfADate(string value, string culture)
    {
        // The parser fills in whatever the text leaves out, from the clock: a bare time becomes
        // today, a day and month become this year. The first was fixed and the second was not, so the
        // nondeterminism simply moved from the day boundary to the year boundary.
        FileProfile profile = Profile(
            $"when\n{value}\n",
            new AnalysisOptions { Cultures = [culture] });

        ColumnFacts facts = profile.Sheets[0].Columns[0].Facts;

        Assert.Null(facts.MinDate);
        Assert.All(facts.ParseCounts, counts => Assert.Equal(0, counts.Date));
    }

    [Fact]
    public void TheExtractorRefusesWhatTheProfilerRefuses()
    {
        // They disagreed, and the disagreement was structural: the profiler applied a shape test and
        // the extractor did not. So "3/15" was text to one half and March of the current year to the
        // other, and the precheck blocked files the import accepted. One rule, in one place, now.
        ImportSchema schema = new() { Fields = [ImportField.Date("when")] };

        MappingPlan plan = new()
        {
            Culture = "en-US",
            Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "when", FieldName = "when" }],
        };

        using MemoryStream stream = new(Utf8NoBom.GetBytes("when\n3/15\n2023-05-06\n"), writable: false);

        using ImportRun<DateTime?> run = TabularImporter.Import(
            stream,
            "t.csv",
            plan,
            schema,
            row => row[schema.Fields[0] as DateImportField ?? throw new InvalidOperationException()], cancellationToken: TestContext.Current.CancellationToken);

        run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, run.Summary.RowsProduced);
        Assert.Equal(1, run.Summary.RowsFailed);
    }

    [Fact]
    public void StillReadsADateThatNamesItselfCompletely()
    {
        FileProfile profile = Profile("when\n2023-01-15\n2023-02-20\n");
        ColumnFacts facts = profile.Sheets[0].Columns[0].Facts;

        Assert.Equal(new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Unspecified), facts.MinDate);
        Assert.Equal(new DateTime(2023, 2, 20, 0, 0, 0, DateTimeKind.Unspecified), facts.MaxDate);
    }
}
