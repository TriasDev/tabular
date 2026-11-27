using System.Formats.Tar;
using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// <see cref="CsvQuoteMode.Strict"/> reads quotes as RFC 4180 does and refuses what the default mode
/// repairs (#198).
/// </summary>
public sealed class StrictQuoteTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A spreadsheet's export of a cell holding a line break and four commas, in a table of five columns.</summary>
    private const string ExcelExport =
        "ID,Name,Description,Owner,Department\r\n" +
        "1,Payroll,\"Monthly salary run.\nUses names, addresses, bank accounts, tax ids, and social security numbers.\",HR,People\r\n" +
        "2,Recruiting,Applicant tracking,HR,People\r\n";

    private static CsvCursor Open(string text, CsvCursorOptions options) =>
        new(new MemoryStream(Utf8NoBom.GetBytes(text), writable: false), "t.csv", options);

    private static (List<string[]> Rows, CursorDiagnostics Diagnostics) Read(string text, CsvCursorOptions? options = null)
    {
        using CsvCursor cursor = Open(text, options ?? new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict });
        List<string[]> rows = [];

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            rows.Add([.. cursor.CurrentRow.ToArray().Select(c => c.AsText() ?? string.Empty)]);
        }

        return (rows, cursor.Diagnostics);
    }

    private static TabularFormatException Refused(string text, CsvCursorOptions? options = null)
    {
        using CsvCursor cursor = Open(text, options ?? new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict });

        TabularFormatException error = Assert.Throws<TabularFormatException>(() =>
        {
            while (cursor.ReadRow(TestContext.Current.CancellationToken))
            {
                _ = cursor.CurrentRow.Length;
            }
        });

        Assert.Equal(TabularFormatException.Corrupt, error.Code);
        Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
        return error;
    }

    [Fact]
    public void ReadsAnExcelCellWithALineBreakAndCommasAsOneValue()
    {
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(ExcelExport);

        Assert.Equal(3, rows.Count);
        Assert.Equal(
            ["1", "Payroll", "Monthly salary run.\nUses names, addresses, bank accounts, tax ids, and social security numbers.", "HR", "People"],
            rows[1]);
        Assert.Equal(["2", "Recruiting", "Applicant tracking", "HR", "People"], rows[2]);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void RepairModeStillSplitsTheExcelCell()
    {
        // Pins that the default did not move: the repair is what strict mode exists to avoid.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(ExcelExport, CsvCursorOptions.Default);

        Assert.Equal(4, rows.Count);
        Assert.True(diagnostics.RecoveredStrayQuotes > 0);
    }

    [Fact]
    public void KeepsAQuotedFieldOfMoreThanTheLineBoundAsOneValue()
    {
        string note = string.Join('\n', Enumerable.Range(1, 151).Select(i => $"line {i}"));

        (List<string[]> rows, CursorDiagnostics diagnostics) = Read($"id,note\n1,\"{note}\"\n2,b\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(note, rows[1][1]);
        Assert.Equal(["2", "b"], rows[2]);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void RefusesAQuoteTheInputEndsInside()
    {
        TabularFormatException error = Refused("a,b\n1,\"open\nmore");

        Assert.Contains("row 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAQuoteTheInputEndsInsideOnItsFirstLine()
    {
        Refused("a,b\n1,\"open");
    }

    [Theory]
    [InlineData("a,b\n\"C\" Road,x\n")]
    [InlineData("a,b\n\"C\"Road,x\n")]
    [InlineData("a,b\n\"note\n135\"th\",x\n")]
    [InlineData("a,b\n1,\"x\" \"y\"\n")]
    public void RefusesTextAfterAClosingQuote(string text)
    {
        TabularFormatException error = Refused(text);

        Assert.Contains("row 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoresWhitespaceAroundAQuotedField()
    {
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read("ID, Name, Note\r\n1, \"Payroll\", \"monthly, HR\"  \r\n");

        Assert.Equal(["1", "Payroll", "monthly, HR"], rows[1]);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void KeepsAQuoteInsideAnUnquotedField()
    {
        (List<string[]> rows, _) = Read("a,b\n21\"st AVE,x\nsize 5 \"big\",y\n");

        Assert.Equal(["21\"st AVE", "x"], rows[1]);
        Assert.Equal(["size 5 \"big\"", "y"], rows[2]);
    }

    [Theory]
    [InlineData("a\n\"x\"\"\"", "x\"")]
    [InlineData("a\n\"x\"\"\"\n", "x\"")]
    [InlineData("a\n\"\"\"\"\n", "\"")]
    [InlineData("a\n\"x\"  \r", "x")]
    [InlineData("a\n\"x\"  \rb\r", "x")]
    [InlineData("a\n\"x\"  ", "x")]
    [InlineData("a\n\"x\" \t \r\n", "x")]
    public void ClosesAQuotedFieldBeforeWhitespaceAndTheEnd(string text, string value)
    {
        (List<string[]> rows, _) = Read(text);

        Assert.Equal([value], rows[1]);
    }

    [Fact]
    public void TakesATabDelimiterAsADelimiterNotAsWhitespace()
    {
        (List<string[]> rows, _) = Read(
            "a\tb\tc\n1\t \"x\ty\"\t\"z\"\n",
            new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict, Delimiter = '\t' });

        Assert.Equal(["1", "x\ty", "z"], rows[1]);
    }

    [Fact]
    public void HonoursAStatedQuoteCharacter()
    {
        CsvCursorOptions options = new() { QuoteMode = CsvQuoteMode.Strict, Quote = '\'', Delimiter = ',' };

        (List<string[]> rows, _) = Read("a,b\n 'x, y'  ,z\n'it''s',\"q\"\n", options);

        Assert.Equal(["x, y", "z"], rows[1]);
        Assert.Equal(["it's", "\"q\""], rows[2]);
        Refused("a,b\n'C' Road,x\n", options);
    }

    [Fact]
    public void ReadsQuotesAfterLeadingBlanksInLinearTime()
    {
        // Each quote inside a field asks whether the field so far is only whitespace. Asked from the
        // field's start, that cost the length of its leading blanks per quote: sixty thousand blanks
        // and as many quotes took twenty-four seconds, eighty thousand thirty-seven, and cancellation,
        // checked per character, could not reach it. Linear, it is milliseconds, so the ceiling sits a
        // thousand times above it and far below the defect.
        const int n = 60_000;
        string text = "a,b\n" + new string(' ', n) + "x" + new string('"', n) + ",y\n";
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        (List<string[]> rows, _) = Read(text);

        watch.Stop();
        Assert.Equal("x" + new string('"', n), rows[1][0]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Fact]
    public void NamesTheRowAndTheOpenQuoteWhenAStrayQuoteReachesTheFieldLimit()
    {
        // A quote that is never closed reads on to the field limit. A small file ends first and is
        // refused as quoting; a large one meets the limit, and the message has to say where and why.
        CsvCursorOptions options = new() { QuoteMode = CsvQuoteMode.Strict, Delimiter = ',', MaxFieldChars = 100 };
        string text = "a,b\n1,\"oops,3\n" + string.Concat(Enumerable.Repeat("x,y\n", 50));
        using CsvCursor cursor = Open(text, options);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        TabularLimitException error = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.Contains("row 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("never closed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CountsTheBlanksBeforeAQuoteTowardsTheFieldLimit()
    {
        // They are held until the quote shows they are not content, so they count — as documented.
        CsvCursorOptions options = new() { QuoteMode = CsvQuoteMode.Strict, Delimiter = ',', MaxFieldChars = 8 };
        using CsvCursor cursor = Open("a\n" + new string(' ', 10) + "\"x\"\n", options);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DropsAnyWhitespaceAroundAQuotedFieldNotOnlySpaces()
    {
        // A no-break space is what a cell pasted from a spreadsheet or a web page often starts with,
        // and the value is trimmed of it anyway.
        (List<string[]> rows, _) = Read(
            "a,b\n \"x,y\" ,z\n",
            new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict, Delimiter = ',' });

        Assert.Equal(["x,y", "z"], rows[1]);
    }

    [Fact]
    public void CancellationReachesARunOfBlanksAfterAClosingQuote()
    {
        using CancellationTokenSource source = new();
        byte[] text = Utf8NoBom.GetBytes("a\n\"x\"" + new string(' ', 3_000_000) + "\nb\n");
        using CancellingStream stream = new(text, source);
        using CsvCursor cursor = new(stream, "t.csv", new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict, Delimiter = ',' });

        Assert.True(cursor.ReadRow(source.Token));
        stream.CancelAfter(1);

        Assert.ThrowsAny<OperationCanceledException>(() => cursor.ReadRow(source.Token));
    }

    [Theory]
    [InlineData(65_531)]
    [InlineData(65_532)]
    [InlineData(65_533)]
    public void RefusesTextAfterAClosingQuoteOnTheReadBufferEdge(int pad)
    {
        // "a\n" and the opening quote take three characters, so the closing quote lands on either side
        // of the first 64 K buffer's last character.
        TabularFormatException error = Refused(
            "a\n\"" + new string('p', pad) + "\"Road\n",
            new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict, Delimiter = ',' });

        Assert.Contains("row 2", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(65_532)]
    [InlineData(65_533)]
    [InlineData(65_534)]
    [InlineData(65_535)]
    public void OpensAQuoteAfterBlanksThatCrossTheReadBufferEdge(int blanks)
    {
        (List<string[]> rows, _) = Read(
            "a\n" + new string(' ', blanks) + "\"x,y\"  \n",
            new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict, Delimiter = ',' });

        Assert.Equal(["x,y"], rows[1]);
    }

    [Fact]
    public void ReadsACsvInsideAZipStrictlyAndNamesTheEntryItRefuses()
    {
        byte[] zip = new ZipArchiveBuilder()
            .With("ok.csv", ExcelExport)
            .With("data/bad.csv", "a,b\n\"C\" Road,x\n")
            .Build();

        AssertReadsTheGoodEntryAndNamesTheBadOne(zip, "t.zip");
    }

    [Fact]
    public void ReadsACsvInsideATarStrictlyAndNamesTheEntryItRefuses()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Pax, ("ok.csv", ExcelExport), ("data/bad.csv", "a,b\n\"C\" Road,x\n"));

        AssertReadsTheGoodEntryAndNamesTheBadOne(tar, "t.tar");
    }

    private static void AssertReadsTheGoodEntryAndNamesTheBadOne(byte[] archive, string name)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        TabularOpenOptions options = new() { Csv = new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict } };
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(archive, writable: false), name, options, token);
        int good = cursor.Sheets.ToList().FindIndex(sheet => sheet.Source == "ok.csv");
        int bad = cursor.Sheets.ToList().FindIndex(sheet => sheet.Source == "data/bad.csv");

        Assert.True(cursor.MoveToSheet(good, token));
        int rows = 0;

        while (cursor.ReadRow(token))
        {
            rows++;
        }

        Assert.Equal(3, rows);
        Assert.True(cursor.Diagnostics.IsClean);

        Assert.True(cursor.MoveToSheet(bad, token));
        Assert.True(cursor.ReadRow(token));
        TabularFormatException error = Assert.Throws<TabularFormatException>(() => cursor.ReadRow(token));

        Assert.Equal(TabularFormatException.Corrupt, error.Code);
        Assert.Contains("data/bad.csv", error.Message, StringComparison.Ordinal);
        Assert.Contains("row 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsACsvInsideGzipStrictlyToo()
    {
        MemoryStream compressed = new();

        using (System.IO.Compression.GZipStream gzip = new(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(Utf8NoBom.GetBytes(ExcelExport));
        }

        compressed.Position = 0;
        TabularOpenOptions options = new() { Csv = new CsvCursorOptions { QuoteMode = CsvQuoteMode.Strict } };
        using ITabularCursor cursor = TabularFile.Open(compressed, "t.csv.gz", options, TestContext.Current.CancellationToken);
        int rows = 0;

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            rows++;
        }

        Assert.Equal(3, rows);
        Assert.True(cursor.Diagnostics.IsClean);
    }

    [Fact]
    public void HoldsAQuotedFieldToTheFieldLimit()
    {
        CsvCursorOptions options = new() { QuoteMode = CsvQuoteMode.Strict, MaxFieldChars = 8 };

        Assert.Equal(["12345678"], Read("a\n\"12345678\"\n", options).Rows[1]);

        using CsvCursor cursor = Open("a\n\"123456789\"\n", options);
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }
}
