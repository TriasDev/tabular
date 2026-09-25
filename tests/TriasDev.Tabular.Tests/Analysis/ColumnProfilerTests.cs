
using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// Pins what a column profile measures. No file is involved: a profiler takes values, which is what
/// makes the whole of this layer testable as a table.
/// </summary>
public sealed class ColumnProfilerTests
{
    private static ColumnFacts Profile(IEnumerable<string?> values, AnalysisOptions? options = null)
    {
        AnalysisOptions effective = options ?? AnalysisOptions.Default;
        ColumnProfiler profiler = new(0, "column", new DistinctBudget(effective.DistinctTrackingBudget), effective);

        int row = 1;

        foreach (string? value in values)
        {
            profiler.Accept(RawCell.FromText(value), row++);
        }

        return profiler.ToFacts();
    }

    private static ColumnFacts ProfileCells(IEnumerable<RawCell> cells, AnalysisOptions? options = null)
    {
        AnalysisOptions effective = options ?? AnalysisOptions.Default;
        ColumnProfiler profiler = new(0, "column", new DistinctBudget(effective.DistinctTrackingBudget), effective);

        int row = 1;

        foreach (RawCell cell in cells)
        {
            profiler.Accept(cell, row++);
        }

        return profiler.ToFacts();
    }

    private static CultureParseCounts For(ColumnFacts facts, string culture) =>
        facts.ParseCounts.Single(c => c.Culture == culture);

    [Fact]
    public void SeparatesEmptyValuesFromPresentOnes()
    {
        ColumnFacts facts = Profile(["a", null, "", "   ", "b"]);

        Assert.Equal(3, facts.EmptyCount);
        Assert.Equal(2, facts.NonEmptyCount);
    }

    [Fact]
    public void ReportsLengthsOverPresentValuesOnly()
    {
        ColumnFacts facts = Profile(["abc", null, "z", "abcdef"]);

        Assert.Equal(1, facts.MinLength);
        Assert.Equal(6, facts.MaxLength);
    }

    [Fact]
    public void ReportsLengthsAsAbsentRatherThanZeroForAColumnWithNoValues()
    {
        // Zero would be a measurement of something. There is nothing here to measure.
        ColumnFacts facts = Profile([null, "", "  "]);

        Assert.Null(facts.MinLength);
        Assert.Null(facts.MaxLength);
        Assert.Equal(0, facts.NonEmptyCount);
    }

    [Fact]
    public void AnswersTheCountryCodeQuestionFromFactsAlone()
    {
        // The case the whole full-pass requirement exists for: three characters, never a number,
        // empties allowed. One four-character value anywhere is the answer.
        ColumnFacts facts = Profile(["DEU", "AUT", null, "CHE", "FRA"]);

        Assert.Equal(3, facts.MinLength);
        Assert.Equal(3, facts.MaxLength);
        Assert.Equal(0, For(facts, "invariant").Integer + For(facts, "invariant").Decimal);
        Assert.Equal(1, facts.EmptyCount);
    }

    [Fact]
    public void CatchesAViolationWhereverItStands()
    {
        List<string?> values = [.. Enumerable.Repeat("DEU", 40_000), "DEUX", .. Enumerable.Repeat("AUT", 40_000)];

        ColumnFacts facts = Profile(values);

        Assert.Equal(3, facts.MinLength);
        Assert.Equal(4, facts.MaxLength);
    }

    [Fact]
    public void CountsGermanNumbersOnlyUnderTheGermanCulture()
    {
        // Both separators present, so only one reading of them is arithmetically possible. `7,50`
        // would not do here: under an English reading it is seven hundred and fifty, which is a
        // legitimate answer and the reason the ambiguous case gets its own test.
        ColumnFacts facts = Profile(["1.234,56", "2.000,00"]);

        Assert.Equal(2, For(facts, "de-DE").Decimal);
        Assert.Equal(0, For(facts, "invariant").Decimal + For(facts, "invariant").Integer);
        Assert.Equal(0, For(facts, "en-US").Decimal + For(facts, "en-US").Integer);
    }

    [Fact]
    public void RefusesAReadingWhoseGroupingIsMalformed()
    {
        // `1,00` is one under a German reading. An English reading would make it a hundred — .NET
        // will happily do that, since it does not check that a group holds three digits — but a group
        // of two digits is not grouping, so that reading is refused rather than offered.
        ColumnFacts facts = Profile(["1,00", "2,00", "3,00"]);

        Assert.Equal(3, For(facts, "de-DE").Decimal);
        Assert.Equal(0, For(facts, "invariant").Integer);
        Assert.True(facts.IsUnique);
    }

    [Fact]
    public void LocatesTheValuesThatDidNotParse()
    {
        ColumnFacts facts = Profile(["1,50", "k.A.", "2,50", "—"]);

        IReadOnlyList<ValueLocation> outliers = For(facts, "de-DE").NumericOutliers;

        Assert.Equal(2, outliers.Count);
        Assert.Equal(2, outliers[0].RowNumber);
        Assert.Equal("k.A.", outliers[0].RawValue);
        Assert.Equal(4, outliers[1].RowNumber);
    }

    [Fact]
    public void StopsCollectingOutliersAtTheConfiguredLimit()
    {
        AnalysisOptions options = new() { OutlierSampleSize = 3 };

        ColumnFacts facts = Profile(Enumerable.Repeat("nope", 100), options);

        Assert.Equal(3, For(facts, "de-DE").NumericOutliers.Count);
    }

