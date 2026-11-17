using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Ods;

/// <summary>An OpenDocument spreadsheet read as a workbook: sheets, rows and the cells' own types.</summary>
public sealed class OdsCursorTests
{
    private static OdsCursor Open(OdsPackage package, OdsCursorOptions? options = null) =>
        new(new MemoryStream(package.Build(), writable: false), options, cancellationToken: TestContext.Current.CancellationToken);

    private static List<RawCell[]> ReadAll(OdsCursor cursor)
    {
        List<RawCell[]> rows = [];

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static string Cell(string attributes, string paragraph = "") =>
        $"<table:table-cell {attributes}>{(paragraph.Length > 0 ? $"<text:p>{paragraph}</text:p>" : "")}</table:table-cell>";

    [Fact]
    public void ReadsEachCellAsTheTypeTheFileDeclares()
    {
        string row = "<table:table-row>"
            + Cell("office:value-type=\"string\"", "Contoso")
            + Cell("office:value-type=\"float\" office:value=\"1234.5\"", "1.234,50")
            + Cell("office:value-type=\"percentage\" office:value=\"0.25\"", "25%")
            + Cell("office:value-type=\"currency\" office:currency=\"EUR\" office:value=\"9.99\"", "9,99 €")
            + Cell("office:value-type=\"date\" office:date-value=\"2024-02-29\"", "29.02.2024")
            + Cell("office:value-type=\"date\" office:date-value=\"2024-02-29T13:45:30\"", "29.02.2024 13:45")
            + Cell("office:value-type=\"time\" office:time-value=\"PT12H30M00S\"", "12:30")
            + Cell("office:value-type=\"boolean\" office:boolean-value=\"true\"", "WAHR")
            + Cell("table:formula=\"of:=[.B1]*2\" office:value-type=\"float\" office:value=\"2469\"", "2469")
            + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", row));
        RawCell[] cells = Assert.Single(ReadAll(cursor));

        Assert.Equal(RawCell.FromText("Contoso"), cells[0]);
        Assert.Equal(RawCell.FromNumber(1234.5), cells[1]);
        Assert.Equal(RawCell.FromNumber(0.25), cells[2]);
        Assert.Equal(RawCell.FromNumber(9.99), cells[3]);
        Assert.Equal(RawCell.FromDate(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Unspecified)), cells[4]);
        Assert.Equal(RawCell.FromDate(new DateTime(2024, 2, 29, 13, 45, 30, DateTimeKind.Unspecified)), cells[5]);
        Assert.Equal(RawCell.FromDate(new DateTime(1899, 12, 31, 12, 30, 0, DateTimeKind.Unspecified)), cells[6]);
        Assert.Equal(RawCell.FromBoolean(true), cells[7]);
        Assert.Equal(RawCell.FromNumber(2469), cells[8]);
    }

