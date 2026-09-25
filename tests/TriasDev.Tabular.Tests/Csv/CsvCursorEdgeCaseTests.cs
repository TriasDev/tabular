using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// Edge cases drawn from what the established readers test.
/// </summary>
/// <remarks>
/// The cases come from reading the test suites of Sylvan.Data.Csv, CsvHelper and Sep — what to test
/// is knowledge, not property, and the tests themselves are written here rather than copied. Several
/// of these describe files that no specification sanctions and that producers emit anyway, which is
/// the whole reason for looking at what other readers had to learn the hard way.
/// </remarks>
public sealed class CsvCursorEdgeCaseTests
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

    private static List<string?[]> ReadAll(string text) => ReadAll(Utf8NoBom.GetBytes(text));

    [Fact]
    public void TreatsALoneCarriageReturnAsALineEnding()
    {
        // Classic Mac line endings. Still produced by older exporters, and a reader that only breaks
        // on a line feed reads the entire file as one row without complaining.
        List<string?[]> rows = ReadAll("a;b\r1;2\r3;4\r");

        Assert.Equal(3, rows.Count);
        Assert.Equal(new string?[] { "1", "2" }, rows[1]);
    }

    [Fact]
    public void DoesNotSplitTwiceOnACarriageReturnLineFeedPair()
    {
        List<string?[]> rows = ReadAll("a;b\r\n1;2\r\n");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ReadsAFileWhoseLineEndingsAreMixed()
    {
        List<string?[]> rows = ReadAll("a;b\n1;2\r\n3;4\r5;6");

        Assert.Equal(4, rows.Count);
        Assert.Equal(new string?[] { "5", "6" }, rows[3]);
    }

    [Fact]
    public void ReadsAFileEndingInACarriageReturn()
    {
        List<string?[]> rows = ReadAll("a;b\r\n1;2\r");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new string?[] { "1", "2" }, rows[1]);
    }

    [Fact]
    public void ReadsAFileThatIsOnlyAHeader()
    {
        List<string?[]> rows = ReadAll("a;b;c\n");

        Assert.Equal(new string?[] { "a", "b", "c" }, Assert.Single(rows));
    }

    [Fact]
    public void ReadsAnEmptyFileAsNoRows()
    {
        Assert.Empty(ReadAll(string.Empty));
    }

    [Fact]
    public void ReadsAFileThatHoldsOnlyAByteOrderMarkAsNoRows()
    {
        Assert.Empty(ReadAll(Encoding.UTF8.GetPreamble()));
    }

    [Fact]
    public void ReadsTextAfterAClosingQuoteAsPartOfTheValue()
    {
        // Text after a closing quote is content, not a fault.
        List<string?[]> rows = ReadAll("a\n\"x\" y\n");

        Assert.Equal("x y", rows[1][0]);
    }

    [Fact]
    public void TrimsAValueEvenWhenQuotingSaidToKeepIt()
    {
        // Quoting is how an exporter says "this whitespace is data", and the library no longer
        // honours that: every value is trimmed as it is read, so that what was profiled and what is
        // imported are one string rather than two that usually agree. Pinned because it is a real
        // cost of that decision — a code padded to a fixed width loses its padding — and a cost paid
        // deliberately should be visible in a test rather than discovered in a file.
        List<string?[]> rows = ReadAll("a\n\"0012  \"\n");

        Assert.Equal("0012", rows[1][0]);
    }

    [Fact]
    public void TreatsAQuoteThatFollowsASpaceAsAnOrdinaryCharacter()
    {
        // The quote no longer stands where the field begins, so it is content rather than syntax —
        // and the value keeps its quotes, which is the point. The leading space is trimmed away as
        // every value now is, so the value is judged by what it says rather than by how it was typed.
        List<string?[]> rows = ReadAll("a\n \"x\"\n");

        Assert.Equal("\"x\"", rows[1][0]);
    }

    [Fact]
    public void ReadsAQuotedFieldThatEndsTheFileWithoutALineBreak()
    {
        List<string?[]> rows = ReadAll("a;b\n1;\"last\"");

        Assert.Equal(new string?[] { "1", "last" }, rows[1]);
    }

    [Theory]
    [InlineData("a,b,c\n1,2,3\n", ',')]
    [InlineData("a\tb\tc\n1\t2\t3\n", '\t')]
    [InlineData("a|b|c\n1|2|3\n", '|')]
    [InlineData("a;b;c\n1;2;3\n", ';')]
    public void DetectsEachSupportedDelimiter(string text, char expected)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(text), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        Assert.Equal(expected, cursor.Dialect.Delimiter);
    }

    [Fact]
    public void PrefersTheDelimiterPresentOnEveryLineOverTheMoreFrequentOne()
    {
        // The case a German export creates: commas outnumber semicolons because every number holds
        // one, and only the semicolon divides every line the same way.
        using MemoryStream stream = new(
            Utf8NoBom.GetBytes("a;b\n1,10;2,20\n3,30;4,40\n"),
            writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        Assert.Equal(';', cursor.Dialect.Delimiter);
    }

    [Fact]
    public void ReadsASingleColumnFileAsOneFieldPerRow()
    {
        List<string?[]> rows = ReadAll("name\nMüller\nWeiß\n");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Single(row));
    }

    [Fact]
    public void DoesNotTakeADelimiterFromInsideQuotesWhenDetecting()
    {
        using MemoryStream stream = new(
            Utf8NoBom.GetBytes("a;b\n\"x,y,z,w\";2\n\"p,q,r,s\";4\n"),
            writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        Assert.Equal(';', cursor.Dialect.Delimiter);
    }

    [Fact]
    public void ReadsARecordThatStraddlesTheReadBuffer()
    {
        // The reader fills in 64 KB blocks. A quote, an escaped quote or a line ending falling on the
        // boundary is where a hand-written state machine breaks, and no small fixture reaches it.
        StringBuilder text = new("a;b\r\n");

        for (int i = 0; i < 20_000; i++)
        {
            text.Append('"').Append("value ").Append(i).Append(" with \"\"quotes\"\" inside").Append("\";")
                .Append(i).Append("\r\n");
        }

        List<string?[]> rows = ReadAll(text.ToString());

        Assert.Equal(20_001, rows.Count);
        Assert.Equal("value 19999 with \"quotes\" inside", rows[^1][0]);
    }

    [Fact]
    public void ReadsAFieldLongerThanTheReadBuffer()
    {
        string long_ = new('x', 200_000);

        List<string?[]> rows = ReadAll($"a;b\n\"{long_}\";2\n");

        Assert.Equal(long_, rows[1][0]);
        Assert.Equal("2", rows[1][1]);
    }
}
