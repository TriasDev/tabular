using System.Globalization;
using System.Text;


using Xunit;

namespace TriasDev.Tabular.InvariantGlobalizationTests;

/// <summary>
/// The core flow under invariant globalization: analysis leaves the named cultures out instead of
/// failing, a plan naming one is refused by code, and an invariant plan imports.
/// </summary>
public sealed class InvariantGlobalizationTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly DecimalImportField Amount = ImportField.Decimal("amount");

    private static readonly ImportSchema Schema = new() { Fields = [Amount] };

    private static ITabularCursor Csv() =>
        TabularFile.Open(new MemoryStream(Utf8NoBom.GetBytes("amount;x\n1.5;a\n2.25;b\n"), writable: false), "t.csv");

    private static MappingPlan Plan(string? culture) => new()
    {
        Culture = culture,
        Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "amount", FieldName = "amount" }],
    };

    [Fact]
    public void RunsWithoutNamedCultures()
    {
        // Everything below means nothing if the host quietly has ICU after all.
        Assert.True(CultureInfo.GetCultures(CultureTypes.SpecificCultures).Length <= 1);
    }

    [Fact]
    public void AnalysesWithTheDefaultCulturesByLeavingTheNamedOnesOut()
    {
        using ITabularCursor cursor = Csv();

        FileProfile profile = TabularAnalyzer.Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        ColumnProfile column = profile.Sheets[0].Columns[0];
        Assert.Equal([""], column.Facts.ParseCounts.Select(c => c.Culture));
        Assert.Equal((ColumnType.Decimal, ""), (column.Hypotheses[0].Type, column.Hypotheses[0].Culture));
    }

    [Fact]
    public void AnalysesUnderTheInvariantCultureWhenNoneOfTheListedOnesExists()
    {
        using ITabularCursor cursor = Csv();

        FileProfile profile = TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["de-DE"] }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([""], profile.Sheets[0].Columns[0].Facts.ParseCounts.Select(c => c.Culture));
    }

    [Fact]
    public void RefusesAPlanThatNamesACultureThisRuntimeDoesNotHave()
    {
        Assert.Contains(MappingPlanValidator.Validate(Plan("de-DE"), Schema), f => f.Code == ErrorCodes.Mapping.UnknownCulture);

        using ITabularCursor cursor = Csv();
        FileProfile profile = TabularAnalyzer.Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);
        PrecheckResult result = MappingPrecheck.Check(Plan("de-DE"), Schema, profile);

        Assert.False(result.CanImport);
        Assert.Equal(ErrorCodes.Mapping.UnknownCulture, Assert.Single(result.Findings).Code);
    }

    [Fact]
    public void ImportsThroughAnInvariantPlan()
    {
        using ITabularCursor cursor = Csv();
        using ImportRun<decimal?> run = TabularImporter.Import(cursor, Plan(""), Schema, row => row[Amount], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1.5m, 2.25m], run.ReadAll(cancellationToken: TestContext.Current.CancellationToken).Items);
    }

    [Fact]
    public void RefusesToWriteInACultureTheRuntimeDoesNotHave()
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => TabularWriter.Create(
            new MemoryStream(),
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Culture = "de-DE" } }));

        Assert.Contains(nameof(CsvWriterOptions.Culture), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesAnInvariantCsvTheImportReadsBack()
    {
        MemoryStream target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv, new TabularWriterOptions { LeaveOpen = true }))
        {
            writer.BeginSheet("data", [new("amount")]);
            writer.BeginRow();
            writer.Write(1.5m);
            writer.EndRow();
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        target.Position = 0;
        using ImportRun<decimal?> run = TabularImporter.Import(target, "t.csv", Plan(null), Schema, row => row[Amount], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1.5m, Assert.Single(run.ReadRows(TestContext.Current.CancellationToken)).Value);
    }
}
