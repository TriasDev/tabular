using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Ods;

/// <summary>
/// OpenDocument markup no writer produces, which a small file can use to make the reader misread,
/// crash or work without end — each refused as a library error, or read as what it means.
/// </summary>
public sealed class OdsHostileInputTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static OdsCursor Open(OdsPackage package, OdsCursorOptions? options = null) =>
        new(new MemoryStream(package.Build(), writable: false), options, cancellationToken: Token);

    private static string Cell(string attributes, string paragraph = "") =>
        $"<table:table-cell {attributes}>{(paragraph.Length > 0 ? $"<text:p>{paragraph}</text:p>" : "")}</table:table-cell>";

    private static string Text(string text) => Cell("office:value-type=\"string\"", text);

    private static string Row(params string[] cells) => "<table:table-row>" + string.Concat(cells) + "</table:table-row>";

    private static List<string?[]> ReadAll(OdsCursor cursor)
    {
        List<string?[]> rows = [];

        while (cursor.ReadRow(Token))
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

    private static string Content(string body) =>
        $"""<?xml version="1.0" encoding="UTF-8"?><office:document-content {OdsPackage.Namespaces}office:version="1.3"><office:body><office:spreadsheet>{body}</office:spreadsheet></office:body></office:document-content>""";

    [Fact]
    public void RefusesAValueBeyondTheColumnCeilingHoweverTheRepeatsBeforeItAddUp()
    {
        // Two empty repeats of long.MaxValue wrap a 64-bit column position to a negative one.
        const string Huge = "table:number-columns-repeated=\"9223372036854775807\"";
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", Row(Cell(Huge), Cell(Huge), Text("v"))));

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token));

        Assert.Equal(nameof(OdsCursorOptions.MaxColumns), error.Limit);
    }

    [Theory]
    [InlineData("2147483647")]
    [InlineData("9223372036854775807")]
    public void RefusesAValueBeyondTheRowCeilingHoweverTheEmptyRowsBeforeItAddUp(string repeat)
    {
        string rows = Row(Text("first"))
            + $"<table:table-row table:number-rows-repeated=\"{repeat}\"><table:table-cell/></table:table-row>"
            + Row(Text("after"));
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows));

        Assert.True(cursor.ReadRow(Token));
        Assert.Equal(1, cursor.CurrentRowNumber);
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token));
        Assert.Equal(nameof(OdsCursorOptions.MaxRows), error.Limit);
    }

    [Fact]
    public void BoundsTheCellsRepeatsMayHandOutAcrossTheFile()
    {
        // Under a kilobyte asking for three hundred million cells, each within the row and column
        // ceilings: the reading itself is the attack.
        string rows = "<table:table-row table:number-rows-repeated=\"20000\">"
            + Cell("table:number-columns-repeated=\"16384\" office:value-type=\"float\" office:value=\"1\"")
            + "</table:table-row>";

        TabularLimitException error = Assert.Throws<TabularLimitException>(() =>
        {
            using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows));
            ReadAll(cursor);
        });

        Assert.Equal(nameof(OdsCursorOptions.MaxRepeatedCells), error.Limit);
        Assert.Equal(100_000_000, OdsCursorOptions.Default.MaxRepeatedCells);
    }

    [Fact]
    public void ListsAndReadsOnlyTheSpreadsheetsOwnTables()
    {
        // A sub-table inside a cell and the cached table of a DDE link are tables in the markup but
        // not sheets; counting them in one pass and not the other reads one sheet's rows as another's.
        string sub = "<table:table-cell><table:table table:name=\"Sub\"><table:table-row>" + Text("sub") + "</table:table-row></table:table><text:p>outer</text:p></table:table-cell>";
        string body = $"""<table:table table:name="A">{Row(sub)}{Row(Text("a2"))}</table:table>"""
            + $"""<table:table table:name="B">{Row(Text("b"))}</table:table>"""
            + $"""<table:dde-links><table:dde-link><office:dde-source office:dde-application="x"/><table:table table:name="Cache">{Row(Text("dde"))}</table:table></table:dde-link></table:dde-links>""";

        using OdsCursor cursor = Open(new OdsPackage().WithRawContent(Content(body)));

        Assert.Equal(["A", "B"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal([["outer"], ["a2"]], ReadAll(cursor));
        Assert.True(cursor.MoveToSheet(1));
        Assert.Equal([["b"]], ReadAll(cursor));

        // The same sheet by index, whichever way the cursor got there.
        using OdsCursor direct = Open(new OdsPackage().WithRawContent(Content(body)));
        Assert.True(direct.MoveToSheet(1));
        Assert.Equal([["b"]], ReadAll(direct));
    }

    [Theory]
    [InlineData("<?><table:table table:name=\"Hidden\"><table:table-row><table:table-cell office:value-type=\"string\"><text:p>h</text:p></table:table-cell></table:table-row></table:table>")]
    [InlineData("<table:table\ftable:name=\"Hidden\"><table:table-row><table:table-cell office:value-type=\"string\"><text:p>h</text:p></table:table-cell></table:table-row></table:table>")]
    [InlineData("<x:y a=\"<table:table table:name='Ghost'>\"/>")]
    public void NeverReadsOneSheetsRowsUnderAnothersName(string before)
    {
        // Markup the two passes over the part might count differently. Whatever each makes of it, a
        // sheet is read under its own name or the file is refused — never another sheet's rows.
        string body = before + $"""<table:table table:name="A">{Row(Text("a"))}</table:table><table:table table:name="B">{Row(Text("b"))}</table:table>""";

        OdsCursor cursor;

        try
        {
            cursor = Open(new OdsPackage().WithRawContent(Content(body)));
        }
        catch (TabularFormatException)
        {
            return;
        }

        using (cursor)
        {
            for (int i = 0; i < cursor.Sheets.Count; i++)
            {
                string name = cursor.Sheets[i].Name;

                try
                {
                    Assert.True(cursor.MoveToSheet(i));
                    List<string?[]> rows = ReadAll(cursor);

                    // Every real table holds one row, the first letter of its name in lower case; a sheet
                    // no real table stands behind has no rows of anyone's.
                    Assert.Equal(name is "A" or "B" or "Hidden" ? [[name[..1].ToLowerInvariant()]] : [], rows);
                }
                catch (TabularFormatException refused)
                {
                    Assert.NotNull(refused.Code);
                }
            }
        }
    }

    [Fact]
    public void ListsATableWhosePrefixIsLong()
    {
        // Well-formed: a namespace prefix may be as long as the writer likes.
        string prefix = new('p', 300);
        string body = $"""<{prefix}:table xmlns:{prefix}="urn:oasis:names:tc:opendocument:xmlns:table:1.0" {prefix}:name="Long">{Row(Text("l"))}</{prefix}:table>""";

        using OdsCursor cursor = Open(new OdsPackage().WithRawContent(Content(body)));

        Assert.Equal("Long", Assert.Single(cursor.Sheets).Name);
        Assert.Equal([["l"]], ReadAll(cursor));
    }

    [Fact]
    public void RefusesAStatedStringLongerThanTheValueCeiling()
    {
        string stated = new('x', 5_000);
        using OdsCursor cursor = Open(
            new OdsPackage().WithTable("S", Row(Cell($"office:value-type=\"string\" office:string-value=\"{stated}\"", "shown"))),
            new OdsCursorOptions { MaxValueChars = 100 });

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token));

        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), error.Limit);
    }

    [Theory]
    [InlineData("<text:p>" + "0123456789" + "</text:p>")]
    [InlineData("<text:p>a<text:s text:c=\"50\"/></text:p>")]
    public void RefusesParagraphTextLongerThanTheValueCeiling(string paragraphs)
    {
        using OdsCursor cursor = Open(
            new OdsPackage().WithTable("S", "<table:table-row><table:table-cell office:value-type=\"string\">" + paragraphs + "</table:table-cell></table:table-row>"),
            new OdsCursorOptions { MaxValueChars = 5 });

        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token)).Limit);
    }

    [Fact]
    public void ReadsATimeOfSixtyDaysOrMoreOnTheDayAWorkbookSerialGives()
    {
        // LibreOffice writes a date-time under a time-only format as a duration since its null date.
        // The xlsx serial 45000.5 is noon on 15 March 2023, and this duration must read the same.
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", Row(
            Cell("office:value-type=\"time\" office:time-value=\"PT1080012H00M00S\""),
            Cell("office:value-type=\"time\" office:time-value=\"PT1440H\""))));

        Assert.True(cursor.ReadRow(Token));
        Assert.Equal(new DateTime(2023, 3, 15, 12, 0, 0, DateTimeKind.Unspecified), cursor.CurrentRow[0].Date);
        Assert.Equal(new DateTime(1900, 2, 28, 0, 0, 0, DateTimeKind.Unspecified), cursor.CurrentRow[1].Date);
    }

    [Fact]
    public void NeverReadsADateValueWithoutADateAsToday()
    {
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", Row(Cell("office:value-type=\"date\" office:date-value=\"13:45\"", "13:45"))));

        Assert.True(cursor.ReadRow(Token));
        Assert.Equal(RawCell.FromText("13:45"), cursor.CurrentRow[0]);
    }

    [Fact]
    public void StopsOpeningWhenAskedTo()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        TrackedStream stream = new(new OdsPackage().WithTable("S", Row(Text("v"))).Build());

        Assert.ThrowsAny<OperationCanceledException>(() => new OdsCursor(stream, cancellationToken: cancelled.Token));
        Assert.True(stream.IsDisposed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("x")]
    public void RefusesARepeatCountThatIsNoCount(string repeat)
    {
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", Row(Cell($"table:number-columns-repeated=\"{repeat}\" office:value-type=\"string\"", "v"))));

        Assert.Equal(TabularFormatException.Corrupt, Assert.Throws<TabularFormatException>(() => cursor.ReadRow(Token)).Code);
    }

    [Fact]
    public void RefusesMoreSheetsThanTheCeilingInEitherEncoding()
    {
        OdsPackage package = new();

        for (int i = 0; i < 4; i++)
        {
            package.WithTable($"S{i}", Row(Text("v")));
        }

        OdsCursorOptions options = new() { MaxSheets = 3 };
        Assert.Equal(nameof(OdsCursorOptions.MaxSheets), Assert.Throws<TabularLimitException>(() => Open(package, options)).Limit);
        Assert.Equal(nameof(OdsCursorOptions.MaxSheets), Assert.Throws<TabularLimitException>(() => Open(package.WithContentEncoding(Encoding.Unicode), options)).Limit);
    }

    [Fact]
    public void RefusesAPackageBeyondItsEntryOrSizeBudgetAndClosesTheStream()
    {
        byte[] package = new OdsPackage().WithTable("S", Row(Text(new string('v', 3000)))).Build();

        TrackedStream entries = new(package);
        Assert.Equal(nameof(OdsCursorOptions.MaxPackageEntries), Assert.Throws<TabularLimitException>(
            () => new OdsCursor(entries, new OdsCursorOptions { MaxPackageEntries = 2 }, cancellationToken: Token)).Limit);
        Assert.True(entries.IsDisposed);

        TrackedStream bytes = new(package);
        Assert.Equal(nameof(OdsCursorOptions.MaxUncompressedBytes), Assert.Throws<TabularLimitException>(
            () => new OdsCursor(bytes, new OdsCursorOptions { MaxUncompressedBytes = 1000 }, cancellationToken: Token)).Limit);
        Assert.True(bytes.IsDisposed);

        TrackedStream kept = new(package);
        Assert.Throws<TabularLimitException>(() => new OdsCursor(kept, new OdsCursorOptions { MaxPackageEntries = 2 }, leaveOpen: true, Token));
        Assert.False(kept.IsDisposed);
    }

    [Fact]
    public void RefusesAContentPartThatStopsInsideASheet()
    {
        string cut = Content($"""<table:table table:name="A">{Row(Text("a"))}""");
        cut = cut[..cut.IndexOf("</office:spreadsheet>", StringComparison.Ordinal)];

        using OdsCursor cursor = Open(new OdsPackage().WithRawContent(cut));

        Assert.True(cursor.ReadRow(Token));
        Assert.Equal(TabularFormatException.Truncated, Assert.Throws<TabularFormatException>(() => cursor.ReadRow(Token)).Code);
    }

    [Fact]
    public void TakesItsBoundsFromTheOpenOptions()
    {
        byte[] package = new OdsPackage().WithTable("S", Row(Text("v"))).Build();

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => TabularFile.Open(
            new MemoryStream(package), "t.ods", new TabularOpenOptions { Ods = new OdsCursorOptions { MaxPackageEntries = 1 } }, Token));

        Assert.Equal(nameof(OdsCursorOptions.MaxPackageEntries), error.Limit);
    }

    private sealed class TrackedStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
