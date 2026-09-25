using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Mapping;

/// <summary>
/// A plan built from the headers alone, for files whose columns are named after the fields.
/// </summary>
public sealed class MappingPlanByHeaderTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static SheetProfile Sheet(string csv, AnalysisOptions? options = null)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return new TabularAnalyzer(options).Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken).Sheets[0];
    }

    [Fact]
    public void BindsColumnsWhoseHeadersNameTheFieldsIgnoringCaseAndSeparators()
    {
        SheetProfile sheet = Sheet("Postal Code;ignored;COUNTRY_code;title-en\n80331;x;DE;a\n");
        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("postalCode"), ImportField.Text("countryCode"), ImportField.Text("title.en")],
        };

        MappingPlan plan = MappingPlan.ByHeader(sheet, schema);

        Assert.Equal(
            [(0, "Postal Code", "postalCode"), (2, "COUNTRY_code", "countryCode"), (3, "title-en", "title.en")],
            plan.Bindings.Select(b => (b.SourceColumnIndex, b.SourceHeader, b.TargetFieldName)));
        Assert.Empty(MappingPlanValidator.Validate(plan, schema));
    }

    [Fact]
    public void LeavesAnUnmatchedRequiredFieldForTheValidatorToReport()
    {
        TargetSchema schema = new() { Fields = [ImportField.Text("name"), ImportField.Text("iban").Require()] };

        MappingPlan plan = MappingPlan.ByHeader(Sheet("name;account\na;b\n"), schema);

        Assert.Equal(["name"], plan.Bindings.Select(b => b.TargetFieldName));
        Assert.Contains(MappingPlanValidator.Validate(plan, schema), f => f.TargetFieldName == "iban");
    }

    [Fact]
    public void BindsAFieldOnceWhenTwoColumnsCarryItsName()
    {
        TargetSchema schema = new() { Fields = [ImportField.Text("name")] };

        MappingPlan plan = MappingPlan.ByHeader(Sheet("name;Name\na;b\n"), schema);

        Assert.Equal(0, Assert.Single(plan.Bindings).SourceColumnIndex);
        Assert.Empty(MappingPlanValidator.Validate(plan, schema));
    }

    [Fact]
    public void CarriesTheSheetTheHeaderRowAndTheCulture()
    {
        SheetProfile sheet = Sheet("Export 2026\namount;x\n1,5;a\n", new AnalysisOptions { HeaderRowIndex = 1 });

        MappingPlan plan = MappingPlan.ByHeader(sheet, new TargetSchema { Fields = [ImportField.Decimal("amount")] }, "de-DE");

        Assert.Equal(sheet.Index, plan.SheetIndex);
        Assert.Equal(1, plan.HeaderRowIndex);
        Assert.Equal("de-DE", plan.Culture);
        Assert.Equal("amount", Assert.Single(plan.Bindings).SourceHeader);
    }

    [Fact]
    public void RecordsAWorkbookSheetItWasBuiltFor()
    {
        byte[] workbook = new Fixtures.XlsxPackage()
            .WithSheet("Orders", """<row r="1"><c r="A1" t="inlineStr"><is><t>name</t></is></c></row>""")
            .Build();
        using TriasDev.Tabular.Xlsx.XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);
        SheetProfile sheet = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken).Sheets[0];

        MappingPlan plan = MappingPlan.ByHeader(sheet, new TargetSchema { Fields = [ImportField.Text("name")] });

        Assert.Equal(("Orders", (string?)null), (plan.SheetName, plan.SheetSource));
    }

    [Fact]
    public void LeavesAPlainCsvsNameUnrecordedSoARenamedUploadStillImports()
    {
        // A plain csv's sheet name is whatever name the caller passed in, not something the file
        // says: an upload analysed as "Kunden.csv" and imported from its stored blob name is the same
        // file. Its only sheet is index 0, which already says which one.
        SheetProfile sheet = Sheet("name;x\na;b\n");
        TextField name = ImportField.Text("name");
        TargetSchema schema = new() { Fields = [name] };

        MappingPlan plan = MappingPlan.ByHeader(sheet, schema);

        Assert.Null(plan.SheetName);

        using CsvCursor stored = new(new MemoryStream(Utf8NoBom.GetBytes("name;x\na;b\n")), "3f2a.tmp");
        using ImportRun<string?> run = TabularImporter.Import(stored, plan, schema, row => row[name], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["a"], run.All(cancellationToken: TestContext.Current.CancellationToken).Items);
    }

    [Fact]
    public void MatchesWithTheCallersOwnRule()
    {
        // A synonym list is the ordinary case: the file says "PLZ", the field is postalCode.
        Dictionary<string, string[]> synonyms = new(StringComparer.Ordinal) { ["postalCode"] = ["PLZ", "zip"] };
        TargetSchema schema = new() { Fields = [ImportField.Text("postalCode")] };

        MappingPlan plan = MappingPlan.ByHeader(
            Sheet("Ort;PLZ\nMünchen;80331\n"),
            schema,
            (header, field) => synonyms.TryGetValue(field.Name, out string[]? names) && names.Contains(header, StringComparer.OrdinalIgnoreCase));

        Assert.Equal((1, "PLZ"), (Assert.Single(plan.Bindings).SourceColumnIndex, plan.Bindings[0].SourceHeader));
    }

    [Fact]
    public void NeverBindsAnEmptyHeader()
    {
        TargetSchema schema = new() { Fields = [ImportField.Text("x")] };

        MappingPlan plan = MappingPlan.ByHeader(Sheet(";x\na;b\n"), schema, (_, _) => true);

        Assert.Equal(1, Assert.Single(plan.Bindings).SourceColumnIndex);
    }
}
