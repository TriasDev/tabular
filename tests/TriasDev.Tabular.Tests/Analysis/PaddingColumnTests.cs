using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// Columns that hold nothing — no header, no value in any row — to the right of a table are padding,
/// as blank rows below it are, and are not reported (#45).
/// </summary>
/// <remarks>
/// A workbook writes a cell for its formatting alone, and people format whole rows or a block wider
/// than their data: a real 13-column table came out as 23 columns, and a consumer refusing an empty
/// header refused a perfectly good file. An empty column between two real ones is kept, so that every
/// column keeps the index a mapping addresses it by.
/// </remarks>
public sealed class PaddingColumnTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static SheetProfile Analyze(ITabularCursor cursor)
    {
        using (cursor)
        {
            return TabularAnalyzer.Analyze(cursor, cancellationToken: Token).Sheets[0];
        }
    }

    private static CsvCursor Csv(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), "t.csv");

    [Fact]
    public void LeavesOutStyledEmptyCellsToTheRightOfAWorkbookTable()
    {
        const string Styled = """<c r="C{0}" s="1"/><c r="D{0}" s="1"/><c r="E{0}" s="1"/>""";
        string rows = $"""<row r="1"><c r="A1" t="inlineStr"><is><t>id</t></is></c><c r="B1" t="inlineStr"><is><t>name</t></is></c>{string.Format(null, Styled, 1)}</row>"""
            + string.Concat(Enumerable.Range(2, 3).Select(r => $"""<row r="{r}"><c r="A{r}"><v>{r}</v></c><c r="B{r}" t="inlineStr"><is><t>n{r}</t></is></c>{string.Format(null, Styled, r)}</row>"""));
        byte[] workbook = new XlsxPackage()
            .WithStyles("""<?xml version="1.0"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fills count="2"><fill/><fill><patternFill patternType="solid"/></fill></fills><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="0" fillId="1" applyFill="1"/></cellXfs></styleSheet>""")
            .WithSheet("S", rows)
            .Build();

        SheetProfile sheet = Analyze(new XlsxCursor(new MemoryStream(workbook), cancellationToken: Token));

        Assert.Equal(["id", "name"], sheet.Columns.Select(c => c.Facts.Header));
        Assert.Equal(3, sheet.RowCount);
    }

    [Fact]
    public void LeavesOutTheRunOfDelimitersACsvLineEndsIn()
    {
        SheetProfile sheet = Analyze(Csv("id;name;;;\n1;a;;;\n2;b;;;\n"));

        Assert.Equal(["id", "name"], sheet.Columns.Select(c => c.Facts.Header));
    }

    [Fact]
    public void LeavesOutRepeatedEmptyCellsToTheRightOfAnOpenDocumentTable()
    {
        string Row(string a, string b) =>
            $"""<table:table-row><table:table-cell office:value-type="string"><text:p>{a}</text:p></table:table-cell><table:table-cell office:value-type="string"><text:p>{b}</text:p></table:table-cell><table:table-cell table:style-name="ce1" table:number-columns-repeated="8"/></table:table-row>""";
        byte[] file = new OdsPackage().WithTable("S", Row("id", "name") + Row("1", "a")).Build();

        SheetProfile sheet = Analyze(new OdsCursor(new MemoryStream(file), cancellationToken: Token));

        Assert.Equal(["id", "name"], sheet.Columns.Select(c => c.Facts.Header));
    }

    [Fact]
    public void KeepsAnEmptyColumnBetweenTwoRealOnesSoIndexesHold()
    {
        SheetProfile sheet = Analyze(Csv("id;;name;;\n1;;a;;\n2;;b;;\n"));

        Assert.Equal(["id", "", "name"], sheet.Columns.Select(c => c.Facts.Header));
        Assert.Equal([0, 1, 2], sheet.Columns.Select(c => c.Facts.Index));
        Assert.Equal(2, sheet.Columns[1].Facts.EmptyCount);
    }

    [Fact]
    public void KeepsAColumnWithoutAHeaderThatCarriesAValue()
    {
        // Data, whatever its header says: the consumer decides what to make of it.
        SheetProfile sheet = Analyze(Csv("id;name;;\n1;a;;\n2;b;x;\n3;c;;\n"));

        Assert.Equal(["id", "name", ""], sheet.Columns.Select(c => c.Facts.Header));
        Assert.Equal(1, sheet.Columns[2].Facts.NonEmptyCount);
        Assert.Equal(2, sheet.Columns[2].Facts.EmptyCount);
    }

    [Fact]
    public void KeepsAColumnWithAHeaderAndNoValues()
    {
        SheetProfile sheet = Analyze(Csv("id;name;note;;\n1;a;;;\n2;b;;;\n"));

        Assert.Equal(["id", "name", "note"], sheet.Columns.Select(c => c.Facts.Header));
        Assert.Equal(2, sheet.Columns[2].Facts.EmptyCount);
    }
}
