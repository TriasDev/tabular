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

    /// <summary>
    /// One budget for every copy a repeat adds, row after row: a repeated row's copies count each of
    /// its cells, a repeated cell its copies; the copies that use the budget up exactly are read, and
    /// the one after them is refused.
    /// </summary>
    [Fact]
    public void CountsEveryCopyAgainstOneBudgetUpToItsLastCell()
    {
        string rows = "<table:table-row table:number-rows-repeated=\"3\">" + Text("r1") + Text("r2") + "</table:table-row>"   // 2 copies of 2 cells
            + Row(Cell("table:number-columns-repeated=\"7\" office:value-type=\"string\"", "c"))                                   // 6 copies: 10 in all
            + Row(Cell("table:number-columns-repeated=\"2\" office:value-type=\"string\"", "d"));                                  // 1 more
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", rows), new OdsCursorOptions { MaxRepeatedCells = 10 });

        for (int i = 0; i < 3; i++)
        {
            Assert.True(cursor.ReadRow(Token));
            Assert.Equal(["r1", "r2"], cursor.CurrentRow.ToArray().Select(c => c.AsText()));
        }

        Assert.True(cursor.ReadRow(Token));
        Assert.Equal(7, cursor.CurrentRow.Length);
        Assert.Equal(nameof(OdsCursorOptions.MaxRepeatedCells), Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token)).Limit);
    }

    /// <summary>The package of a content part, in UTF-16 when asked: then the sheets are listed by tokenizing it rather than by the byte pass.</summary>
    private static OdsPackage Encoded(string content, bool utf16)
    {
        OdsPackage package = new OdsPackage().WithRawContent(content);
        return utf16 ? package.WithContentEncoding(Encoding.Unicode) : package;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListsAndReadsOnlyTheSpreadsheetsOwnTables(bool utf16)
    {
        // A sub-table inside a cell and the cached table of a DDE link are tables in the markup but
        // not sheets; counting them in one pass and not the other reads one sheet's rows as another's.
        // The second sub-table stands where a count that went wrong on the first would call it a sheet.
        string sub = "<table:table-cell><table:table table:name=\"Sub\"><table:table-row>" + Text("sub") + "</table:table-row></table:table><text:p>outer</text:p></table:table-cell>";
        string other = "<table:table-cell><table:table table:name=\"Other\"><table:table-row>" + Text("other") + "</table:table-row></table:table><text:p>second</text:p></table:table-cell>";
        string body = $"""<table:table table:name="A">{Row(sub)}{Row(other)}{Row(Text("a2"))}</table:table>"""
            + $"""<table:table table:name="B">{Row(Text("b"))}</table:table>"""
            + $"""<table:dde-links><table:dde-link><office:dde-source office:dde-application="x"/><table:table table:name="Cache">{Row(Text("dde"))}</table:table></table:dde-link></table:dde-links>""";

        using OdsCursor cursor = Open(Encoded(Content(body), utf16));

        Assert.Equal(["A", "B"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal([["outer"], ["second"], ["a2"]], ReadAll(cursor));
        Assert.True(cursor.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal([["b"]], ReadAll(cursor));

        // The same sheet by index, whichever way the cursor got there.
        using OdsCursor direct = Open(Encoded(Content(body), utf16));
        Assert.True(direct.MoveToSheet(1, TestContext.Current.CancellationToken));
        Assert.Equal([["b"]], ReadAll(direct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATableBetweenASheetsRowsEndsNeitherTheSheetNorAddsItsRows(bool utf16)
    {
        // A table standing between the rows rather than inside a cell: the row reader never sees it,
        // so the sheet itself must count it — its end is not the sheet's end, its rows not the sheet's.
        string inner = $"""<table:table table:name="Inner">{Row(Text("inner 1"))}<table:table table:name="Deeper">{Row(Text("deeper"))}</table:table>{Row(Text("inner 2"))}</table:table>""";
        string body = $"""<table:table table:name="A">{Row(Text("a1"))}{inner}{Row(Text("a2"))}</table:table>"""
            + $"""<table:table table:name="B">{Row(Text("b"))}</table:table>""";

        using OdsCursor cursor = Open(Encoded(Content(body), utf16));

        Assert.Equal(["A", "B"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal([["a1"], ["a2"]], ReadAll(cursor));
        Assert.True(cursor.MoveToSheet(1, Token));
        Assert.Equal([["b"]], ReadAll(cursor));
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
                    Assert.True(cursor.MoveToSheet(i, TestContext.Current.CancellationToken));
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

    /// <summary>
    /// Tables written without a prefix, their namespace the default one: the name pass finds the
    /// local name of a start tag and of an end tag alike, so it counts the second table as a sheet
    /// once the first has ended, as the reading does.
    /// </summary>
    [Fact]
    public void ListsTablesWrittenWithoutAPrefix()
    {
        static string Table(string name, string text) =>
            $"""<table xmlns="urn:oasis:names:tc:opendocument:xmlns:table:1.0" table:name="{name}"><table-row><table-cell office:value-type="string"><text:p>{text}</text:p></table-cell></table-row></table>""";

        using OdsCursor cursor = Open(new OdsPackage().WithRawContent(Content(Table("A", "a") + Table("B", "b"))));

        Assert.Equal(["A", "B"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal([["a"]], ReadAll(cursor));
        Assert.True(cursor.MoveToSheet(1, Token));
        Assert.Equal([["b"]], ReadAll(cursor));
    }

    /// <summary>
    /// A table inside a processing instruction, a comment or a CDATA section is no table, though a
    /// '>' stands ahead of it there: the name pass skips each construct to its own end, as the
    /// reading does, and a declaration to its '>' — also a comment longer than its buffer, and also
    /// when the part arrives a byte at a time, so that every end straddles two reads. Against a
    /// deadline, since a skip that never moves on loops for ever.
    /// </summary>
    [Theory]
    [InlineData("<?pi a>b <table:table table:name=\"Ghost\"> ?>", 0, false)]
    [InlineData("<!-- a>b <table:table table:name=\"Ghost\"> -->", 0, false)]
    [InlineData("<![CDATA[ a>b <table:table table:name=\"Ghost\"> ]]>", 0, false)]
    [InlineData("<!-- a>b <table:table table:name=\"Ghost\"> # -->", 100_000, false)]
    [InlineData("<?pi a>b <table:table table:name=\"Ghost\"> ?>", 0, true)]
    [InlineData("<!-- a>b <table:table table:name=\"Ghost\"> -->", 0, true)]
    [InlineData("<![CDATA[ a>b <table:table table:name=\"Ghost\"> ]]>", 0, true)]
    [InlineData("<!-- a>b <table:table table:name=\"Ghost\"> # -->", 100_000, true)]
    public async Task ListsNoTableThatStandsInsideMarkupTheReadingSkips(string markup, int filler, bool byteAtATime)
    {
        string body = markup.Replace("#", new string('c', filler), StringComparison.Ordinal)
            + $"""<table:table table:name="A">{Row(Text("a"))}</table:table><table:table table:name="B">{Row(Text("b"))}</table:table>""";
        string content = Content(body).Replace("?><office:document-content", "?><!DOCTYPE office:document-content><office:document-content", StringComparison.Ordinal);
        byte[] package = Stored(new OdsPackage().WithRawContent(content).Build());

        (string[] sheets, string[] rows) = await WithinDeadline(token =>
        {
            using OdsCursor cursor = new(byteAtATime ? new Trickle(package) : new MemoryStream(package, writable: false), cancellationToken: token);
            List<string> read = [];

            for (int sheet = 0; cursor.MoveToSheet(sheet, token); sheet++)
            {
                while (cursor.ReadRow(token))
                {
                    read.Add(cursor.CurrentRow[0].AsText()!);
                }
            }

            return (cursor.Sheets.Select(s => s.Name).ToArray(), read.ToArray());
        });

        Assert.Equal(["A", "B"], sheets);
        Assert.Equal(["a", "b"], rows);
    }

    /// <summary>The same package with every entry stored, so a stream handed to it reaches the content part's reader unchanged.</summary>
    private static byte[] Stored(byte[] package)
    {
        using MemoryStream stored = new();

        using (System.IO.Compression.ZipArchive source = new(new MemoryStream(package), System.IO.Compression.ZipArchiveMode.Read))
        using (System.IO.Compression.ZipArchive target = new(stored, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (System.IO.Compression.ZipArchiveEntry entry in source.Entries)
            {
                using Stream from = entry.Open();
                using Stream to = target.CreateEntry(entry.FullName, System.IO.Compression.CompressionLevel.NoCompression).Open();
                from.CopyTo(to);
            }
        }

        return stored.ToArray();
    }

    /// <summary>A stream that hands out at most one byte a read, as a slow network may.</summary>
    private sealed class Trickle(byte[] content) : MemoryStream(content, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
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

    /// <summary>
    /// The value ceiling is the longest value read, not the first refused: a stated string, paragraph
    /// text and stated spaces of exactly that many characters are read, one more is refused.
    /// </summary>
    [Theory]
    [InlineData("office:value-type=\"string\" office:string-value=\"12345\"", "shown", "12345")]
    [InlineData("office:value-type=\"string\"", "12345", "12345")]
    [InlineData("office:value-type=\"string\"", "a<text:s text:c=\"3\"/>b", "a   b")]
    [InlineData("office:value-type=\"string\" office:string-value=\"123456\"", "shown", null)]
    [InlineData("office:value-type=\"string\"", "123456", null)]
    [InlineData("office:value-type=\"string\"", "a<text:s text:c=\"4\"/>b", null)]
    public void ReadsAValueOfExactlyTheValueCeilingAndRefusesOneCharacterMore(string attributes, string paragraph, string? expected)
    {
        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", Row(Cell(attributes, paragraph))), new OdsCursorOptions { MaxValueChars = 5 });

        if (expected is null)
        {
            Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token)).Limit);
        }
        else
        {
            Assert.Equal([[expected]], ReadAll(cursor));
        }
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

    /// <summary>A package of exactly as many entries and bytes as the budgets allow is opened; a byte more is refused.</summary>
    [Fact]
    public void OpensAPackageOfExactlyItsEntryAndSizeBudgets()
    {
        byte[] package = new OdsPackage().WithTable("S", Row(Text("v"))).Build();
        int entries;
        long bytes;

        using (System.IO.Compression.ZipArchive zip = new(new MemoryStream(package), System.IO.Compression.ZipArchiveMode.Read))
        {
            entries = zip.Entries.Count;
            bytes = zip.Entries.Sum(e => e.Length);
        }

        using (OdsCursor cursor = new(new MemoryStream(package), new OdsCursorOptions { MaxPackageEntries = entries, MaxUncompressedBytes = bytes }, cancellationToken: Token))
        {
            Assert.Equal([["v"]], ReadAll(cursor));
        }

        Assert.Equal(nameof(OdsCursorOptions.MaxUncompressedBytes), Assert.Throws<TabularLimitException>(
            () => new OdsCursor(new MemoryStream(package), new OdsCursorOptions { MaxUncompressedBytes = bytes - 1 }, cancellationToken: Token)).Limit);
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

    /// <summary>
    /// A part that ends inside a row — between its cells, in a cell's text, in a cell passed over or
    /// inside a tag — is refused at the first read, never handed out as a row of the cells before
    /// the cut.
    /// </summary>
    [Theory]
    [InlineData("<table:table-row><table:table-cell office:value-type=\"string\"><text:p>a</text:p></table:table-cell>")]
    [InlineData("<table:table-row><table:table-cell office:value-type=\"string\"><text:p>a")]
    [InlineData("<table:table-row><table:table-cell office:value-type=\"float\" office:value=\"1\"><text:p>1")]
    [InlineData("<table:table-row><table:covered-table-cell><text:p>c")]
    [InlineData("<table:table-row><table:table-cell office:value-type=\"string\"><text:p>a</text:p></table:table-cell><table:table-cell office:value-type=\"str")]
    public void RefusesAContentPartThatStopsInsideARowAtTheFirstRead(string cut)
    {
        string content = Content($"""<table:table table:name="A">{cut}""");
        content = content[..content.IndexOf("</office:spreadsheet>", StringComparison.Ordinal)];

        using OdsCursor cursor = Open(new OdsPackage().WithRawContent(content));

        Assert.Equal(TabularFormatException.Truncated, Assert.Throws<TabularFormatException>(() => cursor.ReadRow(Token)).Code);
    }

    /// <summary>
    /// A part that ends inside a tag behind the first sheet — in an element's name, a start or end
    /// tag, an attribute value, a style or a comment — lists the sheets before the cut and reads
    /// them. The name pass stops where the part does: it neither goes round the cut for ever nor
    /// looks past its data — a name cut off after more characters than stand before it leaves the
    /// buffer holding nothing but that name behind its end. Run against a deadline, so a pass that
    /// never returns fails the test rather than holding up the run.
    /// </summary>
    [Theory]
    [InlineData("<")]
    [InlineData("<table:tab")]
    [InlineData("<table:", 4000)]
    [InlineData("<table:table")]
    [InlineData("<table:table table:name=\"B\"")]
    [InlineData("<table:table table:name=\"B>")]
    [InlineData("</table:tab")]
    [InlineData("<style:style style:name=\"ta1\" style:family=\"table\"")]
    [InlineData("<style:style style:name=\"ta1\" style:family=\"table\"><style:table-properties table:display=\"false\"")]
    [InlineData("<!-- <table:table table:name=\"B\">")]
    [InlineData("<![CDATA[<table:table table:name=\"B\">")]
    [InlineData("<?pi <table:table table:name=\"B\">")]
    public async Task ListsAndReadsTheSheetsBeforeAContentPartThatStopsInsideATag(string cut, int nameFiller = 0)
    {
        string content = Content($"""<table:table table:name="A">{Row(Text("a"))}</table:table>""");
        content = content[..content.IndexOf("</office:spreadsheet>", StringComparison.Ordinal)] + cut + new string('x', nameFiller);
        byte[] package = new OdsPackage().WithRawContent(content).Build();

        (string[] sheets, List<string> rows) = await WithinDeadline(token =>
        {
            using OdsCursor cursor = new(new MemoryStream(package, writable: false), cancellationToken: token);
            List<string> read = [];

            while (cursor.ReadRow(token))
            {
                read.Add(cursor.CurrentRow[0].AsText()!);
            }

            return (cursor.Sheets.Select(s => s.Name).ToArray(), read);
        });

        Assert.Equal(["A"], sheets);
        Assert.Equal(["a"], rows);
    }

    /// <summary>
    /// Runs work that must end on its own, and fails once a deadline passes rather than waiting for
    /// it: cancelled through its token if it checks one, abandoned if it does not. The work has a
    /// background thread of its own, so an abandoned loop neither holds a pool thread nor the process.
    /// </summary>
    private static async Task<T> WithinDeadline<T>(Func<CancellationToken, T> work)
    {
        TimeSpan deadline = TimeSpan.FromSeconds(20);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.CancelAfter(deadline);
        CancellationToken token = stop.Token;
        TaskCompletionSource<T> done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread thread = new(() =>
        {
            try
            {
                done.SetResult(work(token));
            }
            catch (Exception failed)
            {
                done.SetException(failed);
            }
        })
        { IsBackground = true };
        thread.Start();

        return await done.Task.WaitAsync(deadline + TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public void TakesItsBoundsFromTheOpenOptions()
    {
        byte[] package = new OdsPackage().WithTable("S", Row(Text("v"))).Build();

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => TabularFile.Open(
            new MemoryStream(package), "t.ods", new TabularOpenOptions { Ods = new OdsCursorOptions { MaxPackageEntries = 1 } }, Token));

        Assert.Equal(nameof(OdsCursorOptions.MaxPackageEntries), error.Limit);
    }

    [Fact]
    public void HoldsATableTagToAConfiguredValueCeilingInEitherEncoding()
    {
        // The sheet-name pass held one tag to sixteen million characters whatever the options said.
        string table = $"""<table:table table:name="A" table:print-ranges="{new string('x', 200_000)}">{Row(Text("a"))}</table:table>""";
        OdsPackage package = new OdsPackage().WithRawContent(Content(table));
        OdsCursorOptions options = new() { MaxValueChars = 100_000 };

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(package, options));
        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), error.Limit);
        Assert.Equal(100_000 + (64 * 1024), error.Maximum);

        error = Assert.Throws<TabularLimitException>(() => Open(package.WithContentEncoding(Encoding.Unicode), options));
        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), error.Limit);
        Assert.Equal(100_000 + (64 * 1024), error.Maximum);

        // The defaults read it as before.
        using OdsCursor cursor = Open(new OdsPackage().WithRawContent(Content(table)));
        Assert.Equal("a", Assert.Single(ReadAll(cursor))[0]);
    }

    /// <summary>
    /// The name pass holds a table's start tag to the configured ceiling itself, at the three bytes
    /// UTF-8 spends on a character such as '€' — not only the reading, which on opening reaches no
    /// further than the first sheet. A later sheet's tag past the ceiling is refused when the file is
    /// opened; one within it is listed and read.
    /// </summary>
    [Fact]
    public void HoldsALaterSheetsTableTagToTheConfiguredCeilingWhenOpening()
    {
        // 100,000 characters and the 64 K of headroom: 165,536 characters, 496,608 bytes.
        OdsCursorOptions options = new() { MaxValueChars = 100_000 };
        static OdsPackage Package(int euros) => new OdsPackage().WithRawContent(Content(
            $"""<table:table table:name="A">{Row(Text("a"))}</table:table>"""
            + $"""<table:table table:name="B" table:print-ranges="{new string('€', euros)}">{Row(Text("b"))}</table:table>"""));

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => Open(Package(170_000), options));
        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), error.Limit);
        Assert.Equal(100_000 + (64 * 1024), error.Maximum);

        using OdsCursor cursor = Open(Package(160_000), options);
        Assert.Equal(["A", "B"], cursor.Sheets.Select(s => s.Name));
        Assert.True(cursor.MoveToSheet(1, Token));
        Assert.Equal([["b"]], ReadAll(cursor));
    }

    [Fact]
    public void HoldsATextNodeNoValueIsAssembledFromToAConfiguredValueCeiling()
    {
        // A number cell's paragraphs are passed over, never assembled — so only the scanner's own
        // node ceiling stood between them and memory, at sixteen million characters.
        string cell = Cell("office:value-type=\"float\" office:value=\"1\"", new string('9', 200_000));

        using OdsCursor bounded = Open(new OdsPackage().WithTable("S", Row(cell)), new OdsCursorOptions { MaxValueChars = 100_000 });
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => bounded.ReadRow(Token));
        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), error.Limit);
        Assert.Equal(100_000 + (64 * 1024), error.Maximum);

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", Row(cell)));
        Assert.Equal("1", Assert.Single(ReadAll(cursor))[0]);
    }

    [Fact]
    public void HoldsASheetNameToTheValueCeilingInEitherEncoding()
    {
        // A sheet name had a ceiling on how many, none on how long; the value ceiling serves.
        OdsPackage package = new OdsPackage().WithTable(new string('n', 11), Row(Text("v")));
        OdsCursorOptions options = new() { MaxValueChars = 10 };

        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), Assert.Throws<TabularLimitException>(() => Open(package, options)).Limit);
        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), Assert.Throws<TabularLimitException>(() => Open(package.WithContentEncoding(Encoding.Unicode), options)).Limit);

        using OdsCursor cursor = Open(new OdsPackage().WithTable(new string('n', 10), Row(Text("v"))), options);
        Assert.Equal(new string('n', 10), Assert.Single(cursor.Sheets).Name);

        using OdsCursor utf16 = Open(new OdsPackage().WithTable(new string('n', 10), Row(Text("v"))).WithContentEncoding(Encoding.Unicode), options);
        Assert.Equal(new string('n', 10), Assert.Single(utf16.Sheets).Name);
    }

    [Fact]
    public void RefusesContentThatCannotBeInflatedMidReadAsACorruptFile()
    {
        // Damage behind what the opening reads: the name pass inflated the part and found it whole, the
        // bytes changed under the cursor, and the read meets a deflate stream the BCL refuses with its
        // own InvalidDataException — which reaches a caller as the library's format.corrupt.
        Random random = new(14);
        StringBuilder rows = new();

        for (int i = 0; i < 20_000; i++)
        {
            char[] text = new char[40];

            for (int j = 0; j < text.Length; j++)
            {
                text[j] = (char)('a' + random.Next(26));
            }

            rows.Append(Row(Text(new string(text))));
        }

        byte[] package = new OdsPackage().WithTable("S", rows.ToString()).Build();
        using OdsCursor cursor = new(new MemoryStream(package, writable: false), cancellationToken: Token);

        // Overwritten in place: the memory stream reads the same array.
        package.AsSpan(package.Length / 2, package.Length / 10).Fill(0xFF);

        TabularFormatException error = Assert.Throws<TabularFormatException>(() =>
        {
            while (cursor.ReadRow(Token))
            {
                // read on to the damage
            }
        });

        Assert.Equal(TabularFormatException.Corrupt, error.Code);
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(Token));
    }

    /// <summary>
    /// Any failure part-way through a row leaves the scanner inside it, so the cursor refuses to read
    /// on rather than hand out the rest of that row as the next one — a ceiling's refusal too, which
    /// a caller may well catch and try to continue past.
    /// </summary>
    [Fact]
    public void RefusesToReadOnAfterAReadFailedPartWayThroughARow()
    {
        using OdsCursor cursor = Open(
            new OdsPackage().WithTable("S", Row(Text("0123456789"), Text("rest")) + Row(Text("next"))),
            new OdsCursorOptions { MaxValueChars = 5 });

        Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token));

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(Token));
        Assert.Contains("cannot continue", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverRefusesAStatedStringWithinTheValueCeilingForTheLengthOfItsTag()
    {
        // The node ceiling holds a whole tag, and office:string-value shares its tag with the cell's
        // other attributes: a value just within the ceiling made a tag just beyond it.
        string value = new('v', 99_990);
        string cell = Cell($"table:style-name=\"ce1\" office:value-type=\"string\" calcext:value-type=\"string\" office:string-value=\"{value}\"", "shown");

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", Row(cell)), new OdsCursorOptions { MaxValueChars = 100_000 });

        Assert.Equal(value, Assert.Single(ReadAll(cursor))[0]);
    }

    [Fact]
    public void ReportsTheNodeCeilingInForceBelowTheFirstBuffer()
    {
        // Nothing within the first buffer is refused, so a ceiling of ten does not refuse at ten.
        string cell = Cell("office:value-type=\"float\" office:value=\"1\"", new string('9', 200_000));

        using OdsCursor cursor = Open(new OdsPackage().WithTable("S", Row(cell)), new OdsCursorOptions { MaxValueChars = 10 });
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(Token));

        Assert.Equal(nameof(OdsCursorOptions.MaxValueChars), error.Limit);
        Assert.Equal(10 + (64 * 1024), error.Maximum);
    }
}
