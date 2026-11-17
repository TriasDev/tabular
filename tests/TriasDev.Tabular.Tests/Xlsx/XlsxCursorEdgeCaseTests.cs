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
    public void PlacesACellWithoutAReferenceAfterTheCellBeforeIt()
    {
        // What the writer does: a reference only where a cell does not follow the one before it.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="B1" t="inlineStr"><is><t>b</t></is></c><c t="inlineStr"><is><t>c</t></is></c><c r="F1" t="inlineStr"><is><t>f</t></is></c><c t="inlineStr"><is><t>g</t></is></c></row>""")
            .Build();

        Assert.Equal(new string?[] { null, "b", "c", null, null, "f", "g" }, Assert.Single(ReadAll(content)));
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
    public void ReadsADateCellHoldingOnlyWhitespaceAsEmpty()
    {
        // The zone check looks at the last character of the trimmed value, of which there is none.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="d"><v> </v></c><c r="B1"><v>1</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.CurrentRow[0].IsEmpty);
        Assert.Equal(1, cursor.CurrentRow[1].Number);
    }

    [Fact]
    public void ReadsATimeDeclaredAsAnIso8601StringOnTheDateABareTimeHas()
    {
        // A time with no date, written out: the same instant as the serial 0.5, so it reads on the
        // same day — not on year one, where a parser without a date puts it.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="d"><v>12:00:00.000</v></c><c r="B1" s="1"><v>0.5</v></c><c r="C1" t="d"><v>0001-01-01T12:00:00</v></c></row>""")
            .WithStyles("""<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="21" applyNumberFormat="1"/></cellXfs></styleSheet>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(cursor.CurrentRow[1], cursor.CurrentRow[0]);
        Assert.Equal(new DateTime(1899, 12, 31, 12, 0, 0, DateTimeKind.Unspecified), cursor.CurrentRow[0].Date);
        Assert.Equal(new DateTime(1, 1, 1, 12, 0, 0, DateTimeKind.Unspecified), cursor.CurrentRow[2].Date);
    }

    [Theory]
    [InlineData("9:30 PM", "21:30")]
    [InlineData("9:30pm", "21:30")]
    [InlineData("12:15 AM", "00:15")]
    [InlineData("12:00 PM", "12:00")]
    [InlineData("09:30:15 AM", "09:30:15")]
    [InlineData("9:30 PM +02:00", "21:30+02:00")]
    public void ReadsATwelveHourTimeOnTheDateABareTimeHas(string twelveHour, string twentyFourHour)
    {
        // Not ISO 8601, but parsed all the same; read at all, it must be the same value as the time
        // written the other way round, not that time on year one.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", $"""<row r="1"><c r="A1" t="d"><v>{twelveHour}</v></c><c r="B1" t="d"><v>{twentyFourHour}</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(1899, cursor.CurrentRow[0].Date.Year);
        Assert.Equal(cursor.CurrentRow[1], cursor.CurrentRow[0]);
    }

    [Fact]
    public void KeepsTheDateOfAWrittenOutValueThatOpensWithItsTime()
    {
        // Out of the format's spec but parsed all the same: the time comes first and a date follows.
        // Shaped like a bare time at its start, it is not one, and its date must survive.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="d"><v>12:00 2024-01-01</v></c><c r="B1" t="d"><v>12:00:00+02:00</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Unspecified), cursor.CurrentRow[0].Date);
        Assert.Equal(new DateTime(1899, 12, 31, 12, 0, 0, DateTimeKind.Unspecified), cursor.CurrentRow[1].Date);
    }

    [Fact]
    public void ReadsAWrittenOutTimeOnTheDateABareTimeHasInA1904Workbook()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="d"><v>6:30</v></c><c r="B1" s="1"><v>0.2708333333333333</v></c></row>""")
            .WithStyles("""<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="21" applyNumberFormat="1"/></cellXfs></styleSheet>""")
            .WithDate1904()
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(new DateTime(1904, 1, 1, 6, 30, 0, DateTimeKind.Unspecified), cursor.CurrentRow[0].Date);
        Assert.Equal(cursor.CurrentRow[1], cursor.CurrentRow[0]);
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
    public void KeepsACellFormatWhenAConditionalFormatReusesItsId()
    {
        // A differential format for conditional formatting carries its own numFmt, and its id may be
        // one the cell formats already use. Taken as a cell format, "yyyy-mm-dd" overwrote "0.00" and
        // an amount of 45000.25 read as 15 March 2023 (#48).
        byte[] content = new XlsxPackage()
            .WithStyles("""<?xml version="1.0"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="0.00"/></numFmts><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="164" applyNumberFormat="1"/></cellXfs><dxfs count="1"><dxf><numFmt numFmtId="164" formatCode="yyyy-mm-dd"/></dxf></dxfs></styleSheet>""")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="1"><v>45000.25</v></c></row>""")
            .Build();

        using XlsxCursor cursor = new(new MemoryStream(content, writable: false));

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(RawCell.FromNumber(45000.25), cursor.CurrentRow[0]);
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

    [Theory]
    [InlineData("<t> </t>")]
    [InlineData("<t xml:space=\"preserve\"> </t>")]
    public void KeepsAWhitespaceOnlyRunOfASharedStringAsTheInlineOneKeepsIt(string run)
    {
        // A run of formatted text that is only a space, written without xml:space by some producers:
        // the space is the text's, not the markup's, and dropping it joined "Max" and "Mustermann".
        string runs = $"<r><rPr><b/></rPr><t>Max</t></r><r>{run}</r><r><t>Mustermann</t></r>";
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", $"""<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is>{runs}</is></c></row>""")
            .WithSharedStrings($"<si>{runs}</si>")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("Max Mustermann", cursor.CurrentRow[0].Text);
        Assert.Equal(cursor.CurrentRow[1], cursor.CurrentRow[0]);
    }

    [Fact]
    public void ReadsAnIndentedSharedStringTableWithoutItsIndentation()
    {
        // The whitespace between the table's elements is the markup's; only what stands in a <t> is text.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>""")
            .WithSharedStrings("\n  <si>\n    <t>a</t>\n  </si>\n  <si>\n    <r>\n      <t>b</t>\n    </r>\n    <r>\n      <t>c</t>\n    </r>\n  </si>\n")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(["a", "bc"], [cursor.CurrentRow[0].Text, cursor.CurrentRow[1].Text]);
    }

    [Fact]
    public void ReadsAnIndentedInlineStringWithoutItsIndentation()
    {
        // As in the shared string table: the whitespace before, between and after the runs is the markup's.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\">\n  <is>\n    <r>\n      <rPr><b/></rPr>\n      <t>a</t>\n    </r>\n    <r>\n      <t>b</t>\n    </r>\n  </is>\n</c><c r=\"B1\" t=\"inlineStr\"><is>\n  <t>c</t>\n</is></c></row>")
            .Build();

        Assert.Equal(new string?[] { "ab", "c" }, Assert.Single(ReadAll(content)));
    }

    [Fact]
    public void ReadsAnInlineStringCellWhoseValueStandsInAV()
    {
        // t="inlineStr" with <v> instead of <is>, as some exporters write it: still text, never the number it looks like.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><v>007</v></c><c r="B1" t="inlineStr"><v>a_x0042_</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(RawCell.FromText("007"), cursor.CurrentRow[0]);
        Assert.Equal(RawCell.FromText("aB"), cursor.CurrentRow[1]);
    }

    [Fact]
    public void ReadsClosingTagsWithWhitespaceBeforeTheBracket()
    {
        // XML allows whitespace between a closing tag's name and its '>'; the name is the name without it.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>a</t ></is\t></c\n><c r=\"B1\"><v>2</v  ></c \r\n></row ><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>b</t></is></c></row>")
            .Build();

        List<string?[]> rows = ReadAll(content);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new string?[] { "a", "2" }, rows[0]);
        Assert.Equal(new string?[] { "b" }, rows[1]);
    }

    [Fact]
    public void ReadsALongTextWithAnEntityPastTwiceTheDecodeBuffer()
    {
        // A text node with an entity is decoded into a scratch buffer, which grows to the text's length
        // even when that is more than double what it held.
        string text = new string('x', 1_000) + "&amp;" + new string('y', 1_000);
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", $"""<row r="1"><c r="A1" t="inlineStr"><is><t>{text}</t></is></c></row>""")
            .Build();

        Assert.Equal(new string?[] { new string('x', 1_000) + "&" + new string('y', 1_000) }, Assert.Single(ReadAll(content)));
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

    [Fact]
    public void ReadsTheLastMillisecondOfTheLastDayExactly()
    {
        Assert.True(XlsxCursor.TryFromSerial(2958465.999999988426, date1904: false, out DateTime date));

        Assert.Equal(new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Unspecified).Ticks, date.Ticks);
    }

    [Fact]
    public void ReadsALateMidRangeDateTimeToTheExactMillisecond()
    {
        DateTime expected = new(2500, 6, 15, 13, 45, 30, 123, DateTimeKind.Unspecified);
        double serial = (expected.Ticks - new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified).Ticks) / TimeSpan.TicksPerMillisecond / 86_400_000d;

        Assert.True(XlsxCursor.TryFromSerial(serial, date1904: false, out DateTime date));

        Assert.Equal(expected.Ticks, date.Ticks);
    }

    [Fact]
    public void ReadsANumberThatIsNoFiniteNumberAsItsTextAsItDoesAnUnparseableOne()
    {
        // double.TryParse accepts NaN and Infinity, and reads 1e400 as Infinity. None of them is a
        // number a spreadsheet holds, and as Number cells they would reach the analysis' sums. They
        // read as an unparseable value does — as their text — and the ods reader agrees.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", "<row><c><v>NaN</v></c><c><v>Infinity</v></c><c><v>-Infinity</v></c><c><v>1e400</v></c><c><v>abc</v></c><c><v>1e300</v></c></row>")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(
            [RawCell.FromText("NaN"), RawCell.FromText("Infinity"), RawCell.FromText("-Infinity"), RawCell.FromText("1e400"), RawCell.FromText("abc"), RawCell.FromNumber(1e300)],
            cursor.CurrentRow.ToArray());
    }

    /// <summary>Every row's cells, read under a deadline, so a reader that loops fails instead of hanging the run.</summary>
    private static List<RawCell[]> Cells(byte[] content) =>
        Deadline.Within(() =>
        {
            using XlsxCursor cursor = new(new MemoryStream(content, writable: false));
            List<RawCell[]> rows = [];

            while (cursor.ReadRow(TestContext.Current.CancellationToken))
            {
                rows.Add(cursor.CurrentRow.ToArray());
            }

            return rows;
        });

    private const string DateStyleAtZero =
        """<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="14" applyNumberFormat="1"/><xf numFmtId="0"/></cellXfs></styleSheet>""";

    [Fact]
    public void ReadsAttributesWrittenWithWhitespaceAroundTheirEqualsSign()
    {
        // XML allows white space on either side of '=', spaces, tabs and line ends alike. Skipping it
        // badly took the space for the opening quote, or did not move past it at all and spun.
        byte[] content = new XlsxPackage()
            .WithSharedStrings("<si><t>shared</t></si>")
            .WithStyles(DateStyleAtZero)
            .WithSheet(
                "Sheet1",
                "<row r = \"1\"><c r = \"B1\" t =\"inlineStr\"><is><t>b</t></is></c><c r\t=\t'C1'\nt\n=\n's'><v>0</v></c><c r=  \"D1\"  s  =  \"0\"><v>45000</v></c></row>")
            .Build();

        RawCell[] row = Assert.Single(Cells(content));

        Assert.Equal(
            [RawCell.Empty, RawCell.FromText("b"), RawCell.FromText("shared"), RawCell.FromDate(new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Unspecified))],
            row);
    }

    [Fact]
    public void KeepsSayingTheSheetHasEndedOnceItHas()
    {
        // The end of the sheet data is remembered: what follows it is not markup the cursor reads,
        // and reading on from there found the part's end and called the sheet truncated.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1"><v>1</v></c></row>""")
            .Build();

        using XlsxCursor cursor = new(new MemoryStream(content, writable: false));

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadsPastElementsOfARowThatAreNotCells()
    {
        // An extension list inside a row, between cells and after the last one. Only <c> is a cell
        // and only </row> ends the row: taking the list for a cell read the next row's value into
        // this one, and taking its end for the row's lost the cells after it.
        byte[] content = new XlsxPackage()
            .WithSheet(
                "Sheet1",
                """<row r="1"><c r="A1"><v>1</v></c><extLst><ext uri="{0}"><x14:id>7</x14:id></ext></extLst><c r="B1"><v>2</v></c><extLst><ext uri="{1}"/></extLst></row><row r="2"><c r="A2"><v>3</v></c></row>""")
            .Build();

        List<RawCell[]> rows = Cells(content);

        Assert.Equal(2, rows.Count);
        Assert.Equal([RawCell.FromNumber(1), RawCell.FromNumber(2)], rows[0]);
        Assert.Equal([RawCell.FromNumber(3)], rows[1]);
    }

    [Fact]
    public void ReadsStyleZeroAsADateWhenItsFormatIsOne()
    {
        // The first cell format is as much a format as any other; an index of zero is not "no style".
        byte[] content = new XlsxPackage()
            .WithStyles(DateStyleAtZero)
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="0"><v>45000</v></c><c r="B1" s="1"><v>45000</v></c></row>""")
            .Build();

        Assert.Equal(
            [RawCell.FromDate(new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Unspecified)), RawCell.FromNumber(45000)],
            Assert.Single(Cells(content)));
    }

    [Fact]
    public void ReadsAStyleOrSharedStringIndexJustPastItsTableAsNoneOfIt()
    {
        // An index equal to the table's length names nothing; it is read as a plain number and an
        // empty cell, not looked up one past the end.
        byte[] content = new XlsxPackage()
            .WithStyles(DateStyleAtZero)
            .WithSharedStrings("<si><t>a</t></si><si><t>b</t></si>")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="2"><v>45000</v></c><c r="B1" t="s"><v>2</v></c><c r="C1" t="s"><v>1</v></c></row>""")
            .Build();

        Assert.Equal(
            [RawCell.FromNumber(45000), RawCell.Empty, RawCell.FromText("b")],
            Assert.Single(Cells(content)));
    }

    [Fact]
    public void ReadsASharedStringCellAsEmptyWhenThePackageHasNoTable()
    {
        // A workbook that writes every string inline has no shared string table, and a cell that
        // points into one anyway names nothing.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is><t>x</t></is></c></row>""")
            .Build();

        Assert.Equal([RawCell.Empty, RawCell.FromText("x")], Assert.Single(Cells(content)));
    }

    [Fact]
    public void ReadsASharedStringPastACommentOrInstructionInsideItsText()
    {
        // Only text nodes are the value; a comment's text, or an instruction's, is not part of it.
        byte[] content = new XlsxPackage()
            .WithSharedStrings("<si><t>a<!-- note -->b<?pi data?>c</t></si><si><t>d</t></si>")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>""")
            .Build();

        Assert.Equal([RawCell.FromText("abc"), RawCell.FromText("d")], Assert.Single(Cells(content)));
    }

    [Fact]
    public void ReadsARowOfMoreCellsThanTheFirstBufferHolds()
    {
        // Seventeen cells: one past the sixteen the row buffer starts with, so it has to grow for the
        // last. The wide export tests cover this too, but slowly.
        string cells = string.Concat(Enumerable.Range(0, 17).Select(i => $"""<c r="{(char)('A' + i)}1"><v>{i}</v></c>"""));
        byte[] content = new XlsxPackage().WithSheet("Sheet1", $"""<row r="1">{cells}</row>""").Build();

        Assert.Equal(
            Enumerable.Range(0, 17).Select(i => RawCell.FromNumber(i)),
            Assert.Single(Cells(content)));
    }

    [Fact]
    public void ReadsTheFirstDayOfTheCalendarFromItsSerialAndLeavesTheDayBeforeANumber()
    {
        // The calendar's edge is the only bound on a serial: the earliest day a DateTime holds is a
        // date, a moment before it is no date and keeps its number.
        double first = -(new DateTime(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified) - DateTime.MinValue).TotalDays;

        Assert.True(XlsxCursor.TryFromSerial(first, date1904: false, out DateTime date));
        Assert.Equal(DateTime.MinValue, date);
        Assert.False(XlsxCursor.TryFromSerial(first - 0.001, date1904: false, out _));
    }
}
