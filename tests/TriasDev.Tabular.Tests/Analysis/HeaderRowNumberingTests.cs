using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Import;
using TriasDev.Tabular.Mapping;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// <c>HeaderRowIndex</c> names a spreadsheet row, the same numbering every reported row number uses.
/// </summary>
/// <remarks>
/// A workbook does not write empty rows. Counting the rows present, a header on spreadsheet row 3
/// under two blank rows sat at "index 0", so <c>HeaderRowIndex = 2</c> — what a person reading the
/// sheet would say — skipped the header and the first data row.
/// </remarks>
public sealed class HeaderRowNumberingTests
{
    private static byte[] SheetWithHeaderOnRowThree() =>
        new XlsxPackage()
            .WithSheet(
                "Sheet1",
                """<row r="3"><c r="A3" t="inlineStr"><is><t>name</t></is></c></row>"""
                + """<row r="4"><c r="A4" t="inlineStr"><is><t>first</t></is></c></row>"""
                + """<row r="5"><c r="A5" t="inlineStr"><is><t>second</t></is></c></row>""")
            .Build();

    [Fact]
    public void AnalysesUnderTheHeaderOnTheSpreadsheetRowItNames()
    {
        using MemoryStream stream = new(SheetWithHeaderOnRowThree(), writable: false);
        using XlsxCursor cursor = new(stream);

        SheetProfile sheet = new TabularAnalyzer(new AnalysisOptions { HeaderRowIndex = 2 })
            .Analyze(cursor, TestContext.Current.CancellationToken).Sheets[0];

        Assert.Equal("name", Assert.Single(sheet.Columns).Facts.Header);
        Assert.Equal(2, sheet.RowCount);
    }

    [Fact]
    public void ImportsFromTheRowBelowThatHeader()
    {
        TextField name = ImportField.Text("name");
        using MemoryStream stream = new(SheetWithHeaderOnRowThree(), writable: false);
        using XlsxCursor cursor = new(stream);

        using ImportRun<string?> run = TabularImporter.Import(
            cursor,
            new MappingPlan
            {
                HeaderRowIndex = 2,
                Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "name", TargetFieldName = "name" }],
            },
            new TargetSchema { Fields = [name] },
            row => row[name],
            cancellationToken: TestContext.Current.CancellationToken);

        List<ImportOutcome<string?>> outcomes = [.. run];

        Assert.Equal(["first", "second"], outcomes.Select(o => o.Value));
        Assert.Equal([4, 5], outcomes.Select(o => o.RowNumber));
    }

    [Fact]
    public void TakesTheFirstRowWithContentWhenTheNamedRowIsEmpty()
    {
        // Row 2 is blank and therefore absent from the file; the header is the next row that exists.
        using MemoryStream stream = new(SheetWithHeaderOnRowThree(), writable: false);
        using XlsxCursor cursor = new(stream);

        SheetProfile sheet = new TabularAnalyzer(new AnalysisOptions { HeaderRowIndex = 1 })
            .Analyze(cursor, TestContext.Current.CancellationToken).Sheets[0];

        Assert.Equal("name", Assert.Single(sheet.Columns).Facts.Header);
    }
}