    [Fact]
    public void ListsTheSheetsInFileOrderAndMovesBetweenThem()
    {
        OdsPackage package = new OdsPackage()
            .WithTable("Orders", "<table:table-row>" + Cell("office:value-type=\"string\"", "o") + "</table:table-row>")
            .WithTable("Returns", "<table:table-row>" + Cell("office:value-type=\"string\"", "r") + "</table:table-row>");

        using OdsCursor cursor = Open(package);

        Assert.Equal(["Orders", "Returns"], cursor.Sheets.Select(s => s.Name));
        Assert.All(cursor.Sheets, s => Assert.Equal(TabularFormat.Ods, s.Format));
        Assert.Equal(TabularFormat.Ods, cursor.Format);

        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal("r", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.True(cursor.MoveToSheet(0, TestContext.Current.CancellationToken));
        Assert.Equal("o", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.False(cursor.MoveToSheet(2, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamesTheSheetsAsTheirMarkupMeansAndCountsOnlyRealTables(bool utf16)
    {
        // The names are listed by a pass of their own over the part; it must see exactly the tables
        // the reading does — not one mentioned in a comment or CDATA, not a tag cut short by a '>'
        // inside an attribute — and read names with entities and non-ASCII characters as written.
        static string Row(string text) => "<table:table-row>" + Cell("office:value-type=\"string\"", text) + "</table:table-row>";
        OdsPackage package = new OdsPackage()
            .WithTable("R&amp;D &lt;2024&gt; &#x263A;", "<!-- <table:table table:name=\"comment\"> -->" + Row("first"))
            .WithTable("Übersicht\" table:style-name=\"a>b", "<![CDATA[<table:table table:name=\"cdata\">]]>" + Row("second"))
            .WithRawTable("<table:table>" + Row("third") + "</table:table>");

        using OdsCursor cursor = Open(utf16 ? package.WithContentEncoding(Encoding.Unicode) : package);

        Assert.Equal(["R&D <2024> ☺", "Übersicht", ""], cursor.Sheets.Select(s => s.Name));
        Assert.True(cursor.MoveToSheet(2, TestContext.Current.CancellationToken));
        Assert.Equal("third", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal("second", Assert.Single(ReadAll(cursor))[0].AsText());
    }

    /// <summary>
    /// The name pass takes a table's start tag apart itself, and must find the name the reading
    /// finds behind whatever stands ahead of it: a '>' inside an attribute's value, an empty value,
    /// or a name in single quotes.
    /// </summary>
    [Theory]
    [InlineData("<table:table table:style-name=\"a>b\" table:name=\"Real\">", "Real")]
    [InlineData("<table:table table:style-name=\"\" table:name=\"AfterEmpty\">", "AfterEmpty")]
    [InlineData("<table:table table:name='Single'>", "Single")]
    public void NamesASheetWhateverTheAttributesAheadOfItsNameHold(string startTag, string name)
    {
        static string Row(string text) => "<table:table-row>" + Cell("office:value-type=\"string\"", text) + "</table:table-row>";
        using OdsCursor cursor = Open(new OdsPackage().WithRawTable(startTag + Row("a") + "</table:table>").WithTable("B", Row("b")));

        Assert.Equal([name, "B"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal("a", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal("b", Assert.Single(ReadAll(cursor))[0].AsText());
    }

    [Fact]
    public void ListsTheSheetsOfAContentPartWrittenInUtf16()
    {
        // The name pass reads bytes as UTF-8; a part in UTF-16 is recognised by its byte order mark
        // and tokenized instead, as the reading will.
        OdsPackage package = new OdsPackage()
            .WithTable("Übersicht", "<table:table-row>" + Cell("office:value-type=\"string\"", "ä") + "</table:table-row>")
            .WithTable("Zweite", "")
            .WithContentEncoding(Encoding.Unicode);

        using OdsCursor cursor = Open(package);

        Assert.Equal(["Übersicht", "Zweite"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal("ä", Assert.Single(ReadAll(cursor))[0].AsText());
    }

    /// <summary>
    /// A table element without content, written as an empty tag, holds no rows and is no sheet: the
    /// list leaves it out in either encoding, and moving to the sheet behind it reads that sheet's rows
    /// rather than refusing the file because the two passes counted differently.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListsNoSheetForATableWithoutContent(bool utf16)
    {
        static string Row(string text) => "<table:table-row>" + Cell("office:value-type=\"string\"", text) + "</table:table-row>";
        OdsPackage package = new OdsPackage()
            .WithTable("A", Row("a"))
            .WithRawTable("<table:table table:name=\"empty\"/>")
            .WithRawTable("<table:table table:name=\"spaced\" />")
            .WithTable("B", Row("b"));

        using OdsCursor cursor = Open(utf16 ? package.WithContentEncoding(Encoding.Unicode) : package);

        Assert.Equal(["A", "B"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal("a", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal("b", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.True(cursor.MoveToSheet(0, TestContext.Current.CancellationToken));
        Assert.Equal("a", Assert.Single(ReadAll(cursor))[0].AsText());
    }

    /// <summary>
    /// Once a sheet has ended, reading on finds no more rows, however often it is asked: the scanner
    /// stands before the next sheet, whose rows are that sheet's and reached only by moving to it.
    /// </summary>
    [Fact]
    public void ReadsNoRowOnceTheSheetHasEndedAndNeverTheNextSheets()
    {
        static string Row(string text) => "<table:table-row>" + Cell("office:value-type=\"string\"", text) + "</table:table-row>";
        using OdsCursor cursor = Open(new OdsPackage().WithTable("A", Row("a1") + Row("a2")).WithTable("B", Row("b1")));

        Assert.Equal(["a1", "a2"], ReadAll(cursor).Select(r => r[0].AsText()));
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.CurrentRow.IsEmpty);
        Assert.Equal(0, cursor.CurrentSheetIndex);

        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal("b1", Assert.Single(ReadAll(cursor))[0].AsText());
    }

    [Fact]
    public void MovesEmptyRepeatsAlongWithoutExpandingThem()
    {
        // LibreOffice writes "the rest of the sheet is empty" as one element of a million rows.
        string rows =
            "<table:table-row>" + "<table:table-cell table:number-columns-repeated=\"5000\"/>" + Cell("office:value-type=\"string\"", "far") + "</table:table-row>"
            + "<table:table-row table:number-rows-repeated=\"1000\"><table:table-cell table:number-columns-repeated=\"1024\"/></table:table-row>"
            + "<table:table-row>" + Cell("office:value-type=\"string\"", "after") + "</table:table-row>"
            + "<table:table-row table:number-rows-repeated=\"1048000\"><table:table-cell table:number-columns-repeated=\"16384\"/></table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows));

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(5001, cursor.CurrentRow.Length);
        Assert.Equal("far", cursor.CurrentRow[5000].AsText());
        Assert.Equal(1, cursor.CurrentRowNumber);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(1002, cursor.CurrentRowNumber);
        Assert.Equal("after", cursor.CurrentRow[0].AsText());

        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExpandsARepeatedCellOrRowThatHoldsAValue()
    {
        string rows = "<table:table-row table:number-rows-repeated=\"3\">"
            + "<table:table-cell office:value-type=\"float\" office:value=\"7\" table:number-columns-repeated=\"2\"><text:p>7</text:p></table:table-cell>"
            + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows));
        List<RawCell[]> read = ReadAll(cursor);

        Assert.Equal(3, read.Count);
        Assert.All(read, r => Assert.Equal([RawCell.FromNumber(7), RawCell.FromNumber(7)], r));
    }

    [Fact]
    public void AssemblesACellsTextFromItsParagraphsAndSpans()
    {
        string cell = "<table:table-cell office:value-type=\"string\">"
            + "<office:annotation><dc:creator>someone</dc:creator><text:p>a note</text:p></office:annotation>"
            + "<text:p>Haupt<text:span text:style-name=\"T1\">straße</text:span><text:s text:c=\"3\"/>1<text:tab/>A</text:p>"
            + "<text:p>Hinterhaus<text:line-break/>2. OG<text:s/>links</text:p>"
            + "</table:table-cell>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", "<table:table-row>" + cell + "</table:table-row>"));

        Assert.Equal("Hauptstraße   1\tA\nHinterhaus\n2. OG links", Assert.Single(ReadAll(cursor))[0].AsText());
    }

    /// <summary>
    /// A comment is not the cell's text wherever it stands among the paragraphs, empty or not: its
    /// paragraphs add no line, and its spaces, tabs and line breaks add nothing, while the paragraphs
    /// around it, and the rest of a paragraph it stands in, are read in full.
    /// </summary>
    [Theory]
    [InlineData("<text:p>a</text:p><office:annotation><dc:creator>x</dc:creator><text:p>note<text:s/>1<text:tab/>2<text:line-break/>3</text:p><text:p>more</text:p></office:annotation><text:p>b</text:p>", "a\nb")]
    [InlineData("<text:p>a</text:p><office:annotation/><text:p>b</text:p>", "a\nb")]
    [InlineData("<office:annotation/><text:p>a</text:p><text:p>b</text:p>", "a\nb")]
    [InlineData("<text:p>a<office:annotation><text:p>note</text:p></office:annotation>b</text:p>", "ab")]
    public void LeavesACommentAmongTheParagraphsOutOfTheText(string paragraphs, string expected)
    {
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", "<table:table-row>"
            + $"<table:table-cell office:value-type=\"string\">{paragraphs}</table:table-cell>"
            + "</table:table-row>"));

        Assert.Equal(RawCell.FromText(expected), Assert.Single(ReadAll(cursor))[0]);
    }

    /// <summary>
    /// ODF's white-space rule for paragraph text: a space, tab, carriage return or line feed written
    /// as a character is one space, a run of them collapses to one, and none counts at the start of a
    /// paragraph or right after a tab or line break. Only text:s, text:tab and text:line-break state
    /// white space that is the value's. LibreOffice and Excel show the cell that way; read literally,
    /// a producer's doubled space or indented markup became part of the value.
    /// </summary>
    [Theory]
    [InlineData("<text:p>Max  Mustermann</text:p>", "Max Mustermann")]
    [InlineData("<text:p>Hauptstr.\n      12</text:p>", "Hauptstr. 12")]
    [InlineData("<text:p>a\tb\r\nc</text:p>", "a b c")]
    [InlineData("<text:p>a<text:span> </text:span> b</text:p>", "a b")]
    [InlineData("<text:p>a <text:s text:c=\"2\"/>b</text:p>", "a   b")]
    [InlineData("<text:p>a<text:s/> b</text:p>", "a  b")]
    [InlineData("<text:p>a<text:tab/> b</text:p>", "a\tb")]
    [InlineData("<text:p>a<text:line-break/>  b</text:p>", "a\nb")]
    [InlineData("<text:p>a</text:p>\n  <text:p>\n    b\n  </text:p>", "a\nb")]
    [InlineData("<text:p>a\u00A0\u00A0b</text:p>", "a\u00A0\u00A0b")]
    public void ReadsTheWhiteSpaceOfParagraphTextAsOdfDoes(string paragraphs, string expected)
    {
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", "<table:table-row>"
            + $"<table:table-cell office:value-type=\"string\">{paragraphs}</table:table-cell>"
            + "</table:table-row>"));

        Assert.Equal(expected, Assert.Single(ReadAll(cursor))[0].AsText());
    }

    /// <summary>
    /// A time cell's duration: zero is midnight on the day a bare time reads on; a duration the
    /// format cannot hold, or no duration at all, leaves the cell its text rather than a wrong time.
    /// </summary>
    [Theory]
    [InlineData("PT00H00M00S", "00:00", true)]
    [InlineData("garbage", "shown", false)]
    [InlineData("-PT1H", "shown", false)]
    [InlineData("P10675199D", "shown", false)]
    public void ReadsATimeCellAsADurationOrElseAsItsText(string duration, string shown, bool isTime)
    {
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", "<table:table-row>"
            + Cell($"office:value-type=\"time\" office:time-value=\"{duration}\"", shown)
            + "</table:table-row>"));

        RawCell cell = Assert.Single(ReadAll(cursor))[0];

        if (isTime)
        {
            Assert.Equal(RawCell.FromDate(new DateTime(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified)), cell);
        }
        else
        {
            Assert.Equal(RawCell.FromText(shown), cell);
        }
    }

    /// <summary>
    /// The whole content part indented, as a pretty-printing producer writes it: the indentation
    /// between elements is markup, and the sheets, rows and cells read as the compact file's.
    /// </summary>
    [Fact]
    public void ReadsAnIndentedContentPartAsTheCompactOne()
    {
        const string Compact =
            "<table:table table:name=\"A\"><table:table-row>"
            + "<table:table-cell office:value-type=\"string\"><text:p>a</text:p><text:p/><text:p>b</text:p></table:table-cell>"
            + "<table:table-cell office:value-type=\"float\" office:value=\"1.5\"><text:p>1,5</text:p></table:table-cell>"
            + "<table:covered-table-cell/>"
            + "<table:table-cell office:value-type=\"string\"><text:p>c</text:p></table:table-cell>"
            + "</table:table-row></table:table>"
            + "<table:table table:name=\"B\"><table:table-row><table:table-cell office:value-type=\"string\"><text:p>d</text:p></table:table-cell></table:table-row></table:table>";
        string indented = Compact.Replace("><", ">\n    <", StringComparison.Ordinal);

        using OdsCursor compact = Open(new OdsPackage().WithRawContent(Content(Compact)));
        using OdsCursor pretty = Open(new OdsPackage().WithRawContent(Content(indented)));

        Assert.Equal(["A", "B"], pretty.Sheets.Select(s => s.Name));
        Assert.Equal(["a\n\nb", "1.5", null, "c"], ReadAll(pretty)[0].Select(c => c.Kind == RawCellKind.Number ? c.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : c.AsText()));

        foreach (int sheet in (int[])[0, 1])
        {
            Assert.True(compact.MoveToSheet(sheet, TestContext.Current.CancellationToken));
            Assert.True(pretty.MoveToSheet(sheet, TestContext.Current.CancellationToken));
            Assert.Equal(ReadAll(compact).Select(r => r.ToArray()), ReadAll(pretty).Select(r => r.ToArray()));
        }
    }

    private static string Content(string body) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><office:document-content {OdsPackage.Namespaces}office:version="1.3"><office:body><office:spreadsheet>{body}</office:spreadsheet></office:body></office:document-content>""";

    [Fact]
    public void ReadsAParagraphInsideAParagraphAsALineBreakAndKeepsTheOuterTail()
    {
        // Malformed ODF, but text either way: the inner paragraph breaks the line, and the outer one's
        // text after it is still the cell's.
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", "<table:table-row>"
            + Cell("office:value-type=\"string\"", "ab<text:p>in</text:p>ef")
            + Cell("office:value-type=\"string\"", "ab<text:p/>ef<text:h>in<text:p>ner</text:p>most</text:h>")
            + "</table:table-row>"));
        RawCell[] cells = Assert.Single(ReadAll(cursor));

        Assert.Equal("ab\ninef", cells[0].AsText());
        Assert.Equal("ab\nef\nin\nnermost", cells[1].AsText());
    }

    [Fact]
    public void ReadsANumberThatIsNoFiniteNumberAsTheCellsTextAsItDoesAnUnparseableOne()
    {
        // double.TryParse accepts NaN and Infinity, and reads 1e400 as Infinity; none is a number a
        // spreadsheet holds. They are passed over as an unparseable value is, so the cell reads as
        // its text, as the xlsx reader reads them.
        string row = "<table:table-row>"
            + Cell("office:value-type=\"float\" office:value=\"NaN\"", "NaN shown")
            + Cell("office:value-type=\"float\" office:value=\"Infinity\"", "inf")
            + Cell("office:value-type=\"percentage\" office:value=\"-Infinity\"", "-inf")
            + Cell("office:value-type=\"currency\" office:value=\"1e400\"", "huge")
            + Cell("office:value-type=\"float\" office:value=\"abc\"", "abc shown")
            + Cell("office:value-type=\"float\" office:value=\"1e300\"", "big")
            + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", row));

        Assert.Equal(
            [RawCell.FromText("NaN shown"), RawCell.FromText("inf"), RawCell.FromText("-inf"), RawCell.FromText("huge"), RawCell.FromText("abc shown"), RawCell.FromNumber(1e300)],
            Assert.Single(ReadAll(cursor)));
    }

    [Fact]
    public void ReadsAStringValueAttributeOverTheParagraphs()
    {
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S",
            "<table:table-row>"
            + Cell("office:value-type=\"string\" office:string-value=\"exact\"", "shown")
            + Cell("office:value-type=\"string\" office:string-value=\"Q&amp;A &quot;1&quot; &#228;\"", "shown")
            + "</table:table-row>"));
        RawCell[] cells = Assert.Single(ReadAll(cursor));

        Assert.Equal("exact", cells[0].AsText());
        Assert.Equal("Q&A \"1\" ä", cells[1].AsText());
    }

    [Fact]
    public void ReadsAnErrorCellAsTheErrorItShows()
    {
        // LibreOffice's form for a formula that failed: an empty string value, the error in its
        // extension attribute and the error's text in the paragraph. The empty string value must not
        // win over the error, and a prefix other than calcext's must not be taken for it.
        const string Error = "office:value-type=\"string\" office:string-value=\"\" calcext:value-type=\"error\"";
        string row = "<table:table-row>"
            + Cell(Error, "#N/A")
            + Cell(Error, "Err:502")
            + Cell(Error)
            + Cell("office:value-type=\"string\" office:string-value=\"\" table:value-type=\"error\"", "#N/A")
            + Cell("office:value-type=\"float\" office:value=\"2\" calcext:value-type=\"float\"", "2")
            + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", row));
        RawCell[] cells = Assert.Single(ReadAll(cursor));

        Assert.Equal(RawCell.FromError("#N/A"), cells[0]);
        Assert.Equal(RawCell.FromError("Err:502"), cells[1]);
        Assert.Equal(RawCell.Empty, cells[2]);
        Assert.Equal(RawCell.Empty, cells[3]);
        Assert.Equal(RawCell.FromNumber(2), cells[4]);
    }

    [Fact]
    public void ReadsABooleanAsXmlSpellsOneAndFallsBackToTheTextOtherwise()
    {
        // xsd:boolean is true, false, 1 or 0. A converter has been seen writing "2"; that is no
        // boolean, and reading it as false would state the opposite of what the cell may show.
        string row = "<table:table-row>"
            + Cell("office:value-type=\"boolean\" office:boolean-value=\"1\"", "WAHR")
            + Cell("office:value-type=\"boolean\" office:boolean-value=\"0\"", "FALSCH")
            + Cell("office:value-type=\"boolean\" office:boolean-value=\"false\"", "FALSCH")
            + Cell("office:value-type=\"boolean\" office:boolean-value=\"2\"", "2")
            + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", row));
        RawCell[] cells = Assert.Single(ReadAll(cursor));

        Assert.Equal(RawCell.FromBoolean(true), cells[0]);
        Assert.Equal(RawCell.FromBoolean(false), cells[1]);
        Assert.Equal(RawCell.FromBoolean(false), cells[2]);
        Assert.Equal(RawCell.FromText("2"), cells[3]);
    }

    [Fact]
    public void ReadsATimeWithMoreFractionalDigitsThanATimeSpanHolds()
    {
        // LibreOffice writes nine digits after the second; a TimeSpan holds seven, and a parser that
        // refuses the rest reads a stated time as nothing.
        string row = "<table:table-row>"
            + Cell("office:value-type=\"time\" office:time-value=\"PT10H59M59.999999999S\"")
            + Cell("office:value-type=\"time\" office:time-value=\"PT01H02M03.4567S\"")
            + Cell("office:value-type=\"time\" office:time-value=\"PT1234H05M06S\"")
            + Cell("office:value-type=\"time\" office:time-value=\"PT10H59M59.99949999999S\"")
            + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", row));
        RawCell[] cells = Assert.Single(ReadAll(cursor));
        DateTime epoch = new(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(RawCell.FromDate(epoch.AddHours(11)), cells[0]);
        Assert.Equal(RawCell.FromDate(epoch.Add(new TimeSpan(0, 1, 2, 3, 457))), cells[1]);
        Assert.Equal(RawCell.FromDate(epoch.AddHours(1234).AddMinutes(5).AddSeconds(6)), cells[2]);
        Assert.Equal(RawCell.FromDate(epoch.Add(new TimeSpan(0, 10, 59, 59, 999))), cells[3]);
    }

    [Theory]
    [InlineData("1904-01-01", "1904-01-01T12:30:00", "1904-03-01T00:00:00")]
    [InlineData("1980-06-15", "1980-06-15T12:30:00", "1980-08-14T00:00:00")]
    [InlineData("1899-12-30", "1899-12-31T12:30:00", "1900-02-28T00:00:00")]
    public void CountsATimeFromTheNullDateTheSpreadsheetStates(string nullDate, string halfDay, string sixtyDays)
    {
        // A spreadsheet may count its days from another null date than 30 December 1899 — LibreOffice
        // offers 1 January 1904 — and says so in its calculation settings, ahead of the tables. A
        // duration counts from that day, on every sheet, also after moving back to one. The default,
        // stated or not, reads as it always has: a time of day on 31 December 1899, as in an xlsx.
        static string Table(string name) => $"""<table:table table:name="{name}"><table:table-row>"""
            + Cell("office:value-type=\"time\" office:time-value=\"PT12H30M00S\"")
            + Cell("office:value-type=\"time\" office:time-value=\"PT1440H\"")
            + "</table:table-row></table:table>";
        string content = $"""<?xml version="1.0" encoding="UTF-8"?><office:document-content {OdsPackage.Namespaces}office:version="1.3"><office:body><office:spreadsheet>"""
            + $"""<table:calculation-settings table:automatic-find-labels="false"><table:null-date table:date-value="{nullDate}"/></table:calculation-settings>"""
            + Table("A") + Table("B")
            + "</office:spreadsheet></office:body></office:document-content>";
        RawCell[] expected =
        [
            RawCell.FromDate(DateTime.Parse(halfDay, System.Globalization.CultureInfo.InvariantCulture)),
            RawCell.FromDate(DateTime.Parse(sixtyDays, System.Globalization.CultureInfo.InvariantCulture)),
        ];

        using OdsCursor cursor = Open(new OdsPackage().WithRawContent(content));

        Assert.Equal(expected, Assert.Single(ReadAll(cursor)));
        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal(expected, Assert.Single(ReadAll(cursor)));
        Assert.True(cursor.MoveToSheet(0, TestContext.Current.CancellationToken));
        Assert.Equal(expected, Assert.Single(ReadAll(cursor)));
    }

    /// <summary>
    /// The null date is the spreadsheet's only where its calculation settings state it, ahead of the
    /// sheets, and only as a date written year first: one met after a sheet or inside a DDE link,
    /// or a value that is no ISO date, leaves the default, so every time reads on 31 December 1899.
    /// </summary>
    [Theory]
    [InlineData("after the first sheet", "1904-01-01")]
    [InlineData("in a DDE link", "1904-01-01")]
    [InlineData("ahead", "13:45")]
    [InlineData("ahead", "01/02/1904")]
    public void TakesTheNullDateOnlyFromTheSettingsAheadOfTheSheetsAndOnlyAsAnIsoDate(string where, string nullDate)
    {
        static string Table(string name) => $"""<table:table table:name="{name}"><table:table-row>"""
            + Cell("office:value-type=\"time\" office:time-value=\"PT12H30M00S\"")
            + "</table:table-row></table:table>";
        string settings = $"""<table:calculation-settings><table:null-date table:date-value="{nullDate}"/></table:calculation-settings>""";
        string body = where switch
        {
            "after the first sheet" => Table("A") + settings + Table("B"),
            "in a DDE link" => $"<table:dde-links><table:dde-link>{settings}</table:dde-link></table:dde-links>" + Table("A") + Table("B"),
            _ => settings + Table("A") + Table("B"),
        };
        RawCell expected = RawCell.FromDate(new DateTime(1899, 12, 31, 12, 30, 0, DateTimeKind.Unspecified));

        using OdsCursor cursor = Open(new OdsPackage().WithRawContent(Content(body)));

        Assert.Equal(["A", "B"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal(expected, Assert.Single(ReadAll(cursor))[0]);
        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal(expected, Assert.Single(ReadAll(cursor))[0]);
        Assert.True(cursor.MoveToSheet(0, TestContext.Current.CancellationToken));
        Assert.Equal(expected, Assert.Single(ReadAll(cursor))[0]);
    }

    /// <summary>
    /// How far the reading is through the content part, which holds every sheet: little of it once
    /// opened, about half after the first of two equal sheets, all of it after the second.
    /// </summary>
    [Fact]
    public void ReportsHowFarThroughTheContentPartTheReadingIs()
    {
        StringBuilder rows = new();

        for (int i = 0; i < 4_000; i++)
        {
            rows.Append("<table:table-row>").Append(Cell("office:value-type=\"string\"", $"row {i:D6} of a sheet long enough to measure")).Append("</table:table-row>");
        }

        using OdsCursor cursor = Open(new OdsPackage().WithTable("A", rows.ToString()).WithTable("B", rows.ToString()));

        Assert.InRange(cursor.ReadFraction.GetValueOrDefault(-1), 0.001, 0.2);
        Assert.Equal(4_000, ReadAll(cursor).Count);
        Assert.InRange(cursor.ReadFraction.GetValueOrDefault(-1), 0.4, 0.6);
        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal(4_000, ReadAll(cursor).Count);
        Assert.InRange(cursor.ReadFraction.GetValueOrDefault(-1), 0.95, 1);
    }

    [Fact]
    public void ReadsTheCoveredPartOfAMergeAsEmpty()
    {
        // A covered cell may still hold the value it had before the merge, as LibreOffice keeps it;
        // the sheet shows the merged cell over it, so it reads as empty, repeats included, and the
        // cell after it keeps its column.
        string row = "<table:table-row>"
            + "<table:table-cell table:number-columns-spanned=\"5\" office:value-type=\"string\"><text:p>merged</text:p></table:table-cell>"
            + "<table:covered-table-cell/>"
            + "<table:covered-table-cell office:value-type=\"float\" office:value=\"5\"><text:p>5</text:p></table:covered-table-cell>"
            + "<table:covered-table-cell table:number-columns-repeated=\"2\" office:value-type=\"string\"><text:p>hidden</text:p></table:covered-table-cell>"
            + Cell("office:value-type=\"string\"", "next")
            + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", row));

        Assert.Equal(
            [RawCell.FromText("merged"), RawCell.Empty, RawCell.Empty, RawCell.Empty, RawCell.Empty, RawCell.FromText("next")],
            Assert.Single(ReadAll(cursor)));
    }

    /// <summary>
    /// A cell whose value stands in its attributes is passed over to its own end tag, and no further
    /// nor less: past the empty elements in its paragraph and past a table nested in it, whose cells
    /// and row end are not the row's. The cell after it, and the next row, read as written.
    /// </summary>
    [Fact]
    public void PassesOverExactlyTheCellWhoseValueStandsInItsAttributes()
    {
        const string SubTable = "<table:table><table:table-row><table:table-cell office:value-type=\"string\"><text:p>inner</text:p></table:table-cell></table:table-row></table:table>";
        string rows = "<table:table-row>"
            + "<table:table-cell office:value-type=\"float\" office:value=\"1\"><text:p>1<text:s/><text:s/>0</text:p></table:table-cell>"
            + $"<table:table-cell office:value-type=\"float\" office:value=\"2\">{SubTable}<text:p>2</text:p></table:table-cell>"
            + $"<table:table-cell office:value-type=\"string\" office:string-value=\"stated\">{SubTable}<text:p>shown</text:p></table:table-cell>"
            + Cell("office:value-type=\"string\"", "next")
            + "</table:table-row>"
            + "<table:table-row>" + Cell("office:value-type=\"string\"", "row2") + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows));

        Assert.Equal(
            [
                [RawCell.FromNumber(1), RawCell.FromNumber(2), RawCell.FromText("stated"), RawCell.FromText("next")],
                [RawCell.FromText("row2")],
            ],
            ReadAll(cursor));
    }

    [Fact]
    public void ReadsRowsInsideHeaderRowsAndRowGroups()
    {
        string rows = "<table:table-header-rows><table:table-row>" + Cell("office:value-type=\"string\"", "id") + "</table:table-row></table:table-header-rows>"
            + "<table:table-row-group><table:table-rows><table:table-row>" + Cell("office:value-type=\"string\"", "1") + "</table:table-row></table:table-rows></table:table-row-group>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows));

        Assert.Equal(["id", "1"], ReadAll(cursor).Select(r => r[0].AsText()));
    }

    [Fact]
    public void RefusesARepeatedValueWiderThanTheColumnCeiling()
    {
        string row = "<table:table-row><table:table-cell office:value-type=\"float\" office:value=\"1\" table:number-columns-repeated=\"20000\"/></table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", row));

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(nameof(OdsCursorOptions.MaxColumns), error.Limit);
    }

    [Fact]
    public void RefusesRepeatedValuesLongerThanTheRowCeiling()
    {
        string rows = "<table:table-row table:number-rows-repeated=\"50\"><table:table-cell office:value-type=\"float\" office:value=\"1\"/></table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows), new OdsCursorOptions { MaxRows = 10 });

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => ReadAll(cursor));
        Assert.Equal(nameof(OdsCursorOptions.MaxRows), error.Limit);
    }

    /// <summary>
    /// The row ceiling is the last row a value may stand on, however the rows before it are written:
    /// a value reached through empty rows, or the last copy of a repeated value, is read on it.
    /// </summary>
    [Fact]
    public void ReadsAValueOnTheLastRowTheCeilingAllows()
    {
        static string Value(string text, int repeat = 1) => $"<table:table-row table:number-rows-repeated=\"{repeat}\">" + Cell("office:value-type=\"string\"", text) + "</table:table-row>";
        OdsCursorOptions options = new() { MaxRows = 10 };

        using OdsCursor afterEmpty = Open(new OdsPackage().WithTable("S", "<table:table-row table:number-rows-repeated=\"9\"><table:table-cell/></table:table-row>" + Value("last") + Value("past")), options);

        Assert.True(afterEmpty.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(10, afterEmpty.CurrentRowNumber);
        Assert.Equal("last", afterEmpty.CurrentRow[0].AsText());
        Assert.Equal(nameof(OdsCursorOptions.MaxRows), Assert.Throws<TabularLimitException>(() => afterEmpty.ReadRow(TestContext.Current.CancellationToken)).Limit);

        using OdsCursor repeated = Open(new OdsPackage().WithTable("S", Value("copy", 10)), options);

        Assert.Equal(10, ReadAll(repeated).Count);
        Assert.Equal(10, repeated.CurrentRowNumber);
    }

    /// <summary>
    /// A value past the row ceiling is refused at the first read that meets it: a repeated value as a
    /// whole, before a single copy is handed out, and one behind empty rows that reach the ceiling.
    /// </summary>
    [Theory]
    [InlineData("<table:table-row table:number-rows-repeated=\"11\"><table:table-cell office:value-type=\"float\" office:value=\"1\"/></table:table-row>")]
    [InlineData("<table:table-row table:number-rows-repeated=\"10\"><table:table-cell/></table:table-row><table:table-row><table:table-cell office:value-type=\"float\" office:value=\"1\"/></table:table-row>")]
    public void RefusesAValuePastTheRowCeilingAtTheFirstReadThatMeetsIt(string rows)
    {
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows), new OdsCursorOptions { MaxRows = 10 });

        Assert.Equal(nameof(OdsCursorOptions.MaxRows), Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken)).Limit);
    }

    /// <summary>
    /// The column ceiling is the last column a value may stand in: one reached through empty cells is
    /// read there, and one behind empty cells that reach the ceiling is refused, however they add up.
    /// </summary>
    [Fact]
    public void ReadsAValueInTheLastColumnTheCeilingAllowsAndRefusesOnePast()
    {
        const string Value = "<table:table-cell office:value-type=\"string\"><text:p>v</text:p></table:table-cell>";
        OdsCursorOptions options = new() { MaxColumns = 5 };

        using OdsCursor last = Open(new OdsPackage().WithTable("S", "<table:table-row><table:table-cell table:number-columns-repeated=\"4\"/>" + Value + "</table:table-row>"), options);
        RawCell[] row = Assert.Single(ReadAll(last));
        Assert.Equal(5, row.Length);
        Assert.Equal("v", row[4].AsText());

        foreach (int second in (int[])[3, 4])
        {
            using OdsCursor past = Open(new OdsPackage().WithTable("S", $"<table:table-row><table:table-cell table:number-columns-repeated=\"2\"/><table:table-cell table:number-columns-repeated=\"{second}\"/>" + Value + "</table:table-row>"), options);
            Assert.Equal(nameof(OdsCursorOptions.MaxColumns), Assert.Throws<TabularLimitException>(() => past.ReadRow(TestContext.Current.CancellationToken)).Limit);
        }
    }

    [Fact]
    public void IsDetectedFromItsBytesAndOpenedByDetection()
    {
        byte[] content = new OdsPackage().WithTable("S", "<table:table-row>" + Cell("office:value-type=\"string\"", "x") + "</table:table-row>").Build();

        using MemoryStream stream = new(content, writable: false);

        Assert.Equal(TabularFormat.Ods, TabularFile.Detect(stream));

        using ITabularCursor cursor = TabularFile.Open(stream, "upload", cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<OdsCursor>(cursor);
        FileProfile profile = TabularAnalyzer.Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TabularFormat.Ods, profile.Format);
    }

    [Fact]
    public void StillRefusesAnOpenDocumentTextFile()
    {
        byte[] content = new OdsPackage().WithMimetype("application/vnd.oasis.opendocument.text").Build();

        using MemoryStream stream = new(content, writable: false);
        TabularFormatException error = Assert.Throws<TabularFormatException>(() => TabularFile.Open(stream, "upload", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TabularFormatException.Unsupported, error.Code);
    }
}
