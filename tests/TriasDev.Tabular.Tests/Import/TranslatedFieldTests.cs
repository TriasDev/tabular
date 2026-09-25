using System.Text;

using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Extraction;
using TriasDev.Tabular.Import;
using TriasDev.Tabular.Mapping;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// One thing a file says in several languages. Modelled on the shape a real catalogue uses —
/// <c>Title#en</c> beside <c>Title#de</c> — because that convention is already in production.
/// </summary>
public sealed class TranslatedFieldTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TranslatedField Title = ImportField.Translated("title", ["en", "de"]).Require();

    private static readonly TextField Id = ImportField.Text("id").Require();

    private static TargetSchema Schema => new() { Fields = [Id, .. Title] };

    [Fact]
    public void DeclaresOneOrdinaryFieldPerLanguage()
    {
        // The whole design rests on this: underneath there is no second dimension, only fields with
        // ordinary names, so bindings, plans and rows keep the shape they already had.
        Assert.Equal(["title.en", "title.de"], Title.Fields.Select(f => f.Name));
        Assert.Equal(["en", "de"], Title.Fields.Select(f => f.Variant));
        Assert.All(Title.Fields, f => Assert.Equal("title", f.Group));
    }

    [Fact]
    public void HandsTheMapperOneValuePerLanguage()
    {
        Dictionary<string, string>? titles = null;

        using ImportRun<int> run = Run(
            "id;title_en;title_de\nS1;Security Organization;Sicherheitsorganisation\n",
            row =>
            {
                titles = row.Translations(Title);
                return row.RowNumber;
            });

        run.All();

        Assert.NotNull(titles);
        Assert.Equal("Security Organization", titles["en"]);
        Assert.Equal("Sicherheitsorganisation", titles["de"]);
    }

    [Fact]
    public void LeavesOutALanguageTheRowDoesNotCarry()
    {
        // Absent rather than present and empty. "Not translated" and "translated to nothing" are
        // different things to whatever stores this, and only the first is true.
        Dictionary<string, string>? titles = null;

        using ImportRun<int> run = Run(
            "id;title_en;title_de\nS1;;Sicherheitsorganisation\n",
            row =>
            {
                titles = row.Translations(Title);
                return row.RowNumber;
            });

        run.All();

        Assert.Equal(["de"], titles!.Keys);
    }

    [Fact]
    public void AcceptsARowThatCarriesOnlyOneLanguage()
    {
        // A catalogue translated into German alone is a complete catalogue. Requiring English would
        // refuse it for saying nothing wrong.
        using ImportRun<int> run = Run("id;title_en;title_de\nS1;;Nur Deutsch\n", row => row.RowNumber);

        ImportOutcome<int>[] outcomes = [.. run];

        Assert.Empty(Assert.Single(outcomes).Errors);
    }

    [Fact]
    public void RefusesARowThatCarriesNoLanguageAtAll()
    {
        using ImportRun<int> run = Run("id;title_en;title_de\nS1;;\n", row => row.RowNumber);

        ImportOutcome<int> outcome = Assert.Single([.. run]);
        RowError error = Assert.Single(outcome.Errors);

        // One error for the group, not one per declared language: the row has one thing wrong with
        // it, and a subscription with ten languages would otherwise get ten errors saying so.
        Assert.Equal("group.required", error.Code);
        Assert.Equal("title", error.TargetFieldName);
    }

    [Fact]
    public void RefusesAPlanThatMapsNoLanguageOfARequiredGroup()
    {
        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "id", TargetFieldName = "id" }],
        };

        MappingFault fault = Assert.Single(MappingPlanValidator.Validate(plan, Schema));

        Assert.Equal("mapping.required-group-unmapped", fault.Code);
        Assert.Equal("title", fault.TargetFieldName);
    }

    [Fact]
    public void AcceptsAPlanThatMapsOneLanguageOfARequiredGroup()
    {
        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "id", TargetFieldName = "id" },
                new ColumnBinding { SourceColumnIndex = 2, SourceHeader = "title_de", TargetFieldName = "title.de" },
            ],
        };

        Assert.Empty(MappingPlanValidator.Validate(plan, Schema));
    }

    [Fact]
    public void BlocksBeforeReadingWhenEveryMappedLanguageIsEmpty()
    {
        FileProfile profile = Profile("id;title_en;title_de\nS1;;\nS2;;\n");

        PrecheckResult result = MappingPrecheck.Check(Plan(), Schema, profile);

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "group.required");

        Assert.Equal(PrecheckSeverity.Blocking, finding.Severity);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void SaysNothingWhenTheLanguagesCoverTheFileBetweenThem()
    {
        // The case that makes a per-column reading of this rule wrong. Each column is half empty and
        // neither is at fault: between them every row is covered, and facts about one column can
        // never show that. Whether a particular row is bare is settled while importing.
        FileProfile profile = Profile("id;title_en;title_de\nS1;English;\nS2;;Deutsch\n");

        Assert.Empty(MappingPrecheck.Check(Plan(), Schema, profile).Findings);
    }

    [Fact]
    public void ReportsCoverageForEachLanguageSeparately()
    {
        // What tells somebody the file is half-translated before they import it: 2 against 1 in the
        // same table, side by side.
        using ImportRun<int> run = Run(
            "id;title_en;title_de\nS1;One;Eins\nS2;Two;\n",
            row => row.RowNumber);

        run.All();

        Assert.Equal(2, run.Coverage.Single(c => c.Field == "title.en").Filled);
        Assert.Equal(1, run.Coverage.Single(c => c.Field == "title.de").Filled);
    }

    private static MappingPlan Plan() =>
        new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "id", TargetFieldName = "id" },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "title_en", TargetFieldName = "title.en" },
                new ColumnBinding { SourceColumnIndex = 2, SourceHeader = "title_de", TargetFieldName = "title.de" },
            ],
        };

    private static FileProfile Profile(string csv)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return new TabularAnalyzer().Analyze(cursor);
    }

    private static ImportRun<int> Run(string csv, TabularRowMapper<int> mapper) =>
        TabularImporter.Import(
            new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false),
            "test.csv",
            Plan(),
            Schema,
            mapper);
}
