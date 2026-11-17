using System.IO.Compression;
using System.Text;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The OpenDocument spreadsheet the ods writer streams, read by the library's own cursor as written.</summary>
public sealed class OdsWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<byte[]> Spreadsheet(Action<TabularWriter> write)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            write(writer);
            await writer.CompleteAsync(Token);
        }

        return target.ToArray();
    }

    private static List<RawCell[]> Rows(byte[] ods, int sheet = 0)
    {
        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.True(cursor.MoveToSheet(sheet, Token));

        List<RawCell[]> rows = [];

        while (cursor.ReadRow(Token))
        {
            rows.Add(cursor.CurrentRow.ToArray());
        }

        return rows;
    }

    private static string Content(byte[] ods) => Part(ods, "content.xml");

    private static string Part(byte[] ods, string name)
    {
        using ZipArchive archive = new(new MemoryStream(ods, writable: false), ZipArchiveMode.Read);
        using StreamReader reader = new(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task<RawCell[]> Column(params string[] values)
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);

            foreach (string value in values)
            {
                writer.BeginRow();
                writer.Write(value);
                writer.EndRow();
            }
        });

        return [.. Rows(ods).Skip(1).Select(r => r[0])];
    }

    [Fact]
    public async Task WritesASpreadsheetTheCursorReadsAndDetects()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("Portfolios", [new("Id"), new("Name"), new("Active")]);
            writer.BeginRow();
            writer.Write("P-1");
            writer.Write("Alpha");
            writer.Write(true);
            writer.EndRow();
            writer.BeginRow();
            writer.WriteEmpty();
            writer.Write("Beta");
            writer.Write(false);
            writer.EndRow();
        });

        Assert.Equal(TabularFormat.Ods, TabularFile.Detect(new MemoryStream(ods)));

        List<RawCell[]> rows = Rows(ods);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Id", "Name", "Active"], rows[0].Select(c => c.Text));
        Assert.Equal(RawCell.FromText("P-1"), rows[1][0]);
        Assert.Equal(RawCell.FromBoolean(true), rows[1][2]);
        Assert.True(rows[2][0].IsEmpty);
        Assert.Equal(RawCell.FromText("Beta"), rows[2][1]);
        Assert.Equal(RawCell.FromBoolean(false), rows[2][2]);
    }

    [Fact]
    public async Task StoresTheMimetypeFirstAsOpenDocumentRequires()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("data", [new("a")]));

        Assert.Equal("mimetype", Encoding.ASCII.GetString(ods, 30, 8));
        Assert.Equal("application/vnd.oasis.opendocument.spreadsheet", Encoding.ASCII.GetString(ods, 38, 46));
        Assert.Equal(0, ods[8] | ods[9]);                                   // stored
        Assert.Equal(0, ods[6] & 0x08);                                    // no data descriptor

        using ZipArchive archive = new(new MemoryStream(ods, writable: false), ZipArchiveMode.Read);
        Assert.Equal(["mimetype", "content.xml", "META-INF/manifest.xml", "styles.xml"], archive.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task WritesSeveralSheetsInOneContentPart()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("First", [new("a")]);
            writer.BeginRow();
            writer.Write("1");
            writer.EndRow();
            writer.BeginSheet("Second", [new("b")]);
            writer.BeginRow();
            writer.Write("2");
            writer.EndRow();
        });

        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.Equal(["First", "Second"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal(RawCell.FromText("2"), Rows(ods, 1)[1][0]);
    }

    [Fact]
    public async Task NamesSheetsAsWrittenEvenWithXmlCharacters()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("R&D <2026> \"q\"", [new("a")]));

        using OdsCursor cursor = new(new MemoryStream(ods, writable: false), cancellationToken: Token);
        Assert.Equal("R&D <2026> \"q\"", Assert.Single(cursor.Sheets).Name);
    }

    [Fact]
    public async Task WritesTextXmlCannotCarryLiterally()
    {
        string[] values = ["a & b < c > d", "Grüße 👍", "_x0041_ stays", "quote \" and apostrophe '"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
    }

    [Fact]
    public async Task WritesSpacesTabsAndLineBreaksAsTheReaderReadsThem()
    {
        string[] values = ["a  b", "x   y", "tab\there", "\tlead tab", "two\nlines", "x\n\ny", "a\n  indented", "a \t b"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
    }

    [Fact]
    public async Task KeepsACarriageReturnExactly()
    {
        string[] values = ["windows\r\nline", "lone\rreturn", "mixed\r\n\nlines\r"];

        Assert.Equal(values.Select(v => RawCell.FromText(v)), await Column(values));
        Assert.Contains("office:string-value=\"windows&#13;&#10;line\"", Content(await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write("windows\r\nline");
            writer.EndRow();
        })), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesColumnWidthsRoundedToWholeCharacters()
    {
        byte[] ods = await Spreadsheet(writer => writer.BeginSheet("data", [new("a", 12.4), new("b"), new("c", 30)]));

        string content = Content(ods);

        Assert.Contains("<table:table-column table:style-name=\"co12\"/><table:table-column table:style-name=\"co3\"/><table:table-column table:style-name=\"co30\"/>", content, StringComparison.Ordinal);
        Assert.Contains("style:name=\"co255\"", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A column style's width is Excel's measure of so many characters — 7 pixels each and 5 of
    /// padding, at 96 pixels to the inch — so a column is as wide in LibreOffice as in an xlsx.
    /// </summary>
    [Fact]
    public async Task StatesAColumnWidthInInchesAsExcelMeasuresIt()
    {
        string content = Content(await Spreadsheet(writer => writer.BeginSheet("data", [new("a", 1), new("b", 19)])));

        Assert.Contains("<style:style style:name=\"co1\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"0.125in\"/></style:style>", content, StringComparison.Ordinal);
        Assert.Contains("<style:style style:name=\"co19\" style:family=\"table-column\"><style:table-column-properties style:column-width=\"1.4375in\"/></style:style>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesTextLongerThanTheReaderReads()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write(new string('x', OdsCursorOptions.Default.MaxValueChars + 1))).Code);
    }

    [Theory]
    [InlineData('&', 5)]
    [InlineData('<', 4)]
    [InlineData('>', 4)]
    [InlineData('\r', 5)]
    [InlineData('x', 1)]
    [InlineData('"', 1)]
    [InlineData('\t', 1)]
    [InlineData('\n', 1)]
    public async Task RefusesTextWhoseEscapedFormExceedsTheReadersTokenLimit(char c, int escaped)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        // One character more than AcceptsTheLongestEscapedTextTheReaderTakes writes.
        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write(new string(c, ((16 * 1024 * 1024 - 1024) / escaped) + 1))).Code);
    }

    [Fact]
    public async Task RefusesQuotesInAnAttributeWhoseEscapedFormExceedsTheReadersTokenLimit()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();
        string value = "a\r" + new string('"', 2_900_000);

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write(value)).Code);
    }

    /// <summary>
    /// A value holding a carriage return is written twice, the second time as an attribute, where
    /// each character takes its escaped width: one more character than the reader's token limit
    /// allows that start tag is refused, whichever character fills it.
    /// </summary>
    [Theory]
    [InlineData('x', 1)]
    [InlineData('&', 5)]
    [InlineData('<', 4)]
    [InlineData('\t', 4)]
    public async Task RefusesTextWhoseAttributeFormExceedsTheReadersTokenLimit(char c, int escaped)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();
        string value = "\r" + new string(c, ((16 * 1024 * 1024 - 1024 - 85) / escaped) + 1);

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write(value)).Code);
    }

    [Fact]
    public async Task RefusesQuotesJustPastTheQuickCheckBeforeCountingThem()
    {
        // Six characters a quote in the attribute: 2,796,200 of them come to just over the limit,
        // close enough that a quick check counted wrongly would let the value through unmeasured.
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write("a\r" + new string('"', 2_796_200))).Code);
    }

    /// <summary>
    /// The quick check has to count the start tag's own markup, as the full count does: 2,796,032
    /// characters at six each come to exactly the limit, so a check without the tag's overhead let
    /// this value through uncounted, though its start tag is 79 characters past what the reader takes.
    /// </summary>
    [Fact]
    public async Task RefusesQuotesWhoseStartTagOnlyItsOverheadTakesPastTheLimit()
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);
        writer.BeginSheet("data", [new("v")]);
        writer.BeginRow();

        Assert.Equal(ErrorCodes.Write.TextTooLong, Assert.Throws<TabularWriteException>(() => writer.Write("\r" + new string('"', 2_796_031))).Code);
    }

    [Fact]
    public async Task AcceptsTheLongestTextTheReaderTakes()
    {
        string value = new('x', 16 * 1024 * 1024 - 2048);

        Assert.Equal([RawCell.FromText(value)], await Column(value));
    }

    /// <summary>
    /// The other side of the escaped-length rule: text whose escaped form just fits under the reader's
    /// token limit is written and read back whole, so the writer refuses only what the reader would.
    /// </summary>
    [Theory]
    [InlineData('<', 4)]
    [InlineData('>', 4)]
    [InlineData('&', 5)]
    public async Task AcceptsTheLongestEscapedTextTheReaderTakes(char c, int escaped)
    {
        string value = new(c, (16 * 1024 * 1024 - 1024) / escaped);

        Assert.Equal([RawCell.FromText(value)], await Column(value));
    }

    [Fact]
    public async Task WritesTheMarkupLibreOfficeExpects()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write("a  b\tc");
            writer.EndRow();
        });

        Assert.Contains("a <text:s text:c=\"1\"/>b<text:tab/>c", Content(ods), StringComparison.Ordinal);
    }

    /// <summary>
    /// A space at the start of a paragraph, or right after a tab or a line break, is one ODF drops when
    /// it stands as a character — LibreOffice does, and so does our reader — so it is written as
    /// <c>text:s</c>, and the value reads back with it.
    /// </summary>
    [Theory]
    [InlineData("a\t b", "a<text:tab/><text:s text:c=\"1\"/>b</text:p>")]
    [InlineData("a\n b", "a</text:p><text:p><text:s text:c=\"1\"/>b</text:p>")]
    [InlineData(" lead", "<text:p><text:s text:c=\"1\"/>lead</text:p>")]
    public async Task StatesASpaceAfterATabOrLineBreakOrAtTheStartAsAnElement(string value, string markup)
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write(value);
            writer.EndRow();
        });

        Assert.Contains(markup, Content(ods), StringComparison.Ordinal);
        Assert.Equal(RawCell.FromText(value), Rows(ods)[1][0]);
    }

    /// <summary>
    /// A sheet without an auto-filter and a file without frozen panes leave nothing in their place:
    /// the content ends with the table's end and the document's, the manifest lists the three parts
    /// written and nothing else.
    /// </summary>
    [Fact]
    public async Task WritesNothingForAFilterOrSettingsTheFileDoesNotHave()
    {
        byte[] ods = await Spreadsheet(writer =>
        {
            writer.BeginSheet("data", [new("v")]);
            writer.BeginRow();
            writer.Write("x");
            writer.EndRow();
        });

        Assert.EndsWith(
            "<text:p>x</text:p></table:table-cell></table:table-row></table:table></office:spreadsheet></office:body></office:document-content>",
            Content(ods),
            StringComparison.Ordinal);
        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">"
            + "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.3\" manifest:media-type=\"application/vnd.oasis.opendocument.spreadsheet\"/>"
            + "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/>"
            + "<manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>"
            + "</manifest:manifest>",
            Part(ods, "META-INF/manifest.xml"));
    }

    /// <summary>
    /// The default date, date-time and boolean cell styles point at data styles named N1, N2 and N3,
    /// declared beside them; LibreOffice shows a bare serial or 1/0 when a name does not resolve,
    /// which our reader, reading the values from their attributes, would never notice.
    /// </summary>
    [Fact]
    public async Task DeclaresTheDataStylesTheDefaultCellStylesNameForDatesAndBooleans()
    {
        string content = Content(await Spreadsheet(writer => writer.BeginSheet("data", [new("v")])));

        Assert.Contains("<number:date-style style:name=\"N1\"><number:year number:style=\"long\"/><number:text>-</number:text><number:month number:style=\"long\"/><number:text>-</number:text><number:day number:style=\"long\"/></number:date-style>", content, StringComparison.Ordinal);
        Assert.Contains("<number:date-style style:name=\"N2\"><number:year number:style=\"long\"/>", content, StringComparison.Ordinal);
        Assert.Contains("<number:seconds number:style=\"long\"/></number:date-style><number:boolean-style style:name=\"N3\"><number:boolean/></number:boolean-style>", content, StringComparison.Ordinal);
        Assert.Contains(
            "<style:style style:name=\"ce1\" style:family=\"table-cell\" style:data-style-name=\"N1\"/>"
            + "<style:style style:name=\"ce2\" style:family=\"table-cell\" style:data-style-name=\"N2\"/>"
            + "<style:style style:name=\"ce3\" style:family=\"table-cell\" style:data-style-name=\"N3\"/>",
            content,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("History")]
    [InlineData("a/b")]
    [InlineData("a\tb")]
    public async Task RefusesASheetNameExcelRefuses(string name)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), TabularFormat.Ods);

        Assert.Throws<ArgumentException>(() => writer.BeginSheet(name, [new("a")]));
    }

    [Fact]
    public async Task ASpreadsheetDisposedBeforeCompletingDoesNotOpen()
    {
        WriteTarget target = new();
        Random random = new(72);

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("v")]);

            for (int i = 0; i < 50_000; i++)
            {
                writer.BeginRow();
                writer.Write($"{random.NextInt64():x16} a value long enough that a flush writes part of the sheet");
                writer.EndRow();
            }

            await writer.FlushAsync(Token);
        }

        byte[] partial = target.ToArray();
        Assert.True(partial.Length > 64 * 1024, $"only {partial.Length} bytes went out");

        TabularFormatException refused = Assert.Throws<TabularFormatException>(() => new OdsCursor(new MemoryStream(partial, writable: false), cancellationToken: Token));
        Assert.Contains(refused.Code, new[] { ErrorCodes.Format.Corrupt, ErrorCodes.Format.Truncated, ErrorCodes.Format.Unsupported });
    }

    [Fact]
    public async Task TouchesTheTargetOnlyAsynchronously()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Ods))
        {
            writer.BeginSheet("data", [new("v")]);

            for (int i = 0; i < 100_000; i++)
            {
                writer.BeginRow();
                writer.Write("row value");
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        Assert.False(target.DisposedSynchronously);
        Assert.Equal(100_001, Rows(target.ToArray()).Count);
    }

    [Fact]
    public void RefusesACompressionLevelItDoesNotKnow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(
            new MemoryStream(),
            TabularFormat.Ods,
            new TabularWriterOptions { Ods = new OdsWriterOptions { CompressionLevel = (CompressionLevel)42 } }));
    }
}
