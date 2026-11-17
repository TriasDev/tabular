using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
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
        { "GzipCursor", "stream", () => _ = new GzipCursor(null!, "t.csv.gz") },
        { "GzipCursor", "name", () => _ = new GzipCursor(new MemoryStream(GzipFile.Of("a\n")), null!) },
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
        { "TabularWriter.RegisterStyle", "style", () => TabularWriter.Create(new MemoryStream(), TabularFormat.Xlsx).RegisterStyle(null!) },
        { "TabularWriter.WriteBatch", "batch", () => InSheet().WriteBatch(null!) },
        { "CellColor.Parse", "text", () => CellColor.Parse(null!) },
        { "DateFormat.Parse", "code", () => DateFormat.Parse(null!) },
        { "NumberFormat.Parse", "code", () => NumberFormat.Parse(null!) },
        { "TabularExportBuilder.Sheet", "options", () => TabularExport.For<string>().Sheet(null!) },
        { "ColumnBatch.Add(values)", "values", () => OneRow().Add((IReadOnlyList<long>)null!) },
        { "ColumnBatch.Add(values, styles)", "styles", () => OneRow().Add(new[] { 1L }, (IReadOnlyList<StyleId>)null!) },
        { "ColumnBatch.Add(values, rule)", "style", () => OneRow().Add(new[] { 1L }, (Func<long, CellStyle?>)null!) },
        { "ColumnBatch.Add(items, value)", "items", () => OneRow().Add((IReadOnlyList<int>)null!, i => (long)i) },
        { "ColumnBatch.Add(items, value)", "value", () => OneRow().Add(new[] { 1 }, (Func<int, long>)null!) },
        { "ColumnBatch.Add(items, value, styles)", "styles", () => OneRow().Add(new[] { 1 }, i => (long)i, (IReadOnlyList<StyleId>)null!) },
        { "ColumnBatch.Add(items, value, rule)", "style", () => OneRow().Add(new[] { 1 }, i => (long)i, (Func<long, CellStyle?>)null!) },
    };

    private static readonly TabularExport<string> Export = TabularExport.For<string>().Column("a", s => s).Build();

    private static TabularWriter InSheet()
    {
        TabularWriter writer = TabularWriter.Create(new MemoryStream(), TabularFormat.Csv);
        writer.BeginSheet("data", [new("a")]);
        return writer;
    }

    private static ColumnBatch OneRow()
    {
        ColumnBatch batch = new();
        batch.Reset(1);
        return batch;
    }

    private static async IAsyncEnumerable<T> Async<T>(T item)
    {
        await Task.Yield();
        yield return item;
    }

    /// <summary>The asynchronous entry points: a null they refuse arrives with the task, not as a throw from the call.</summary>
    public static TheoryData<string, string, Func<Task>> AsyncCalls => new()
    {
        { "TabularWriter.WriteBatchAsync", "batch", async () => await InSheet().WriteBatchAsync(null!, CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(chunks)", "writer", async () => await Export.WriteSheetAsync(null!, "data", Async<IReadOnlyList<string>>(["x"]), CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(chunks)", "chunks", async () => await Export.WriteSheetAsync(InSheet(), "data", (IAsyncEnumerable<IReadOnlyList<string>>)null!, CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(messages)", "writer", async () => await Export.WriteSheetAsync(null!, "data", Async(1), _ => ["x"], CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(messages)", "chunks", async () => await Export.WriteSheetAsync(InSheet(), "data", (IAsyncEnumerable<int>)null!, _ => ["x"], CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(messages)", "itemsOf", async () => await Export.WriteSheetAsync(InSheet(), "data", Async(1), (Func<int, IReadOnlyList<string>>)null!, CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(async items)", "writer", async () => await Export.WriteSheetAsync(null!, "data", Async("x"), CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(async items)", "items", async () => await Export.WriteSheetAsync(InSheet(), "data", (IAsyncEnumerable<string>)null!, CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(items)", "writer", async () => await Export.WriteSheetAsync(null!, "data", new[] { "x" }, CancellationToken.None) },
        { "TabularExport.WriteSheetAsync(items)", "items", async () => await Export.WriteSheetAsync(InSheet(), "data", (IEnumerable<string>)null!, CancellationToken.None) },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public void RefusesANullItTakesNone(string call, string parameter, Action invoke)
    {
        ArgumentException error = Assert.ThrowsAny<ArgumentException>(invoke);

        Assert.True(parameter == error.ParamName, $"{call}: refused {error.ParamName ?? "(unnamed)"} for {parameter}");
    }

    [Theory]
    [MemberData(nameof(AsyncCalls))]
    public async Task RefusesANullItTakesNoneAsynchronouslyToo(string call, string parameter, Func<Task> invoke)
    {
        ArgumentException error = await Assert.ThrowsAnyAsync<ArgumentException>(invoke);

        Assert.True(parameter == error.ParamName, $"{call}: refused {error.ParamName ?? "(unnamed)"} for {parameter}");
    }
}
