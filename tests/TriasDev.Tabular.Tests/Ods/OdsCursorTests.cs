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

        Assert.True(cursor.MoveToSheet(1));
        Assert.Equal("r", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.True(cursor.MoveToSheet(0));
        Assert.Equal("o", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.False(cursor.MoveToSheet(2));
    }

    [Fact]
    public void NamesTheSheetsAsTheirMarkupMeansAndCountsOnlyRealTables()
    {
        // The names are listed by a pass of their own over the part; it must see exactly the tables
        // the reading does — not one mentioned in a comment or CDATA, not a tag cut short by a '>'
        // inside an attribute — and read names with entities and non-ASCII characters as written.
        string Row(string text) => "<table:table-row>" + Cell("office:value-type=\"string\"", text) + "</table:table-row>";
        OdsPackage package = new OdsPackage()
            .WithTable("R&amp;D &lt;2024&gt; &#x263A;", "<!-- <table:table table:name=\"comment\"> -->" + Row("first"))
            .WithTable("Übersicht\" table:style-name=\"a>b", "<![CDATA[<table:table table:name=\"cdata\">]]>" + Row("second"))
            .WithRawTable("<table:table>" + Row("third") + "</table:table>");

        using OdsCursor cursor = Open(package);

        Assert.Equal(["R&D <2024> ☺", "Übersicht", ""], cursor.Sheets.Select(s => s.Name));
        Assert.True(cursor.MoveToSheet(2));
        Assert.Equal("third", Assert.Single(ReadAll(cursor))[0].AsText());
        Assert.True(cursor.MoveToSheet(1));
        Assert.Equal("second", Assert.Single(ReadAll(cursor))[0].AsText());
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

    [Fact]
    public void ReadsTheCoveredPartOfAMergeAsEmpty()
    {
        string row = "<table:table-row>"
            + "<table:table-cell table:number-columns-spanned=\"2\" office:value-type=\"string\"><text:p>merged</text:p></table:table-cell>"
            + "<table:covered-table-cell/>"
            + Cell("office:value-type=\"string\"", "next")
            + "</table:table-row>";

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", row));

        Assert.Equal([RawCell.FromText("merged"), RawCell.Empty, RawCell.FromText("next")], Assert.Single(ReadAll(cursor)));
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

    [Fact]
    public void IsDetectedFromItsBytesAndOpenedByDetection()
    {
        byte[] content = new OdsPackage().WithTable("S", "<table:table-row>" + Cell("office:value-type=\"string\"", "x") + "</table:table-row>").Build();

        using MemoryStream stream = new(content, writable: false);

        Assert.Equal(TabularFormat.Ods, TabularFile.Detect(stream));

        using ITabularCursor cursor = TabularFile.Open(stream, "upload", cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<OdsCursor>(cursor);
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);
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
