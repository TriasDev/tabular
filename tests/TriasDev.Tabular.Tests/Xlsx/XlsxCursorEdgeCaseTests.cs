using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// Edge cases drawn from what the established readers had to learn.
/// </summary>
/// <remarks>
/// Most of these come from reading ExcelDataReader's regression suite, whose test names are a
/// catalogue of the files that broke it in production — sheets written by Google Sheets, by
/// OpenOffice, by exporters that omit attributes Excel always writes. What to test is knowledge; the
/// tests themselves are written here.
/// </remarks>
public sealed class XlsxCursorEdgeCaseTests
{
    private static List<string?[]> ReadAll(byte[] content)
    {
        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        List<string?[]> rows = [];

        while (cursor.ReadRow())
        {
            string?[] row = new string?[cursor.CurrentRow.Length];

            for (int i = 0; i < row.Length; i++)
            {
                row[i] = cursor.CurrentRow[i].AsText();
            }

            rows.Add(row);
        }

        return rows;
    }

    [Fact]
    public void ReadsCellsThatCarryNoReferenceAtAll()
    {
        // Some producers omit `r` entirely and rely on order. Excel never does, so a reader that
        // trusts the attribute to be there fails only on other people's files.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c t="inlineStr"><is><t>a</t></is></c><c t="inlineStr"><is><t>b</t></is></c></row><row><c t="inlineStr"><is><t>c</t></is></c><c t="inlineStr"><is><t>d</t></is></c></row>""")
            .Build();

        List<string?[]> rows = ReadAll(content);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new string?[] { "a", "b" }, rows[0]);
        Assert.Equal(new string?[] { "c", "d" }, rows[1]);
    }

    [Fact]
    public void NumbersRowsInOrderWhenTheyCarryNoReference()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c t="inlineStr"><is><t>a</t></is></c></row><row><c t="inlineStr"><is><t>b</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        List<int> numbers = [];

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            numbers.Add(cursor.CurrentRowNumber);
        }

        Assert.Equal([1, 2], numbers);
    }

    [Fact]
    public void AcceptsALowerCaseCellReference()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="a1" t="inlineStr"><is><t>a</t></is></c><c r="c1" t="inlineStr"><is><t>c</t></is></c></row>""")
            .Build();

        Assert.Equal(new string?[] { "a", null, "c" }, Assert.Single(ReadAll(content)));
    }

    [Fact]
    public void PlacesCellsByReferenceEvenWhenTheyAreOutOfOrder()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="C1" t="inlineStr"><is><t>c</t></is></c><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""")
            .Build();

        List<string?[]> rows = ReadAll(content);

        Assert.Equal("a", rows[0][0]);
        Assert.Equal("c", rows[0][2]);
    }

    [Fact]
    public void ReadsADateDeclaredAsAnIso8601String()
    {
        // The `d` cell type: the value is a date written out rather than a serial number. Excel does
        // not emit it; the strict OOXML profile does, and so do several exporters.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="d"><v>2023-01-15T00:00:00</v></c><c r="B1" t="d"><v>2024-06-30</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(RawCellKind.Date, cursor.CurrentRow[0].Kind);
        Assert.Equal(new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Unspecified), cursor.CurrentRow[0].Date);
        Assert.Equal(new DateTime(2024, 6, 30, 0, 0, 0, DateTimeKind.Unspecified), cursor.CurrentRow[1].Date);
    }

    [Fact]
    public void ReadsAStyledCellWhenThePackageCarriesNoStyles()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="3"><v>44927</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(RawCellKind.Number, cursor.CurrentRow[0].Kind);
    }

    [Fact]
    public void DoesNotReadTheTextFormatAsADate()
    {
        // Number format 49 is "@", which renders anything as text. Its identifier sits close to the
        // date formats and is not one.
        byte[] content = new XlsxPackage()
            .WithStyles("""<?xml version="1.0"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" xfId="0"/><xf numFmtId="49" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>""")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="1"><v>44927</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(RawCellKind.Number, cursor.CurrentRow[0].Kind);
    }

    [Fact]
    public void TakesTheLastDefinitionWhenANumberFormatIsDeclaredTwice()
    {
        byte[] content = new XlsxPackage()
            .WithStyles("""<?xml version="1.0"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="2"><numFmt numFmtId="164" formatCode="0.00"/><numFmt numFmtId="164" formatCode="dd\.mm\.yyyy"/></numFmts><fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" xfId="0"/><xf numFmtId="164" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>""")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="1"><v>44927</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(RawCellKind.Date, cursor.CurrentRow[0].Kind);
    }

    [Fact]
    public void ReadsAPackageWhosePartsAreNamedInAnotherCase()
    {
        // Part names in a package are compared without regard to case. Excel writes them lower-case,
        // and other producers do not always.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""")
            .WithPartNaming(path => path.Replace("xl/", "XL/", StringComparison.Ordinal))
            .Build();

        Assert.Equal(new string?[] { "a" }, Assert.Single(ReadAll(content)));
    }

    [Fact]
    public void TreatsACellHoldingOnlyWhitespaceAsEmpty()
    {
        // Including the ideographic space, which arrives in files exported from Japanese systems and
        // is whitespace as far as the runtime is concerned.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">   </t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">　</t></is></c></row>")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.CurrentRow[0].IsEmpty);
        Assert.True(cursor.CurrentRow[1].IsEmpty);
    }

    [Fact]
    public void ReadsARowSplitAcrossSeveralRowElements()
    {
        // One logical row written as two elements carrying the same number. Produced by at least one
        // exporter in the wild, and a reader that trusts one element per row loses half of it.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row><row r="1"><c r="B1" t="inlineStr"><is><t>b</t></is></c></row>""")
            .Build();

        List<string?[]> rows = ReadAll(content);

        // Two elements, so two rows: they are reported as the file writes them, with their own
        // number, rather than merged. What matters is that neither value is lost.
        Assert.Equal(2, rows.Count);
        Assert.Equal("a", rows[0][0]);
        Assert.Equal("b", rows[1][1]);
    }

    [Fact]
    public void ReadsASheetWhoseElementsCarryANamespacePrefix()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<x:row xmlns:x="http://schemas.openxmlformats.org/spreadsheetml/2006/main" r="1"><x:c r="A1" t="inlineStr"><x:is><x:t>a</x:t></x:is></x:c></x:row>""")
            .Build();

        Assert.Equal(new string?[] { "a" }, Assert.Single(ReadAll(content)));
    }

    [Fact]
    public void ReadsAnEmptyCellElement()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1"/><c r="B1" t="inlineStr"><is><t>b</t></is></c></row>""")
            .Build();

        Assert.Equal(new string?[] { null, "b" }, Assert.Single(ReadAll(content)));
    }

    [Fact]
    public void ReadsASheetWithNoRowsAtAll()
    {
        byte[] content = new XlsxPackage().WithSheet("Sheet1", string.Empty).Build();

        Assert.Empty(ReadAll(content));
    }
}
