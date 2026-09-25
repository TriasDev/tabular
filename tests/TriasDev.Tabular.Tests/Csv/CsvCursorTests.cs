using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// Pins how the csv cursor reads records, decides a dialect, and repairs input no specification
/// allows.
/// </summary>
public sealed class CsvCursorTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static List<string?[]> ReadAll(byte[] content, CsvCursorOptions? options = null)
    {
        using MemoryStream stream = new(content, writable: false);
        using CsvCursor cursor = new(stream, "test.csv", options);

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
    public void ReadsFieldsSeparatedByTheDetectedDelimiter()
    {
        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes("a;b;c\n1;2;3\n"));

        Assert.Equal(2, rows.Count);
        Assert.Equal(new string?[] { "a", "b", "c" }, rows[0]);
        Assert.Equal(new string?[] { "1", "2", "3" }, rows[1]);
    }

    [Fact]
    public void PrefersTheDelimiterThatDividesEveryLineEqually()
    {
        // Commas outnumber semicolons three to one on every line, because every number carries one.
        // Counting alone would pick the comma; only consistency of field count picks the semicolon.
        byte[] content = Utf8NoBom.GetBytes("ID;Betrag;Land\n1,00;1.234,56;DEU\n2,00;22693870,00;AUT\n");

        List<string?[]> rows = ReadAll(content);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new string?[] { "1,00", "1.234,56", "DEU" }, rows[1]);
    }

    [Fact]
    public void ReadsUtf8WithoutAByteOrderMark()
    {
        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes("Name\nMüller\n"));

        Assert.Equal("Müller", rows[1][0]);
    }

    [Fact]
    public void ReadsUtf8WithAByteOrderMarkWithoutLeakingItIntoTheFirstField()
    {
        byte[] content = [.. Encoding.UTF8.GetPreamble(), .. Utf8NoBom.GetBytes("Name\nMüller\n")];

        List<string?[]> rows = ReadAll(content);

        Assert.Equal("Name", rows[0][0]);
        Assert.Equal("Müller", rows[1][0]);
    }

    [Fact]
    public void FallsBackToWindows1252WhenTheBytesAreNotValidUtf8()
    {
        // 0xFC is ü in Windows-1252 and an invalid lead byte in UTF-8, so the file cannot be both.
        byte[] content = [.. "Name\nM"u8.ToArray(), 0xFC, .. "ller\n"u8.ToArray()];

        List<string?[]> rows = ReadAll(content);

        Assert.Equal("Müller", rows[1][0]);
    }

    [Fact]
    public void TreatsAQuotedFieldsLineBreakAsPartOfTheValue()
    {
        byte[] content = Utf8NoBom.GetBytes("Name;Note\r\nAcme;\"first\r\nsecond\"\r\n");

        List<string?[]> rows = ReadAll(content);

        Assert.Equal(2, rows.Count);
        Assert.Equal("first\r\nsecond", rows[1][1]);
    }

    [Fact]
    public void ReadsADoubledQuoteInsideAQuotedFieldAsOneQuote()
    {
        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes("a\n\"He said \"\"hi\"\"\"\n"));

        Assert.Equal("He said \"hi\"", rows[1][0]);
    }

    [Fact]
    public void TreatsAQuoteInsideAnUnquotedFieldAsAnOrdinaryCharacter()
    {
        // The shapes a real export carries: inch marks and letter names in street addresses. Treating
        // these as syntax makes the reader swallow every line up to the next quote.
        byte[] content = Utf8NoBom.GetBytes("ID;Street\n1;10 N. \"K\" St.\n2;100 21\"st AVE\n3;plain\n");

        List<string?[]> rows = ReadAll(content);

        Assert.Equal(4, rows.Count);
        Assert.Equal("10 N. \"K\" St.", rows[1][1]);
        Assert.Equal("100 21\"st AVE", rows[2][1]);
        Assert.Equal("plain", rows[3][1]);
    }

    [Fact]
    public void AppendsTextThatFollowsAClosingQuote()
    {
        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes("ID;Street\n1;\"C\" Road\n"));

        Assert.Equal("C Road", rows[1][1]);
    }

    [Fact]
    public void RecoversFromAQuoteThatIsNeverClosedInsteadOfConsumingTheFile()
    {
        // One stray quote in row 2. Unbounded, it eats every following row; bounded, it costs the
        // rows it spans and the file is read out.
        StringBuilder text = new("ID;Note\n1;\"unterminated\n");

        for (int i = 2; i <= 40; i++)
        {
            text.Append(i).Append(";row ").Append(i).Append('\n');
        }

        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes(text.ToString()));

        Assert.Equal("40", rows[^1][0]);
        Assert.True(rows.Count > 30, $"expected the file to be read out, got {rows.Count} rows");
    }

    [Fact]
    public void CountsEveryRecoveredUnterminatedQuote()
    {
        StringBuilder text = new("ID;Note\n1;\"unterminated\n");

        for (int i = 2; i <= 40; i++)
        {
            text.Append(i).Append(";row ").Append(i).Append('\n');
        }

        using MemoryStream stream = new(Utf8NoBom.GetBytes(text.ToString()), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            // Only the diagnostics are asserted, and they are complete once the file is read.
        }

        Assert.Equal(1, cursor.Diagnostics.RecoveredUnterminatedQuotes);
        Assert.False(cursor.Diagnostics.IsClean);
    }

    [Fact]
    public void ReportsARaggedRowAtItsOwnWidth()
    {
        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes("a;b;c\n1;2\n1;2;3;4\n"));

        Assert.Equal(3, rows[0].Length);
        Assert.Equal(2, rows[1].Length);
        Assert.Equal(4, rows[2].Length);
    }

    [Fact]
    public void NumbersRowsAsAPersonReadingTheFileWould()
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes("a\n1\n2\n"), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        List<int> numbers = [];

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            numbers.Add(cursor.CurrentRowNumber);
        }

        Assert.Equal([1, 2, 3], numbers);
    }

    [Fact]
    public void ReadsTheFinalRecordWhenTheFileDoesNotEndWithALineBreak()
    {
        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes("a;b\n1;2"));

        Assert.Equal(2, rows.Count);
        Assert.Equal(new string?[] { "1", "2" }, rows[1]);
    }

    [Fact]
    public void PresentsOneSheetNamedAfterTheFile()
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes("a\n"), writable: false);
        using CsvCursor cursor = new(stream, "export.csv");

        Assert.Equal(TabularFormat.Csv, cursor.Format);
        Assert.Single(cursor.Sheets);
        Assert.Equal("export.csv", cursor.Sheets[0].Name);
        Assert.True(cursor.MoveToSheet(0));
        Assert.False(cursor.MoveToSheet(1));
    }

    [Fact]
    public void HonoursADialectTheCallerDecided()
    {
        // The content would be detected as semicolon-delimited; the caller says otherwise and wins.
        CsvCursorOptions options = new()
        {
            Dialect = new CsvDialect
            {
                Encoding = Utf8NoBom,
                EncodingSource = DialectSource.Specified,
                Delimiter = ',',
                DelimiterSource = DialectSource.Specified,
                Quote = '"',
            },
        };

        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes("a;b,c\n"), options);

        Assert.Equal(new string?[] { "a;b", "c" }, rows[0]);
    }

    [Fact]
    public void TreatsAnEmptyFieldAndAQuotedEmptyFieldAlike()
    {
        List<string?[]> rows = ReadAll(Utf8NoBom.GetBytes("a;b\n;\"\"\n"));

        using MemoryStream stream = new(Utf8NoBom.GetBytes("a;b\n;\"\"\n"), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        cursor.ReadRow(TestContext.Current.CancellationToken);
        cursor.ReadRow(TestContext.Current.CancellationToken);

        // Both are absence. The syntax distinguishes them; the data does not, and a profile that
        // counted them apart would be reporting on punctuation rather than on content.
        Assert.Equal(RawCellKind.Empty, cursor.CurrentRow[0].Kind);
        Assert.Equal(RawCellKind.Empty, cursor.CurrentRow[1].Kind);
        Assert.Equal(2, rows[1].Length);
    }
}
