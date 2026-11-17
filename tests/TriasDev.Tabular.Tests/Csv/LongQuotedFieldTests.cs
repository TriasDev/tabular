using System.Globalization;
using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// A genuine quoted field of many lines is one value; a stray quote is still caught — by what it
/// swallows rather than by how many lines it spans (issue #10).
/// </summary>
public sealed class LongQuotedFieldTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static (List<string[]> Rows, CursorDiagnostics Diagnostics) Read(string text, CsvCursorOptions? options = null)
    {
        using CsvCursor cursor = new(new MemoryStream(Utf8NoBom.GetBytes(text), writable: false), "t.csv", options);
        List<string[]> rows = [];

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            rows.Add([.. cursor.CurrentRow.ToArray().Select(c => c.AsText() ?? string.Empty)]);
        }

        return (rows, cursor.Diagnostics);
    }

    [Fact]
    public void ReadsAQuotedNoteOfSixLinesAsOneValue()
    {
        // The case from #10: Sylvan, Sep and CsvHelper read it as one field; a four-line bound split it
        // into six rows.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read("id,note\n1,\"l1\nl2\nl3\nl4\nl5\nl6\"\n2,x\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "l1\nl2\nl3\nl4\nl5\nl6"], rows[1]);
        Assert.Equal(["2", "x"], rows[2]);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void DoesNotCloseARunawayQuoteOnAnInchMark()
    {
        // Past a line ending, a quote closes the field only where a field could end. "135"th" is an
        // inch mark inside the text the stray quote swallowed, not the end of a field.
        (List<string[]> rows, _) = Read(
            "id;name;street;city;note\n" +
            "1;Acme;\"Ring 1;Bonn;a\n" +
            "2;Beta;135\"th St;Köln;b\n" +
            "3;Gamma;Main;Ulm;c\n");

        Assert.Equal(4, rows.Count);
        Assert.Equal(["1", "Acme", "\"Ring 1", "Bonn", "a"], rows[1]);
        Assert.Equal(["2", "Beta", "135\"th St", "Köln", "b"], rows[2]);
        Assert.Equal(["3", "Gamma", "Main", "Ulm", "c"], rows[3]);
    }

    [Fact]
    public void CatchesAStrayQuoteInAWideTableByWhatItSwallows()
    {
        // With the line bound far away, a quote never closed would swallow a hundred lines before it
        // tripped. It is caught as soon as it has swallowed a record's worth of delimiters instead.
        StringBuilder text = new("id;name;street;city;note\n1;Acme;\"Ring 1;Bonn;a\n");

        for (int i = 2; i <= 40; i++)
        {
            text.Append(i).Append(";n").Append(i).Append(";s;c;x\n");
        }

        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(text.ToString());

        Assert.Equal(41, rows.Count);
        Assert.Equal("\"Ring 1", rows[1][2]);
        Assert.All(rows, r => Assert.Equal(5, r.Length));
        Assert.Equal(1, diagnostics.RecoveredStrayQuotes);
    }

    [Fact]
    public void StillBoundsAStrayQuoteInANarrowTable()
    {
        // Two columns are too few to judge by delimiters, so the line bound is what recovers it.
        StringBuilder text = new("id;note\n1;\"never closed\n");

        for (int i = 2; i <= 150; i++)
        {
            text.Append(i).Append(";x\n");
        }

        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(text.ToString());

        Assert.Equal(151, rows.Count);
        Assert.Equal("\"never closed", rows[1][1]);
        Assert.Equal(1, diagnostics.RecoveredUnterminatedQuotes);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReplaysAStrayQuoteTrippedAtTheLineBoundWithoutInventingACharacter(string lineEnding)
    {
        // A lone carriage return ends a line only when the character after it is not a line feed, so
        // the reader has looked one character ahead when the bound trips there. That character goes
        // back behind the replayed text; the slot it was held in must be left empty, not hold a
        // character of its own.
        StringBuilder text = new($"id;note{lineEnding}1;\"never closed{lineEnding}");

        for (int i = 2; i <= 150; i++)
        {
            text.Append(i).Append(";x").Append(lineEnding);
        }

        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(text.ToString());

        Assert.Equal(151, rows.Count);
        Assert.Equal(["1", "\"never closed"], rows[1]);

        for (int i = 2; i <= 150; i++)
        {
            Assert.Equal([i.ToString(CultureInfo.InvariantCulture), "x"], rows[i]);
        }

        Assert.Equal(1, diagnostics.RecoveredUnterminatedQuotes);
    }

    [Fact]
    public void KeepsAQuotedFieldOfExactlyTheBoundsLinesAsOneValue()
    {
        CsvCursorOptions options = new() { MaxQuotedFieldLines = 2 };

        (List<string[]> within, CursorDiagnostics clean) = Read("a;b\n1;\"l1\nl2\nl3\";2\n", options);
        (List<string[]> over, CursorDiagnostics repaired) = Read("a;b\n1;\"l1\nl2\nl3\nl4\";2\n", options);

        Assert.Equal([["a", "b"], ["1", "l1\nl2\nl3", "2"]], within);
        Assert.True(clean.IsClean);
        Assert.Equal([["a", "b"], ["1", "\"l1"], ["l2"], ["l3"], ["l4\"", "2"]], over);
        Assert.Equal(1, repaired.RecoveredUnterminatedQuotes);
    }

    [Fact]
    public void BoundsAQuotedFieldAtAHundredLinesByDefault()
    {
        Assert.Equal(100, CsvCursorOptions.Default.MaxQuotedFieldLines);
    }
}
