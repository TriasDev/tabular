using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// Shapes found by reading other readers' test corpora through this one — legal workbooks, written
/// by real producers, that the golden fixtures did not happen to contain.
/// </summary>
/// <remarks>
/// Each fixture is rebuilt here from raw OOXML in the smallest form that shows the case; the files
/// that revealed them are not copied.
/// </remarks>
public sealed class ProducerQuirksTests
{
    /// <summary>
    /// Reads every row, under a deadline, so a reader that loops forever fails the test instead of
    /// hanging the run.
    /// </summary>
    private static List<string?[]> ReadAll(byte[] content)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream, cancellationToken: deadline.Token);

        List<string?[]> rows = [];

        while (cursor.ReadRow(deadline.Token))
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
    public void ReadsAnEmptySharedStringInsteadOfLoopingOnIt()
    {
        // An empty string in the shared table is written <si><t/></si>. The item reader took the
        // empty <t/> as consumed without advancing past it, found the same element again, and did so
        // forever: without a cancellation token the read never returned.
        byte[] content = new XlsxPackage()
            .WithSharedStrings("""<si><t>a</t></si><si><t/></si><si><t>b</t></si>""")
            .WithSheet(
                "Sheet1",
                """<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>""")
            .Build();

        string?[] row = Assert.Single(ReadAll(content));

        Assert.Equal(3, row.Length);
        Assert.Equal("a", row[0]);
        Assert.True(string.IsNullOrEmpty(row[1]));
        Assert.Equal("b", row[2]);
    }

    [Fact]
    public void ReadsASheetWhoseMarkupCarriesElementsWithManyAttributes()
    {
        // Sparkline groups and other extension elements carry twenty attributes and more. The
        // scanner kept every attribute of every element up to sixteen and refused the sheet past
        // that, so a workbook with sparklines could not be read at all.
        byte[] content = new XlsxPackage()
            .WithSheet(
                "Sheet1",
                """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row><ext x1="1" x2="1" x3="1" x4="1" x5="1" x6="1" x7="1" x8="1" x9="1" x10="1" x11="1" x12="1" x13="1" x14="1" x15="1" x16="1" x17="1" x18="1" x19="1" x20="1"/>""")
            .Build();

        Assert.Equal(new string?[] { "a" }, Assert.Single(ReadAll(content)));
    }

    [Fact]
    public void ReadsACellsTypeBehindAttributesItHasNoUseFor()
    {
        // The reason the ceiling was a refusal rather than a truncation: dropping the seventeenth
        // attribute would read t="s" behind sixteen others as a number. Keeping only the attributes
        // the cursor reads — r, t and s — keeps that from happening at any count.
        byte[] content = new XlsxPackage()
            .WithSharedStrings("""<si><t>shared</t></si>""")
            .WithSheet("Sheet1", """<row r="1"><c x1="1" x2="1" x3="1" x4="1" x5="1" x6="1" x7="1" x8="1" x9="1" x10="1" x11="1" x12="1" x13="1" x14="1" x15="1" x16="1" x17="1" x18="1" x19="1" x20="1" r="A1" t="s"><v>0</v></c></row>""")
            .Build();

        Assert.Equal(new string?[] { "shared" }, Assert.Single(ReadAll(content)));
    }

    private static readonly string OneCell =
        """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""";

    [Fact]
    public void ReadsAPackageWhoseEntryNamesUseBackslashes()
    {
        // Some Windows tools write zip entry names as xl\workbook.xml. The format does not define
        // them, readers are expected to accept them, and matching names literally found no workbook.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", OneCell)
            .WithPartNaming(path => path.Replace('/', '\\'))
            .Build();

        Assert.Equal(new string?[] { "a" }, Assert.Single(ReadAll(content)));
    }

    [Fact]
    public void FindsTheWorkbookThroughThePackageRelationshipsWhereverItIs()
    {
        // The package's own relationships say where the workbook is; xl/ is only where Excel puts
        // it. A producer that writes it at the root was refused with "xl/workbook.xml is missing".
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", OneCell)
            .WithSharedStrings("""<si><t>unused</t></si>""")
            .WithWorkbookFolder(string.Empty)
            .Build();

        Assert.Equal(new string?[] { "a" }, Assert.Single(ReadAll(content)));
    }

    [Fact]
    public void LeavesOutASheetThatHoldsNoCells()
    {
        // A macro or dialog sheet is declared beside the worksheets and has no worksheet part. It was
        // offered as a sheet, and moving to it failed the cursor for good.
        byte[] content = new XlsxPackage()
            .WithSheet("Data", OneCell)
            .WithSheetDeclarations("""<sheet name="Module" sheetId="9" r:id=""/>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.Equal("Data", Assert.Single(cursor.Sheets).Name);
    }

    [Fact]
    public void FollowsSheetRelationshipsDeclaredInTheStrictNamespace()
    {
        // Strict OOXML declares r:id in another namespace. Missing it sent every sheet to the
        // conventional path, which a strict producer need not use — here the parts are named
        // otherwise, so only the relationships lead to them.
        byte[] content = new XlsxPackage()
            .WithSheet("First", """<row r="1"><c r="A1" t="inlineStr"><is><t>first</t></is></c></row>""")
            .WithSheet("Second", """<row r="1"><c r="A1" t="inlineStr"><is><t>second</t></is></c></row>""")
            .WithStrictRelationshipNamespace()
            .WithSheetPartNames(number => $"worksheets/data{number}.xml")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.Equal(["First", "Second"], cursor.Sheets.Select(s => s.Name));
        Assert.True(cursor.MoveToSheet(1));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("second", cursor.CurrentRow[0].AsText());
    }

    /// <summary>A stylesheet whose second cell format is the given number format.</summary>
    private static string StylesWithFormat(int numFmtId) =>
        $"""<?xml version="1.0"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="{numFmtId}" applyNumberFormat="1"/></cellXfs></styleSheet>""";

    private static RawCellSummary ReadFirstCell(byte[] content)
    {
        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));

        RawCell cell = cursor.CurrentRow[0];
        return new RawCellSummary(cell.Kind, cell.Kind == RawCellKind.Date ? cell.Date : null, cell.AsText());
    }

    private readonly record struct RawCellSummary(RawCellKind Kind, DateTime? Date, string? Text);

    [Fact]
    public void KeepsThe1904EpochWhenAnExtensionElementSharesItsName()
    {
        // Excel 2013 and later add <x15:workbookPr chartTrackingRefBase="1"/> in an extension list.
        // Its local name is the same, and reading it as the workbook's own properties reset the epoch
        // to 1900: every date in a 1904 workbook came out four years and a day early.
        byte[] content = new XlsxPackage()
            .WithDate1904()
            .WithWorkbookTail("""<extLst><ext uri="{B58B0392-4F1F-4190-BB64-5DF3571DCE5F}" xmlns:x15="http://schemas.microsoft.com/office/spreadsheetml/2010/11/main"><x15:workbookPr chartTrackingRefBase="1"/></ext></extLst>""")
            .WithStyles(StylesWithFormat(14))
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="1"><v>43708</v></c></row>""")
            .Build();

        Assert.Equal(new DateTime(2023, 9, 1, 0, 0, 0, DateTimeKind.Unspecified), ReadFirstCell(content).Date);
    }

    [Fact]
    public void RoundsASerialDateToTheMillisecondExcelKeeps()
    {
        // 16:00 exactly is stored as 41655.666666666664, a hair below the true fraction. Truncating
        // the ticks read it as 15:59:59.999, which every other reader and Excel itself show as 16:00.
        byte[] content = new XlsxPackage()
            .WithStyles(StylesWithFormat(22))
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="1"><v>41655.666666666664</v></c></row>""")
            .Build();

        Assert.Equal(new DateTime(2014, 1, 16, 16, 0, 0, DateTimeKind.Unspecified), ReadFirstCell(content).Date);
    }

    [Theory]
    [InlineData(27)]
    [InlineData(31)]
    [InlineData(36)]
    [InlineData(50)]
    [InlineData(55)]
    [InlineData(58)]
    public void ReadsTheEastAsianBuiltInDateFormatsAsDates(int numFmtId)
    {
        // The specification reserves 27–36 and 50–58 for dates in East Asian locales; a workbook
        // saved by a Japanese, Chinese or Korean Excel uses them without declaring a format code.
        byte[] content = new XlsxPackage()
            .WithStyles(StylesWithFormat(numFmtId))
            .WithSheet("Sheet1", """<row r="1"><c r="A1" s="1"><v>44211</v></c></row>""")
            .Build();

        Assert.Equal(new DateTime(2021, 1, 15, 0, 0, 0, DateTimeKind.Unspecified), ReadFirstCell(content).Date);
    }

    [Fact]
    public void LeavesThePhoneticGuideOutOfASharedString()
    {
        // <rPh> carries the reading of the text above it — furigana — as a rendering aid, not as
        // part of the value. Concatenating it turned 漢字 into 漢字かんじ.
        byte[] content = new XlsxPackage()
            .WithSharedStrings("""<si><t>漢字</t><rPh sb="0" eb="2"><t>かんじ</t></rPh><phoneticPr fontId="1"/></si>""")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""")
            .Build();

        Assert.Equal("漢字", ReadFirstCell(content).Text);
    }

    [Fact]
    public void LeavesThePhoneticGuideOutOfAnInlineString()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="inlineStr"><is><t>漢字</t><rPh sb="0" eb="2"><t>かんじ</t></rPh></is></c></row>""")
            .Build();

        Assert.Equal("漢字", ReadFirstCell(content).Text);
    }

    [Fact]
    public void RefusesAWorkbookPartThatIsNotWellFormedXmlAsAnUnreadableFile()
    {
        // XmlException escaped the constructor: a type the guide never names, so a host catching the
        // documented ones crashed on a crafted upload.
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", OneCell)
            .WithWorkbookTail("<unclosed>")
            .Build();

        using MemoryStream stream = new(content, writable: false);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => new XlsxCursor(stream));
        Assert.IsType<System.Xml.XmlException>(error.InnerException);
    }

    [Fact]
    public void RefusesAStylesheetThatIsNotWellFormedXmlAsAnUnreadableFile()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("Sheet1", OneCell)
            .WithStyles("<styleSheet><cellXfs>")
            .Build();

        using MemoryStream stream = new(content, writable: false);

        Assert.Throws<InvalidDataException>(() => new XlsxCursor(stream));
    }

    [Fact]
    public void RefusesASharedStringTableThatIsNotWellFormedXmlWhenARowFirstNeedsIt()
    {
        byte[] content = new XlsxPackage()
            .WithSharedStrings("""<si><t>a</t></si><si><t>b""")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>0</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.Throws<InvalidDataException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    private const string SheetOpen =
        """<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""";

    private static readonly string TwoRows =
        """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>b</t></is></c></row>""";

    [Theory]
    [InlineData("""<row r="3"><c r="A3" t="inlineStr"><is><t>c""")]      // inside a value
    [InlineData("""<row r="3"><c r="A3" t="inlineStr"><is><t>c</t></is></c>""")] // inside a row
    [InlineData("")]                                                          // between rows
    public void RefusesAWorksheetCutOffBeforeItsMarkupEnds(string tail)
    {
        // A clipped upload ended the scanner cleanly, and the cursor handed out a shorter table whose
        // last row was whatever part of it had arrived — no exception, nothing in the diagnostics.
        byte[] content = new XlsxPackage().WithRawSheet("Sheet1", SheetOpen + TwoRows + tail).Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadsAnEmptyWorksheetWrittenAsASelfClosingElement()
    {
        byte[] content = new XlsxPackage()
            .WithRawSheet("Sheet1", """<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData/></worksheet>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StopsReadingAtTheEndOfTheSheetDataWhateverFollowsIt()
    {
        byte[] content = new XlsxPackage()
            .WithRawSheet("Sheet1", SheetOpen + TwoRows + "</sheetData><extLst><ext><row r=\"9\"/></ext></extLst></worksheet>")
            .Build();

        Assert.Equal(2, ReadAll(content).Count);
    }

    [Fact]
    public void ReadsAnotherSheetAfterOneFailed()
    {
        // MoveToSheet reopened the sheet from its start and returned true, and then ReadRow refused
        // with "cannot continue" — the fault outlived the position it was about.
        byte[] content = new XlsxPackage()
            .WithRawSheet("Broken", SheetOpen + """<row r="1"><c r="A1" t="inlineStr"><is><t>x""")
            .WithSheet("Good", OneCell)
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.Throws<InvalidDataException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.True(cursor.MoveToSheet(1));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("a", cursor.CurrentRow[0].AsText());
    }
}
