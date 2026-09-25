
using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// Pins how measured facts are turned into suggestions. No file and no parsing: facts go in,
/// ranked readings come out, which is what makes this layer a table rather than a fixture.
/// </summary>
public sealed class HypothesisBuilderTests
{
    private static ColumnFacts Profile(IEnumerable<string?> values)
    {
        ColumnProfiler profiler = new(0, "column", new DistinctBudget(AnalysisOptions.Default.DistinctTrackingBudget));

        int row = 1;

        foreach (string? value in values)
        {
            profiler.Accept(RawCell.FromText(value), row++);
        }

        return profiler.ToFacts();
    }

    private static ColumnFacts ProfileCells(IEnumerable<RawCell> cells)
    {
        ColumnProfiler profiler = new(0, "column", new DistinctBudget(AnalysisOptions.Default.DistinctTrackingBudget));

        int row = 1;

        foreach (RawCell cell in cells)
        {
            profiler.Accept(cell, row++);
        }

        return profiler.ToFacts();
    }

    [Fact]
    public void ProposesNothingForAColumnWithNoValues()
    {
        Assert.Empty(HypothesisBuilder.Build(Profile([null, "", "  "])));
    }

    [Fact]
    public void AlwaysOffersTextAndAlwaysOffersItLast()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["1", "2", "3"]));

        Assert.Equal(ColumnType.Text, hypotheses[^1].Type);
        Assert.Equal(1, hypotheses[^1].Confidence);
    }

    [Fact]
    public void PutsTheNarrowestConvincingReadingFirst()
    {
        // Whole numbers bear both readings at full confidence; the narrower one is the useful one.
        // Every culture reads them, so the ordering is all the integer readings, then all the
        // decimal ones, then text.
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["1", "2", "3"]));

        Assert.Equal(ColumnType.Integer, hypotheses[0].Type);

        int lastInteger = hypotheses.ToList().FindLastIndex(h => h.Type == ColumnType.Integer);
        int firstDecimal = hypotheses.ToList().FindIndex(h => h.Type == ColumnType.Decimal);

        Assert.True(lastInteger < firstDecimal, "every integer reading should precede every decimal one");
        Assert.Equal(ColumnType.Text, hypotheses[^1].Type);
    }

    [Fact]
    public void RanksTheCultureThatReadsTheValuesAboveTheOnesThatCannot()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["1.234,56", "2.000,00"]));

        Assert.Equal(ColumnType.Decimal, hypotheses[0].Type);
        Assert.Equal("de-DE", hypotheses[0].Culture);
        Assert.Equal(1, hypotheses[0].Confidence);
    }

    [Fact]
    public void ReportsConfidenceBelowOneAndLocatesWhatDidNotFit()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["1,50", "2,50", "k.A."]));

        TypeHypothesis best = hypotheses.First(h => h.Type == ColumnType.Decimal && h.Culture == "de-DE");

        Assert.Equal(2, best.MatchedCount);
        Assert.Equal(1, best.UnmatchedCount);
        Assert.InRange(best.Confidence, 0.66, 0.67);
        Assert.Equal(3, Assert.Single(best.Outliers).RowNumber);
        Assert.Equal("k.A.", best.Outliers[0].RawValue);
    }

    [Fact]
    public void CarriesNoOutliersForAReadingThatFitsEverything()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["1", "2"]));

        Assert.All(hypotheses.Where(h => h.Confidence >= 1), h => Assert.Empty(h.Outliers));
    }

    [Fact]
    public void OffersTheIdentifierColumnAsANumberAndLeavesTheDecisionOpen()
    {
        // The case that settles why a hypothesis proposes rather than decides: every reading here is
        // arithmetically correct, and only the facts beside them - every value distinct - say the
        // column is an identifier.
        ColumnFacts facts = Profile(["1,00", "2,00", "3,00"]);

        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(facts);

        Assert.Equal(ColumnType.Decimal, hypotheses[0].Type);
        Assert.Equal("de-DE", hypotheses[0].Culture);
        Assert.Equal(1, hypotheses[0].Confidence);
        Assert.Contains(hypotheses, h => h.Type == ColumnType.Text);

        // And the facts, independently, say what the column actually is.
        Assert.True(facts.IsUnique);
    }

    [Fact]
    public void KeepsTextLastEvenWhenOneValueSpoilsTheReadingAboveIt()
    {
        // Text fits everything, so ranking by confidence alone puts it first the moment a single
        // value fails to parse — and a hundred-thousand-row column of German amounts with one `k.A.`
        // gets proposed as text. Text is the fallback; its place is fixed, not earned.
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["1,50", "2,50", "k.A."]));

        Assert.Equal(ColumnType.Decimal, hypotheses[0].Type);
        Assert.Equal("de-DE", hypotheses[0].Culture);
        Assert.Equal(ColumnType.Text, hypotheses[^1].Type);
    }

    [Fact]
    public void DoesNotReadAnAmericanDecimalAsADate()
    {
        // A single dot made a value look date-shaped, so `1.5` was proposed as a date at full
        // confidence — ahead of the decimal reading, because a date claims more.
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["1.5", "2.5", "12.5"]));

        Assert.DoesNotContain(hypotheses, h => h.Type == ColumnType.Date);
        Assert.Equal(ColumnType.Decimal, hypotheses[0].Type);
    }

    [Fact]
    public void DoesNotInventAWholeNumberOutOfAnAmericanDecimal()
    {
        // .NET does not check group sizes, so under a German reading `19.99` parses as the integer
        // 1,999 — and the column was then proposed as whole numbers, at full confidence, ahead of
        // the reading that is actually right.
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["19.99", "5.49"]));

        Assert.DoesNotContain(hypotheses, h => h.Type == ColumnType.Integer);
        Assert.Equal(ColumnType.Decimal, hypotheses[0].Type);

        // en-US and the invariant culture read it alike; a tie goes to the invariant one, whose name
        // is the empty string and sorts first.
        Assert.Equal(string.Empty, hypotheses[0].Culture);
        Assert.Contains(hypotheses, h => h.Type == ColumnType.Decimal && h.Culture == "en-US");
    }

    [Fact]
    public void StillReadsAGermanThousandsSeparatorAsGrouping()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["1.234", "5.678"]));

        Assert.Contains(hypotheses, h => h.Culture == "de-DE" && h.Confidence >= 1);
    }

    [Fact]
    public void LocatesADateOutlierEvenWhenItIsANumber()
    {
        // The outlier was recorded only when the value failed the numeric reading too, so a date
        // column with a stray number reported that one value did not fit and could not say which —
        // which is the one thing a located outlier is for.
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["2023-01-15", "2023-02-20", "5"]));

        TypeHypothesis date = hypotheses.First(h => h.Type == ColumnType.Date);

        Assert.Equal(1, date.UnmatchedCount);
        Assert.Equal("5", Assert.Single(date.Outliers).RawValue);
        Assert.Equal(3, date.Outliers[0].RowNumber);
    }

    [Fact]
    public void DoesNotOfferAReadingThatFitsAlmostNothing()
    {
        // From a file of ten thousand real companies: a column of twenty-character identifiers was
        // offered as a decimal because 1% of them happened to parse, and it outranked text because a
        // decimal claims more. A reading that fits a rounding error of the column is not a reading.
        List<string?> values = [.. Enumerable.Range(1, 99).Select(i => (string?)$"5299{i:D3}WJZI08EFVARQ"), "1234"];

        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(values), minimumConfidence: 0.5);

        Assert.Equal(ColumnType.Text, Assert.Single(hypotheses).Type);
    }

    [Fact]
    public void StillOffersAReadingThatDescribesMostOfTheColumn()
    {
        // Postal codes: numeric in most countries and not in Britain. Worth offering, with its
        // confidence and its outliers, which is the whole point of not simply demanding perfection.
        List<string?> values = [.. Enumerable.Range(10000, 80).Select(i => (string?)i.ToString()),
                                .. Enumerable.Range(1, 20).Select(i => (string?)$"NE{i}")];

        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(values), minimumConfidence: 0.5);

        TypeHypothesis top = hypotheses[0];

        Assert.Equal(ColumnType.Integer, top.Type);
        Assert.InRange(top.Confidence, 0.79, 0.81);
        Assert.NotEmpty(top.Outliers);
    }

    [Fact]
    public void ProposesTextAloneForACodeColumn()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["DEU", "AUT", "CHE"]));

        Assert.Equal(ColumnType.Text, Assert.Single(hypotheses).Type);
    }

    [Fact]
    public void ProposesDatesWithTheCultureThatReadThem()
    {
        // Also the guard on DoesNotReadAnAmericanDecimalAsADate: making dots less date-shaped must not
        // cost the German dates that are written with them.
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["31.12.2023", "01.01.2024"]));

        Assert.Equal(ColumnType.Date, hypotheses[0].Type);
        Assert.Equal("de-DE", hypotheses[0].Culture);
    }

    [Fact]
    public void ProposesBooleans()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(Profile(["true", "false", "true"]));

        Assert.Equal(ColumnType.Boolean, hypotheses[0].Type);
        Assert.Null(hypotheses[0].Culture);
    }

    [Fact]
    public void NamesNoCultureForValuesTheFileItselfDeclared()
    {
        // A workbook states that a cell is a number. Offering a choice of cultures there would invent
        // a question the file has already answered.
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(
            ProfileCells([RawCell.FromNumber(1.5), RawCell.FromNumber(2.5)]));

        Assert.Equal(ColumnType.Decimal, hypotheses[0].Type);
        Assert.Null(hypotheses[0].Culture);
        Assert.DoesNotContain(hypotheses, h => h.Culture is not null);
    }

    [Fact]
    public void OffersTheNarrowReadingOfDeclaredNumbersOnlyWhenEveryValueBearsIt()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(
            ProfileCells([RawCell.FromNumber(1), RawCell.FromNumber(2.5)]));

        Assert.DoesNotContain(hypotheses, h => h.Type == ColumnType.Integer);
        Assert.Equal(ColumnType.Decimal, hypotheses[0].Type);
    }

    [Fact]
    public void NamesNoCultureForDeclaredDates()
    {
        IReadOnlyList<TypeHypothesis> hypotheses = HypothesisBuilder.Build(
            ProfileCells([RawCell.FromDate(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)), RawCell.FromDate(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified))]));

        Assert.Equal(ColumnType.Date, hypotheses[0].Type);
        Assert.Null(hypotheses[0].Culture);
    }
}
