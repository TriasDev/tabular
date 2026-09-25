using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>
/// A plan that recorded which sheet it was built for refuses a file where another sheet stands at
/// that index — the sheet-level twin of a changed header.
/// </summary>
public sealed class SheetIdentityTests
{
    private static readonly TargetSchema Schema = new() { Fields = [ImportField.Text("name")] };

    private static MappingPlan Plan(string? sheetName = null, string? sheetSource = null, int sheetIndex = 0) => new()
    {
        SheetIndex = sheetIndex,
        SheetName = sheetName,
        SheetSource = sheetSource,
        Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "name", TargetFieldName = "name" }],
    };

    private static XlsxCursor Workbook() =>
        new(new MemoryStream(new XlsxPackage()
            .WithSheet("Orders", """<row r="1"><c r="A1" t="inlineStr"><is><t>name</t></is></c></row>""")
            .WithSheet("Returns", """<row r="1"><c r="A1" t="inlineStr"><is><t>name</t></is></c></row>""")
            .Build()), cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public void AcceptsTheSheetThePlanRecorded()
    {
        using XlsxCursor cursor = Workbook();

        ExtractionSession session = TabularExtractor.Start(cursor, Plan("Returns", sheetIndex: 1), Schema, cancellationToken: TestContext.Current.CancellationToken);

        // Started, positioned past the header, and at the end of a sheet that holds nothing else.
        Assert.False(session.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesAnotherSheetStandingAtTheIndex()
    {
        using XlsxCursor cursor = Workbook();

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan("Returns", sheetIndex: 0), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.SheetChanged, error.Code);
        Assert.Equal(0, error.SheetIndex);
    }

    [Fact]
    public void ComparesSheetNamesOrdinally()
    {
        using XlsxCursor cursor = Workbook();

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan("orders"), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.SheetChanged, error.Code);
    }

    [Fact]
    public void RefusesASourceThePlainFileDoesNotHave()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("name\na\n")), "t.csv");

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan(sheetSource: "export/t.csv"), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.SheetChanged, error.Code);
    }

    [Fact]
    public void DoesNotCheckAPlanThatRecordedNoSheet()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("name\na\n")), "renamed.csv");

        ExtractionSession session = TabularExtractor.Start(cursor, Plan(), Schema, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(session.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReportsAMissingSheetAsMissingNotChanged()
    {
        using XlsxCursor cursor = Workbook();

        TabularStructureException error = Assert.Throws<TabularStructureException>(
            () => TabularExtractor.Start(cursor, Plan("Orders", sheetIndex: 5), Schema, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularStructureException.SheetMissing, error.Code);
    }

    [Fact]
    public void ThePrecheckBlocksAPlanForAnotherSheet()
    {
        // Not "undetermined": analysing the file again finds the same other sheet there, and the
        // import is certain to refuse it. Nothing about the columns is judged — they belong to a
        // sheet the plan was not built for.
        using XlsxCursor cursor = Workbook();
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        PrecheckResult result = MappingPrecheck.Check(Plan("Returns", sheetIndex: 0), Schema, profile);

        PrecheckFinding finding = Assert.Single(result.Findings);
        Assert.Equal((ErrorCodes.Structure.SheetChanged, PrecheckSeverity.Blocking), (finding.Code, finding.Severity));
        Assert.False(result.CanImport);
    }
}
