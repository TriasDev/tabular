using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>A read stops when its token is cancelled, inside a single row too, and a cursor stopped mid-row reads no further.</summary>
public sealed class ReadCancellationTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A workbook whose one cell points into a shared string table of the given size.</summary>
    private static byte[] SharedStringWorkbook(int entries, int cellIndex)
    {
        StringBuilder items = new(entries * 20);

        for (int i = 0; i < entries; i++)
        {
            items.Append("<si><t>s").Append(i).Append("</t></si>");
        }

        return new XlsxPackage()
            .WithSheet("Sheet1", $"""<row><c t="s"><v>{cellIndex}</v></c></row>""")
            .WithSharedStrings(items.ToString())
            .Build();
    }

    /// <summary>
    /// Empty elements whose only content is an attribute of random digits, so the sheet does not
    /// deflate to almost nothing.
    /// </summary>
    /// <remarks>
    /// The token is armed on compressed bytes. A run of identical elements compresses to a few
    /// hundred bytes, which the archive reads before the row begins — and whether that happens
    /// depended on the runtime's zlib: net8 read it all up front and these tests never saw a
    /// cancellation. Random attribute values keep the compressed part large, so it is still being
    /// read while the loop runs, on any runtime. Seeded, so every run builds the same file.
    /// </remarks>
    private static string Incompressible(string element, int count)
    {
        Random random = new(20260925);
        StringBuilder xml = new(count * 24);

        for (int i = 0; i < count; i++)
        {
            xml.Append('<').Append(element).Append(" q=\"").Append(random.NextInt64()).Append("\"/>");
        }

        return xml.ToString();
    }

    // -- A read can be stopped ------------------------------------------------------------------

    [Fact]
    public void RefusesToStartAReadWithATokenAlreadyCancelled()
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes("a;b\n1;2\n"), writable: false);
        using CsvCursor cursor = new(stream, "t.csv");
        using CancellationTokenSource source = new();

        source.Cancel();

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
    }

    [Fact]
    public void StopsInTheMiddleOfReadingALongCsvRow()
    {
        // Cancelled from inside the stream, once the read is already under way. Cancelling before the
        // call would prove only that the check at the top of the method exists, which is not the part
        // that matters: the whole point is to interrupt a call that has been running for a while.
        string row = string.Join(';', Enumerable.Range(0, 40_000).Select(i => "0123456789"));

        using CancellationTokenSource source = new();
        using MemoryStream inner = new(Utf8NoBom.GetBytes(row), writable: false);
        using CancellingStream stream = new(inner, source);
        using CsvCursor cursor = new(stream, "long.csv", new CsvCursorOptions { MaxColumns = 100_000 });

        stream.CancelAfter(4_096);

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
    }

    [Fact]
    public void StopsInTheMiddleOfLoadingASharedStringTable()
    {
        // The longest thing the cursor ever does, and it happens inside a row read rather than at
        // open time — so this is the case a token checked only between rows would never reach. The
        // stream is armed after construction, so the cancellation lands during the table's load.
        using CancellationTokenSource source = new();
        using MemoryStream inner = new(SharedStringWorkbook(entries: 200_000, cellIndex: 0), writable: false);
        using CancellingStream stream = new(inner, source);
        using XlsxCursor cursor = new(stream);

        stream.CancelAfter(4_096);

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
    }

    [Fact]
    public void ReadsToTheEndWhenNothingCancels()
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes("a;b\n1;2\n"), writable: false);
        using CsvCursor cursor = new(stream, "t.csv");
        using CancellationTokenSource source = new();

        Assert.True(cursor.ReadRow(source.Token));
        Assert.True(cursor.ReadRow(source.Token));
        Assert.False(cursor.ReadRow(source.Token));
    }

    // -- A cancelled read must not leave a usable-looking cursor ---------------------------------

    [Fact]
    public void RefusesToReadOnAfterARowWasCancelledPartWayThrough()
    {
        // The characters the row already consumed are gone from the stream. Reading on presented the
        // remainder as a complete record, correctly numbered and quietly missing its beginning —
        // measured, 65,535 characters silently discarded and the next row looking perfectly ordinary.
        string row = string.Join(';', Enumerable.Range(0, 40_000).Select(i => "0123456789"));

        using CancellationTokenSource source = new();
        using MemoryStream inner = new(Utf8NoBom.GetBytes($"a\n{row};TAIL\nb\n"), writable: false);
        using CancellingStream stream = new(inner, source);
        using CsvCursor cursor = new(stream, "long.csv", new CsvCursorOptions { MaxColumns = 100_000 });

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));          // the header, before anything is armed
        stream.CancelAfter(4_096);

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));

        // And now the important half: it refuses rather than inventing a row.
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.Contains("failed part-way", failure.Message);
    }

    [Fact]
    public void KeepsReadingWhenNoReadWasEverCancelled()
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes("a\n1\n2\n"), writable: false);
        using CsvCursor cursor = new(stream, "t.csv");

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.False(cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    // -- The token has to reach the loop, not the doorway -----------------------------------------

    [Fact]
    public void StopsInsideARowFullOfElementsItWantsNothingFrom()
    {
        // The outer stride stops advancing the moment a row is entered, so a row of four hundred
        // million elements this reader ignores ran for five seconds with nothing able to stop it.
        string noise = Incompressible("z", 20_000);

        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", $"<row>{noise}<c t=\"inlineStr\"><is><t>a</t></is></c></row>")
            .Build();

        using CancellationTokenSource source = new();
        using MemoryStream inner = new(package, writable: false);
        using CancellingStream stream = new(inner, source);
        using XlsxCursor cursor = new(stream);

        stream.CancelAfter(512);

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
    }

    [Fact]
    public void StopsInsideAValueAssembledFromCountlessEmptyRuns()
    {
        // The value ceiling bounds text, and this loop can run without producing any: an element
        // carrying nothing costs nothing to write and no budget measures it. The token was being
        // dropped at the call site.
        string runs = Incompressible("t", 20_000);

        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", $"<row><c t=\"inlineStr\"><is>{runs}</is></c></row>")
            .Build();

        using CancellationTokenSource source = new();
        using MemoryStream inner = new(package, writable: false);
        using CancellingStream stream = new(inner, source);
        using XlsxCursor cursor = new(stream);

        stream.CancelAfter(512);

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
    }

    [Fact]
    public void PoisonsTheCursorWhenAnythingThrowsMidRow()
    {
        // No cancellation involved, and that is the point: the flag used to be set at the cancellation
        // sites only, so an over-long field threw from the middle of a record and the next read handed
        // back the remainder as a whole row — correctly numbered, silently missing its beginning, and
        // InvalidDataException is exactly the exception a caller catches and continues past.
        using MemoryStream stream = new(
            Utf8NoBom.GetBytes("a;b\nAAAAAAAAAAAAAAA;keep\nSECOND;ROW\n"),
            writable: false);

        using CsvCursor cursor = new(stream, "t.csv", new CsvCursorOptions { MaxFieldChars = 10 });

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.Contains("cannot continue", failure.Message);
    }

    [Fact]
    public void PoisonsTheCursorWhenAValueCeilingThrowsMidRow()
    {
        string runs = string.Concat(Enumerable.Repeat("<r><t>abcdefghij</t></r>", 100));

        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", $"<row><c t=\"inlineStr\"><is>{runs}</is></c></row><row><c t=\"inlineStr\"><is><t>b</t></is></c></row>")
            .Build();

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(stream, new XlsxCursorOptions { MaxValueChars = 100 });

        Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }
}