    [Fact]
    public void DoesNotMistakeABareNumberForADate()
    {
        // DateTime.TryParse accepts "1" and makes it the first of the current month, which would
        // make every identifier column look like a column of dates.
        ColumnFacts facts = Profile(["1", "2", "3"]);

        Assert.Equal(0, For(facts, "en-US").Date);
    }

    [Fact]
    public void ReadsDatesAndReportsTheirRange()
    {
        ColumnFacts facts = Profile(["2023-01-15", "2021-06-30", "2024-12-01"]);

        Assert.Equal(3, For(facts, "invariant").Date);
        Assert.Equal(new DateTime(2021, 6, 30, 0, 0, 0, DateTimeKind.Unspecified), facts.MinDate);
        Assert.Equal(new DateTime(2024, 12, 1, 0, 0, 0, DateTimeKind.Unspecified), facts.MaxDate);
    }

    [Fact]
    public void ReportsTheRangeOfNumbers()
    {
        ColumnFacts facts = Profile(["10", "-5", "1000"]);

        Assert.Equal(-5m, facts.MinNumeric);
        Assert.Equal(1000m, facts.MaxNumeric);
    }

    [Fact]
    public void CountsBooleans()
    {
        ColumnFacts facts = Profile(["true", "false", "maybe"]);

        Assert.Equal(2, facts.BooleanCount);
    }

    [Fact]
    public void RecordsWhatTheFileItselfDeclaredEachCellToBe()
    {
        ColumnFacts facts = ProfileCells(
        [
            RawCell.FromNumber(1),
            RawCell.FromDate(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)),
            RawCell.FromText("x"),
            RawCell.Empty,
        ]);

        Assert.Equal(1, facts.NativeKinds[RawCellKind.Number]);
        Assert.Equal(1, facts.NativeKinds[RawCellKind.Date]);
        Assert.Equal(1, facts.NativeKinds[RawCellKind.Text]);
        Assert.Equal(1, facts.NativeKinds[RawCellKind.Empty]);
    }

    [Fact]
    public void TakesAWorkbooksOwnTypesWithoutReparsingThem()
    {
        ColumnFacts facts = ProfileCells([RawCell.FromNumber(1.5), RawCell.FromNumber(2)]);

        Assert.Equal(1, For(facts, "de-DE").Decimal);
        Assert.Equal(1, For(facts, "de-DE").Integer);
        Assert.Equal(1.5m, facts.MinNumeric);
    }

    [Fact]
    public void RecognisesAColumnWhoseValuesAreAllDifferent()
    {
        ColumnFacts facts = Profile(Enumerable.Range(1, 5000).Select(i => $"id-{i}"));

        Assert.Equal(5000, facts.DistinctCount);
        Assert.True(facts.DistinctCountIsExact);
        Assert.True(facts.IsUnique);
    }

    [Fact]
    public void RecognisesAColumnThatRepeatsItself()
    {
        ColumnFacts facts = Profile(["DEU", "AUT", "DEU", "CHE", "AUT"]);

        Assert.Equal(3, facts.DistinctCount);
        Assert.False(facts.IsUnique);
    }

    [Fact]
    public void SaysSoRatherThanGuessingWhenTheTrackingBudgetRunsOut()
    {
        AnalysisOptions options = new() { DistinctTrackingBudget = 10 };

        ColumnFacts facts = Profile(Enumerable.Range(1, 1000).Select(i => $"id-{i}"), options);

        Assert.False(facts.DistinctCountIsExact);
        Assert.Equal(10, facts.DistinctCount);
        Assert.Null(facts.IsUnique);
    }

    [Fact]
    public void SpendsOneBudgetAcrossEveryColumnRatherThanOnePerColumn()
    {
        AnalysisOptions options = new() { DistinctTrackingBudget = 12 };
        DistinctBudget budget = new(options.DistinctTrackingBudget);

        ColumnProfiler first = new(0, "a", budget, options);
        ColumnProfiler second = new(1, "b", budget, options);

        // Filled one after the other, not alternately: with a shared budget of twelve, interleaving
        // would starve both columns at six each and prove nothing about which one ran out.
        for (int i = 1; i <= 10; i++)
        {
            first.Accept(RawCell.FromText($"first-{i}"), i);
        }

        for (int i = 1; i <= 10; i++)
        {
            second.Accept(RawCell.FromText($"second-{i}"), i);
        }

        Assert.True(first.ToFacts().DistinctCountIsExact);
        Assert.False(second.ToFacts().DistinctCountIsExact);
    }

    [Fact]
    public void ReportsABoundedSampleOfValuesWithHowOftenEachAppears()
    {
        AnalysisOptions options = new() { ReportedSampleSize = 2 };

        ColumnFacts facts = Profile(["a", "a", "a", "b", "b", "c"], options);

        Assert.Equal(2, facts.DistinctSamples.Count);
        Assert.Equal("a", facts.DistinctSamples[0].Value);
        Assert.Equal(3, facts.DistinctSamples[0].Count);
        Assert.Equal("b", facts.DistinctSamples[1].Value);
    }

    [Fact]
    public void KeepsTheFirstValuesVerbatimUpToTheConfiguredCount()
    {
        AnalysisOptions options = new() { FirstValueSampleSize = 3 };

        ColumnFacts facts = Profile(["x", null, "y", "z", "w"], options);

        Assert.Equal(["x", "y", "z"], facts.Samples);
    }
}
