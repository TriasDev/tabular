using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// A quote that opens a field and swallows whole records is not syntax, even when another quote
/// closes it: the records inside are read as records (issue #20).
/// </summary>
/// <remarks>
/// A field that is a lone <c>"</c> — an inch mark, a ditto mark — opens a quoted field; the same
/// value in the same column of a later line closes it and is followed by the delimiter. RFC 4180
/// calls that one field spanning lines, so nothing was repaired and nothing counted, and the records
/// between the two quotes were joined without a word.
/// </remarks>
public sealed class StrayQuotePairTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static (List<string[]> Rows, CursorDiagnostics Diagnostics) Read(string text)
    {
        using CsvCursor cursor = new(new MemoryStream(Utf8NoBom.GetBytes(text), writable: false), "t.csv");
        List<string[]> rows = [];

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            rows.Add([.. cursor.CurrentRow.ToArray().Select(c => c.AsText() ?? string.Empty)]);
        }

        return (rows, cursor.Diagnostics);
    }

    [Fact]
    public void ReadsTheRecordsBetweenAPairOfLoneQuotesAsRecords()
    {
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(
            "id;name;size;city;note\n" +
            "1;pipe;\";Bonn;a\n" +
            "2;hose;\";Köln;b\n" +
            "3;tap;half;Ulm;c\n");

        Assert.Equal(4, rows.Count);
        Assert.Equal(["1", "pipe", "\"", "Bonn", "a"], rows[1]);
        Assert.Equal(["2", "hose", "\"", "Köln", "b"], rows[2]);
        Assert.Equal(["3", "tap", "half", "Ulm", "c"], rows[3]);
        Assert.True(diagnostics.RecoveredStrayQuotes > 0);
        Assert.False(diagnostics.IsClean);
    }

    [Fact]
    public void KeepsAGenuineMultiLineFieldAsOneValue()
    {
        // An address across two lines, holding no delimiter at all: a real multi-line value.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(
            "id;name;address;city;note\n" +
            "1;Acme;\"Hauptstraße 1\nHinterhaus\";Bonn;a\n" +
            "2;Beta;Ring 2;Köln;b\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal("Hauptstraße 1\nHinterhaus", rows[1][2]);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void KeepsAMultiLineFieldHoldingAFewDelimiters()
    {
        // A note that happens to contain two semicolons in a table of five columns is fewer than a
        // record's worth, and stays a note.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(
            "id;name;address;city;note\n" +
            "1;Acme;Ring 1;Bonn;\"first; second\nthird; fourth\"\n" +
            "2;Beta;Ring 2;Köln;b\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal("first; second\nthird; fourth", rows[1][4]);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void LeavesANarrowTablesMultiLineNoteAlone()
    {
        // Three columns make "a record's worth" only two delimiters, which a note reaches by
        // accident; in a table this narrow the repair is off rather than wrong.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(
            "id;name;note\n" +
            "1;Acme;\"first; second\nthird; fourth\"\n" +
            "2;Beta;b\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal("first; second\nthird; fourth", rows[1][2]);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void ReadsTheLinesAfterAQuoteThatTheFileEndsInside()
    {
        // The file ends inside a quoted field that spans lines. The text used to become that field's
        // value, joining every record after the quote into it; it is replayed as records instead. A
        // narrow table, so the early stray-quote check stays out of it and the end of the file is what
        // decides.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(
            "id;size;note\n" +
            "1;big;\"never closed\n" +
            "2;small;b\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "big", "\"never closed"], rows[1]);
        Assert.Equal(["2", "small", "b"], rows[2]);
        Assert.Equal(1, diagnostics.RecoveredUnterminatedQuotes);
    }

    [Fact]
    public void KeepsAMultiLineFieldWhoseFirstLineWouldOverfillTheRecordAsOneValue()
    {
        // A record's worth of delimiters and a line break, but read as a stray quote its line would
        // hold six fields in a table of five: the quote cannot have been stray, so the value stands.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(
            "a;b;c;d;e\n" +
            "1;2;3;\"x;y;q\nz;w;v\";5\n" +
            "6;7;8;9;10\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "2", "3", "x;y;q\nz;w;v", "5"], rows[1]);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void EndsAnUnclosedQuoteAtTheLineWhoseNextRecordHasTheRightWidth()
    {
        // The quote opened in row 1 is never closed; the inch mark in row 2 would close it and glue
        // the two records together. The repair goes back to the line end, reads row 2 as a record and
        // keeps its inch mark as text.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(
            "id;street;city\n1;\"Ring 1;Bonn\n2;135\"th St;Köln\n3;Main;Ulm\n");

        Assert.Equal(
            [["id", "street", "city"], ["1", "\"Ring 1", "Bonn"], ["2", "135\"th St", "Köln"], ["3", "Main", "Ulm"]],
            rows);
        Assert.Equal((1, 0), (diagnostics.RecoveredUnterminatedQuotes, diagnostics.RecoveredStrayQuotes));
    }

    [Fact]
    public void KeepsAnInchMarkInsideAMultiLineQuotedField()
    {
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read("a;b\n\"l1\nsize 5\"x\";2\n");

        Assert.Equal([["a", "b"], ["l1\nsize 5\"x", "2"]], rows);
        Assert.True(diagnostics.IsClean);
    }

    [Fact]
    public void ReplaysAnUnclosedQuoteAfterAnEarlierQuotedFieldWithoutInventingText()
    {
        // The replay starts at the open quote, not at the record's start: the closed "x" before it
        // is read once, and the open quote stays in the value as the character it was.
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read("id;a;note\n1;\"x\";\"open\n2;y;z\n");

        Assert.Equal([["id", "a", "note"], ["1", "x", "\"open"], ["2", "y", "z"]], rows);
        Assert.Equal(1, diagnostics.RecoveredUnterminatedQuotes);
    }

    [Theory]
    [InlineData("a;b\n1;\"open", 1)]
    [InlineData("a\n\"\"", 0)]
    public void ReadsAQuoteTheFileEndsInsideOrRightAfterAsOneLastRecord(string text, int repaired)
    {
        (List<string[]> rows, CursorDiagnostics diagnostics) = Read(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal(text.StartsWith("a;b", StringComparison.Ordinal) ? ["1", "open"] : [""], rows[1]);
        Assert.Equal(repaired, diagnostics.RecoveredUnterminatedQuotes);
    }
}
