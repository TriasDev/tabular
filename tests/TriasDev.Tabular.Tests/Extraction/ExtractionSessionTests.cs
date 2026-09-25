using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>Pins what a run produces: values, errors, and what it says about itself afterwards.</summary>
public sealed class ExtractionSessionTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static TargetSchema Schema =>
        new()
        {
            Fields =
            [
                new TargetField
                {
                    Name = "countryIso3",
                    Type = ColumnType.Text,
                    Required = true,
                    Constraints = [new FieldConstraint.ExactLength(3)],
                },
                new TargetField { Name = "amount", Type = ColumnType.Decimal },
            ],
        };

    private static MappingPlan Plan(string? culture = "de-DE") =>
        new()
        {
            Culture = culture,
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "Land", TargetFieldName = "countryIso3" },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "Betrag", TargetFieldName = "amount" },
            ],
        };

    private sealed record Run(List<string?[]> Values, List<RowError> Errors, ExtractionSummary Summary);

    private static Run Extract(
        string text,
        MappingPlan? plan = null,
        TargetSchema? schema = null,
        ExtractionOptions? options = null)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(text), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        ExtractionSession session = TabularExtractor.Start(cursor, plan ?? Plan(), schema ?? Schema, options);

        List<string?[]> values = [];
        List<RowError> errors = [];

        while (session.ReadRow())
        {
            if (session.CurrentRowHasErrors)
            {
                errors.AddRange(session.CurrentErrors);
                continue;
            }

            string?[] row = new string?[session.CurrentValues.Length];

            for (int i = 0; i < row.Length; i++)
            {
                row[i] = session.CurrentValues[i].IsPresent ? session.CurrentValues[i].Text : null;
            }

            values.Add(row);
        }

        return new Run(values, errors, session.Summary);
    }

    [Fact]
    public void ProducesTypedValuesForARowThatFits()
    {
        Run run = Extract("Land;Betrag\nDEU;1.234,56\n");

        Assert.Empty(run.Errors);
        Assert.Equal(new string?[] { "DEU", "1234.56" }, Assert.Single(run.Values));
        Assert.Equal(1, run.Summary.RowsProduced);
    }

    [Fact]
    public void ReadsNumbersUnderTheCultureThePlanNames()
    {
        Run german = Extract("Land;Betrag\nDEU;1.234,56\n", Plan("de-DE"));
        Run english = Extract("Land;Betrag\nDEU;1,234.56\n", Plan("en-US"));

        Assert.Equal("1234.56", german.Values[0][1]);
        Assert.Equal("1234.56", english.Values[0][1]);
    }

    [Fact]
    public void ProducesNoValuesForARowThatFails()
    {
        // Half a row invites a caller to build half an entity, which is how silent corruption starts.
        Run run = Extract("Land;Betrag\nDEUX;1,00\n");

        Assert.Empty(run.Values);
        Assert.Equal("value.exact-length", Assert.Single(run.Errors).Code);
    }

    [Fact]
    public void ReportsEveryFaultOfARowTogether()
    {
        Run run = Extract("Land;Betrag\nDEUX;nonsense\n");

        Assert.Equal(2, run.Errors.Count);
        Assert.Contains(run.Errors, e => e.Code == "value.exact-length");
        Assert.Contains(run.Errors, e => e.Code == "value.type-mismatch");
    }

    [Fact]
    public void LocatesAnErrorAndKeepsTheValueThatCausedIt()
    {
        Run run = Extract("Land;Betrag\nDEU;1,00\nDEUX;2,00\n");

        RowError error = Assert.Single(run.Errors);

        Assert.Equal(3, error.RowNumber);
        Assert.Equal(0, error.SourceColumnIndex);
        Assert.Equal("countryIso3", error.TargetFieldName);
        Assert.Equal("DEUX", error.RawValue);
    }

    [Fact]
    public void ReportsAMissingRequiredValue()
    {
        Run run = Extract("Land;Betrag\n;1,00\n");

        Assert.Equal("value.required", Assert.Single(run.Errors).Code);
    }

    [Fact]
    public void AcceptsAMissingOptionalValue()
    {
        Run run = Extract("Land;Betrag\nDEU;\n");

        Assert.Empty(run.Errors);
        Assert.Null(Assert.Single(run.Values)[1]);
    }

    [Fact]
    public void ReadsAValueTheBindingCallsEmptyAsAbsent()
    {
        MappingPlan plan = new()
        {
            Culture = "de-DE",
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "Land", TargetFieldName = "countryIso3" },
                new ColumnBinding
                {
                    SourceColumnIndex = 1,
                    SourceHeader = "Betrag",
                    TargetFieldName = "amount",
                    TreatAsEmpty = ["k.A.", "-"],
                },
            ],
        };

        Run run = Extract("Land;Betrag\nDEU;k.A.\n", plan);

        Assert.Empty(run.Errors);
        Assert.Null(Assert.Single(run.Values)[1]);
    }

    [Fact]
    public void SkipsARowWhoseMappedCellsAreAllEmptyRatherThanFailingIt()
    {
        // A spreadsheet accumulates such rows below its data as a matter of course. Calling them
        // failures would bury the real ones.
        Run run = Extract("Land;Betrag\nDEU;1,00\n;\n;\n");

        Assert.Empty(run.Errors);
        Assert.Single(run.Values);
        Assert.Equal(2, run.Summary.RowsSkipped);
    }

    [Fact]
    public void TreatsAMissingCellAsEmptyAndFailsOnlyWhereTheFieldIsRequired()
    {
        Run run = Extract("Land;Betrag\nDEU\n");

        Assert.Empty(run.Errors);
        Assert.Null(Assert.Single(run.Values)[1]);
    }

    [Fact]
    public void StopsAtTheErrorLimitAndSaysSo()
    {
        StringBuilder text = new("Land;Betrag\n");

        for (int i = 0; i < 50; i++)
        {
            text.Append("DEUX;1,00\n");
        }

        Run run = Extract(text.ToString(), options: new ExtractionOptions { MaxErrorRows = 5 });

        Assert.True(run.Summary.StoppedEarly);
        Assert.Equal(5, run.Summary.RowsFailed);
    }

    [Fact]
    public void ReadsAWholeCleanFileWithoutStopping()
    {
        Run run = Extract("Land;Betrag\nDEU;1,00\nAUT;2,00\nCHE;3,00\n");

        Assert.False(run.Summary.StoppedEarly);
        Assert.Equal(3, run.Summary.RowsProduced);
        Assert.Equal(3, run.Summary.RowsRead);
    }

    [Fact]
    public void ValidatesWithoutProducingValues()
    {
        Run run = Extract(
            "Land;Betrag\nDEU;1,00\nDEUX;2,00\n",
            options: new ExtractionOptions { ValidateOnly = true });

        Assert.Single(run.Errors);
        Assert.All(run.Values, row => Assert.All(row, Assert.Null));
        Assert.Equal(1, run.Summary.RowsProduced);
    }

    [Fact]
    public void RefusesAFileWhoseHeaderNoLongerMatchesTheMapping()
    {
        // Analysis and extraction are separate reads, with a user's mapping session in between.
        using MemoryStream stream = new(Utf8NoBom.GetBytes("Strasse;Betrag\nDEU;1,00\n"), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan(), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Land", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesASheetThatIsNotThere()
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes("Land;Betrag\nDEU;1,00\n"), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        MappingPlan plan = Plan() with { SheetIndex = 3 };

        Assert.Throws<TabularStructureException>(() => TabularExtractor.Start(cursor, plan, Schema, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesToStartOnAPlanThatDoesNotFitTheSchema()
    {
        // Starting anyway would report the mapping's faults as if they were the data's.
        using MemoryStream stream = new(Utf8NoBom.GetBytes("Land;Betrag\nDEU;1,00\n"), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "Betrag", TargetFieldName = "amount" }],
        };

        Assert.Throws<MappingPlanException>(() => TabularExtractor.Start(cursor, plan, Schema, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StopsWhenAsked()
    {
        StringBuilder text = new("Land;Betrag\n");

        for (int i = 0; i < 10_000; i++)
        {
            text.Append("DEU;1,00\n");
        }

        using MemoryStream stream = new(Utf8NoBom.GetBytes(text.ToString()), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        using CancellationTokenSource cancellation = new();
        ExtractionSession session = TabularExtractor.Start(
            cursor,
            Plan(),
            Schema,
            cancellationToken: cancellation.Token);

        session.ReadRow(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => session.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadsFromTheHeaderRowThePlanNames()
    {
        // A title line above the table is the ordinary case analysis deliberately does not guess at.
        MappingPlan plan = Plan() with { HeaderRowIndex = 1 };

        Run run = Extract("Auswertung Q3 2026;\nLand;Betrag\nDEU;1,00\n", plan);

        Assert.Empty(run.Errors);
        Assert.Equal("DEU", Assert.Single(run.Values)[0]);
    }
}
