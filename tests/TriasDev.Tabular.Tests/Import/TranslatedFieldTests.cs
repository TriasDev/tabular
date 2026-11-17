using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// One thing a file says in several languages. Modelled on the shape a real catalogue uses —
/// <c>Title#en</c> beside <c>Title#de</c> — because that convention is already in production.
/// </summary>
public sealed class TranslatedFieldTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TranslatedImportField Title = ImportField.Translated("title", ["en", "de"]).Require();

    private static readonly TextImportField Id = ImportField.Text("id").Require();

    private static ImportSchema Schema => new() { Fields = [Id, .. Title] };

    [Fact]
    public void DeclaresOneOrdinaryFieldPerLanguage()
    {
        // The whole design rests on this: underneath there is no second dimension, only fields with
        // ordinary names, so bindings, plans and rows keep the shape they already had.
        Assert.Equal(["title.en", "title.de"], Title.Fields.Select(f => f.Name));
        Assert.Equal(["en", "de"], Title.Fields.Select(f => f.Variant));
        Assert.All(Title.Fields, f => Assert.Equal("title", f.Group));

        // Enumerated, as a schema spreads it, in the same order: the schema's, the coverage's and the
        // preview's order all follow it.
        Assert.Equal(["title.en", "title.de"], ((IEnumerable<ImportField>)Title).Select(f => f.Name));
    }

    [Fact]
    public void ImportsAPlainFieldThatCarriesAVariantButNoGroup()
    {
        // Only a field with both is a language of a group; a variant alone names no group to file it under.
        ImportField note = new() { Name = "note", Type = ColumnType.Text, Variant = "en" };
        ImportSchema schema = new() { Fields = [Id, note] };
        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { ColumnIndex = 0, Header = "id", FieldName = "id" },
                new ColumnBinding { ColumnIndex = 1, Header = "note", FieldName = "note" },
            ],
        };

        using ImportRun<string?> run = TabularImporter.Import(
            new MemoryStream(Utf8NoBom.GetBytes("id;note\nS1;hello\n"), writable: false),
            "test.csv",
            plan,
            schema,
            row => row.AsText(note),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["hello"], run.ReadAll(cancellationToken: TestContext.Current.CancellationToken).Items);
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

        run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

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

        run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["de"], titles!.Keys);
    }

    [Fact]
    public void AcceptsARowThatCarriesOnlyOneLanguage()
    {
        // A catalogue translated into German alone is a complete catalogue. Requiring English would
        // refuse it for saying nothing wrong.
        using ImportRun<int> run = Run("id;title_en;title_de\nS1;;Nur Deutsch\n", row => row.RowNumber);

        ImportOutcome<int>[] outcomes = [.. run.ReadRows(TestContext.Current.CancellationToken)];

        Assert.Empty(Assert.Single(outcomes).Errors);
    }

    [Fact]
    public void RefusesARowThatCarriesNoLanguageAtAll()
    {
        using ImportRun<int> run = Run("id;title_en;title_de\nS1;;\n", row => row.RowNumber);

        ImportOutcome<int> outcome = Assert.Single([.. run.ReadRows(TestContext.Current.CancellationToken)]);
        RowError error = Assert.Single(outcome.Errors);

        // One error for the group, not one per declared language: the row has one thing wrong with
        // it, and a subscription with ten languages would otherwise get ten errors saying so.
        Assert.Equal("group.required", error.Code);
        Assert.Equal("title", error.FieldName);

        // The group has no column of its own; the error points at its first mapped member, title_en.
        Assert.Equal(1, error.ColumnIndex);
    }

    [Fact]
    public void RefusesAPlanThatMapsNoLanguageOfARequiredGroup()
    {
        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "id", FieldName = "id" }],
        };

        MappingFault fault = Assert.Single(MappingPlanValidator.Validate(plan, Schema));

        Assert.Equal("mapping.required-group-unmapped", fault.Code);
        Assert.Equal("title", fault.FieldName);
    }

    [Fact]
    public void AcceptsAPlanThatMapsOneLanguageOfARequiredGroup()
    {
        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { ColumnIndex = 0, Header = "id", FieldName = "id" },
                new ColumnBinding { ColumnIndex = 2, Header = "title_de", FieldName = "title.de" },
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
    public void SaysNothingWhenOneLanguageIsFilledAndAnotherIsEmptyThroughout()
    {
        // A language nobody wrote is no fault while another one carries every row.
        PrecheckResult result = MappingPrecheck.Check(Plan(), Schema, Profile("id;title_en;title_de\nS1;English;\nS2;Other;\n"));

        Assert.DoesNotContain(result.Findings, f => f.Code == "group.required");
        Assert.True(result.CanImport);
    }

    [Fact]
    public void AsksNothingOfAGroupThatIsNotRequired()
    {
        // Only a required group must be carried by a row; an optional one may be empty throughout.
        TranslatedImportField optional = ImportField.Translated("title", ["en", "de"]);
        ImportSchema schema = new() { Fields = [Id, .. optional] };

        PrecheckResult result = MappingPrecheck.Check(Plan(), schema, Profile("id;title_en;title_de\nS1;;\nS2;;\n"));

        Assert.DoesNotContain(result.Findings, f => f.Code == "group.required");
        Assert.True(result.CanImport);
    }

    [Fact]
    public void ReportsALanguageBoundToAColumnTheFileDoesNotHave()
    {
        // The bound column is not in the profile: that is the binding's fault, reported as such, and
        // the group's check must not trip over the missing column.
        MappingPlan plan = Plan() with
        {
            Bindings = [.. Plan().Bindings.Take(2), new ColumnBinding { ColumnIndex = 9, Header = "title_de", FieldName = "title.de" }],
        };

        PrecheckResult result = MappingPrecheck.Check(plan, Schema, Profile("id;title_en;title_de\nS1;;\nS2;;\n"));

        PrecheckFinding finding = Assert.Single(result.Findings, f => f.Code == "mapping.invalid-column");
        Assert.Equal("title.de", finding.FieldName);
        Assert.Equal(9, finding.ColumnIndex);
        Assert.False(result.CanImport);
    }

    [Fact]
    public void ReportsCoverageForEachLanguageSeparately()
    {
        // What tells somebody the file is half-translated before they import it: 2 against 1 in the
        // same table, side by side.
        using ImportRun<int> run = Run(
            "id;title_en;title_de\nS1;One;Eins\nS2;Two;\n",
            row => row.RowNumber);

        run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, run.Coverage.Single(c => c.Field == "title.en").Filled);
        Assert.Equal(1, run.Coverage.Single(c => c.Field == "title.de").Filled);
    }

    private static MappingPlan Plan() =>
        new()
        {
            Bindings =
            [
                new ColumnBinding { ColumnIndex = 0, Header = "id", FieldName = "id" },
                new ColumnBinding { ColumnIndex = 1, Header = "title_en", FieldName = "title.en" },
                new ColumnBinding { ColumnIndex = 2, Header = "title_de", FieldName = "title.de" },
            ],
        };

    private static FileProfile Profile(string csv)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return TabularAnalyzer.Analyze(cursor);
    }

    private static ImportRun<int> Run(string csv, TabularRowMapper<int> mapper) =>
        TabularImporter.Import(
            new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false),
            "test.csv",
            Plan(),
            Schema,
            mapper);
}
