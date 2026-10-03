using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Every public entry point refuses a null where it takes none, naming the argument, rather than
/// failing later with a NullReferenceException somewhere inside.
/// </summary>
/// <remarks>
/// Found by mutation testing: removing any one of these checks left the whole suite green, so the
/// contract they state was held by nothing.
/// </remarks>
public sealed class NullArgumentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly TextImportField Name = ImportField.Text("name");

    private static readonly ImportSchema Schema = new() { Fields = [Name] };

    private static readonly MappingPlan Plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "name", Header = "name" }] };

    private static MemoryStream Csv() => new(Encoding.UTF8.GetBytes("name\nx\n"));

    private static CsvCursor Cursor() => new(Csv(), "t.csv");

    private static FileProfile Profile()
    {
        using CsvCursor cursor = Cursor();
        return TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
    }

    public static TheoryData<string, string, Action> Calls => new()
    {
        { "TabularFile.Open", "stream", () => TabularFile.Open(null!, "t.csv", cancellationToken: CancellationToken.None) },
        { "TabularFile.Open", "name", () => TabularFile.Open(Csv(), null!, cancellationToken: CancellationToken.None) },
        { "TabularFile.Detect", "stream", () => TabularFile.Detect(null!) },
        { "CsvCursor", "stream", () => _ = new CsvCursor(null!, "t.csv") },
        { "CsvCursor", "sheetName", () => _ = new CsvCursor(Csv(), null!) },
        { "CsvDialectDetector.Detect", "stream", () => CsvDialectDetector.Detect((Stream)null!) },
        { "XlsxCursor", "stream", () => _ = new XlsxCursor(null!) },
        { "OdsCursor", "stream", () => _ = new OdsCursor(null!) },
        { "ArchiveCursor", "stream", () => _ = new ArchiveCursor(null!) },
        { "TabularAnalyzer.Analyze", "cursor", () => TabularAnalyzer.Analyze(null!, cancellationToken: CancellationToken.None) },
        { "TabularExtractor.Extract", "cursor", () => TabularExtractor.Extract(null!, Plan, Schema, cancellationToken: CancellationToken.None) },
        { "TabularExtractor.Extract", "plan", () => TabularExtractor.Extract(Cursor(), null!, Schema, cancellationToken: CancellationToken.None) },
        { "TabularExtractor.Extract", "schema", () => TabularExtractor.Extract(Cursor(), Plan, null!, cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(cursor)", "cursor", () => TabularImporter.Import<string?>(null!, Plan, Schema, row => row[Name], cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(cursor)", "plan", () => TabularImporter.Import<string?>(Cursor(), null!, Schema, row => row[Name], cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(cursor)", "schema", () => TabularImporter.Import<string?>(Cursor(), Plan, null!, row => row[Name], cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(cursor)", "mapper", () => TabularImporter.Import<string?>(Cursor(), Plan, Schema, null!, cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(stream)", "stream", () => TabularImporter.Import<string?>((Stream)null!, "t.csv", Plan, Schema, row => row[Name], cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(stream)", "name", () => TabularImporter.Import<string?>(Csv(), null!, Plan, Schema, row => row[Name], cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(stream)", "plan", () => TabularImporter.Import<string?>(Csv(), "t.csv", null!, Schema, row => row[Name], cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(stream)", "schema", () => TabularImporter.Import<string?>(Csv(), "t.csv", Plan, null!, row => row[Name], cancellationToken: CancellationToken.None) },
        { "TabularImporter.Import(stream)", "mapper", () => TabularImporter.Import<string?>(Csv(), "t.csv", Plan, Schema, null!, cancellationToken: CancellationToken.None) },
        { "MappingPrecheck.Check", "plan", () => MappingPrecheck.Check(null!, Schema, Profile()) },
        { "MappingPrecheck.Check", "schema", () => MappingPrecheck.Check(Plan, null!, Profile()) },
        { "MappingPrecheck.Check", "profile", () => MappingPrecheck.Check(Plan, Schema, null!) },
        { "MappingPlanValidator.Validate", "plan", () => MappingPlanValidator.Validate(null!, Schema) },
        { "MappingPlanValidator.Validate", "schema", () => MappingPlanValidator.Validate(Plan, null!) },
        { "MappingPlan.ByHeader", "sheet", () => MappingPlan.ByHeader(null!, Schema) },
        { "MappingPlan.ByHeader", "schema", () => MappingPlan.ByHeader(Profile().Sheets[0], null!) },
        { "MappingPlan.ByHeader", "matches", () => MappingPlan.ByHeader(Profile().Sheets[0], Schema, (Func<string, ImportField, bool>)null!) },
        { "HeaderVariant.TryParse", "variants", () => HeaderVariant.TryParse("name (en)", null!, out _, out _) },
        { "ImportField.Text", "name", () => ImportField.Text(null!) },
        { "ImportField.Translated", "name", () => ImportField.Translated(null!, ["en"]) },
        { "ImportField.Translated", "variants", () => ImportField.Translated("title", null!) },
        { "TextImportField.Must", "rule", () => Name.Must("rule", null!) },
        { "IntegerImportField.Must", "rule", () => ImportField.Integer("n").Must("rule", null!) },
        { "DecimalImportField.Must", "rule", () => ImportField.Decimal("n").Must("rule", null!) },
        { "DateImportField.Must", "rule", () => ImportField.Date("n").Must("rule", null!) },
        { "FieldConstraint.AllowedValues", "values", () => _ = new FieldConstraint.AllowedValues(null!) },
        { "FieldConstraint.Pattern", "expression", () => _ = new FieldConstraint.Pattern(null!) },
        { "FieldConstraint.Rule", "code", () => _ = new FieldConstraint.Rule(null!, _ => true) },
        { "FieldConstraint.Rule", "predicate", () => _ = new FieldConstraint.Rule("rule", null!) },
        { "CheckDigits.Luhn", "value", () => CheckDigits.Luhn(null!) },
        { "CheckDigits.Mod97", "value", () => CheckDigits.Mod97(null!) },
        { "ErrorCodes.IsReserved", "code", () => ErrorCodes.IsReserved(null!) },
        { "TabularWriter.Create", "stream", () => TabularWriter.Create(null!, TabularFormat.Csv) },
        { "TabularWriter.BeginSheet", "name", () => TabularWriter.Create(new MemoryStream(), TabularFormat.Csv).BeginSheet(null!, [new("a")]) },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public void RefusesANullItTakesNone(string call, string parameter, Action invoke)
    {
        ArgumentException error = Assert.ThrowsAny<ArgumentException>(invoke);

        Assert.True(parameter == error.ParamName, $"{call}: refused {error.ParamName ?? "(unnamed)"} for {parameter}");
    }
}
