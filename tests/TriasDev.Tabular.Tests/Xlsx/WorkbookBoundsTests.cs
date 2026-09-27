using System.Text;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>Every structure a workbook describes is bounded, and a workbook inside the bounds reads.</summary>
public sealed class WorkbookBoundsTests
{

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

    private static XlsxCursor Workbook(int sheets, XlsxCursorOptions options)
    {
        XlsxPackage package = new();

        for (int i = 0; i < sheets; i++)
        {
            package = package.WithSheet($"S{i}", """<row><c t="inlineStr"><is><t>a</t></is></c></row>""");
        }

        MemoryStream stream = new(package.Build(), writable: false);

        return new XlsxCursor(stream, options);
    }

    // -- A budget has to count what the heap pays for -------------------------------------------

    [Fact]
    public void RefusesASharedStringTableLargerThanAnyRealWorkbook()
    {
        // Measured before the ceiling: a 41 KB workbook holding a million shared strings and a
        // single-cell sheet cost 63 MB of heap — about fifteen hundred to one. The byte budget did
        // not see it, because seventeen bytes on the wire is forty in the heap.
        using MemoryStream stream = new(SharedStringWorkbook(entries: 40, cellIndex: 0), writable: false);
        using XlsxCursor cursor = new(stream, new XlsxCursorOptions { MaxSharedStrings = 20 });

        TabularLimitException failure = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.Contains("20", failure.Message);
    }

    [Fact]
    public void RefusesOneCellAssembledFromCountlessSmallRuns()
    {
        // The scanner bounds a token; this bounds the value the tokens build. Measured before the
        // ceiling: a 307 KB workbook produced a single cell of eighty million characters, with no
        // token anywhere near the scanner's limit.
        string runs = string.Concat(Enumerable.Repeat("<r><t>abcdefghij</t></r>", 100));

        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", $"""<row><c t="inlineStr"><is>{runs}</is></c></row>""")
            .Build();

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(stream, new XlsxCursorOptions { MaxValueChars = 100 });

        Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void StillReadsAWorkbookInsideTheCeilings()
    {
        // A bound nobody can reach from below is a bound that refuses ordinary files.
        using MemoryStream stream = new(SharedStringWorkbook(entries: 40, cellIndex: 7), writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("s7", cursor.CurrentRow[0].AsText());
    }

    // -- A ceiling has to count what the memory is spent on ---------------------------------------

    [Fact]
    public void RefusesASharedStringTableTooLargeToHoldEvenWithinItsEntryCount()
    {
        // The entry ceiling bounds the wrong dimension, which was the mistake it was added to fix,
        // one storey up: a million entries of two thousand characters each satisfies it and costs
        // 3.9 GB — measured, from a workbook of three megabytes. What a table costs is its characters.
        byte[] package = LongSharedStringWorkbook(entries: 10, charsEach: 100);

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(
            stream,
            new XlsxCursorOptions { MaxSharedStrings = 1_000, MaxSharedStringChars = 500 });

        TabularLimitException failure = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.Contains("500", failure.Message);
        Assert.Contains("characters", failure.Message);
    }

    [Fact]
    public void RefusesAPackageOfMorePartsThanAnyWorkbookHas()
    {
        // Every entry's metadata is materialised to find parts by name, before any budget can be
        // consulted: six hundred thousand empty entries in a 52 MB upload retained 405 MB.
        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c t="inlineStr"><is><t>a</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(package, writable: false);

        Assert.Throws<TabularLimitException>(
            () => new XlsxCursor(stream, new XlsxCursorOptions { MaxPackageEntries = 2 }));
    }

    [Fact]
    public void RefusesAStyleTableLargerThanTheFormatAllows()
    {
        // Read in the constructor, before a caller has anything to cancel with.
        string formats = string.Concat(Enumerable.Repeat("""<xf numFmtId="0"/>""", 20));

        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c t="inlineStr"><is><t>a</t></is></c></row>""")
            .WithStyles($"<cellXfs count=\"20\">{formats}</cellXfs>")
            .Build();

        using MemoryStream stream = new(package, writable: false);

        Assert.Throws<TabularLimitException>(
            () => new XlsxCursor(stream, new XlsxCursorOptions { MaxCellFormats = 5 }));
    }

    [Fact]
    public void RefusesOneSharedStringLongerThanAValueMayBe()
    {
        // The inline path had a test and this one did not, though the option's own summary claims
        // both. Deleting the check left the suite green.
        StringBuilder items = new();
        items.Append("<si><t>").Append('x', 500).Append("</t></si>");

        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c t="s"><v>0</v></c></row>""")
            .WithSharedStrings(items.ToString())
            .Build();

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(stream, new XlsxCursorOptions { MaxValueChars = 100 });

        Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    // -- The sweep: every growth, every loop, every mid-row throw -------------------------------

    [Fact]
    public void RefusesAWorkbookDeclaringMoreSheetsThanAnyoneWrites()
    {
        // 2.35 MB of upload declaring sixteen million sheets retained 2,441 MB, held for the cursor's
        // whole life. The ceiling beside it guarded a different list in a different method.
        Assert.Throws<TabularLimitException>(
            () => Workbook(sheets: 10, options: new XlsxCursorOptions { MaxSheets = 5 }));
    }

    [Fact]
    public void RefusesAWorkbookDeclaringMoreRelationshipsThanSheets()
    {
        Assert.Throws<TabularLimitException>(
            () => Workbook(sheets: 10, options: new XlsxCursorOptions { MaxRelationships = 3 }));
    }

    [Fact]
    public void CountsBothListsTheStyleTableGrows()
    {
        // The first version of this ceiling counted the cell formats and left the number formats
        // beside them unbounded — and those carry a string, so they cost more each.
        string formats = string.Concat(
            Enumerable.Range(164, 20).Select(i => $"""<numFmt numFmtId="{i}" formatCode="0.000"/>"""));

        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c t="inlineStr"><is><t>a</t></is></c></row>""")
            .WithStyles($"<numFmts>{formats}</numFmts><cellXfs count=\"1\"><xf numFmtId=\"0\"/></cellXfs>")
            .Build();

        using MemoryStream stream = new(package, writable: false);

        Assert.Throws<TabularLimitException>(
            () => new XlsxCursor(stream, new XlsxCursorOptions { MaxCellFormats = 5 }));
    }

    /// <summary>A workbook of shared strings each this many characters long.</summary>
    private static byte[] LongSharedStringWorkbook(int entries, int charsEach)
    {
        StringBuilder items = new(entries * (charsEach + 20));

        for (int i = 0; i < entries; i++)
        {
            items.Append("<si><t>").Append('x', charsEach).Append("</t></si>");
        }

        return new XlsxPackage()
            .WithSheet("Sheet1", """<row><c t="s"><v>0</v></c></row>""")
            .WithSharedStrings(items.ToString())
            .Build();
    }
}
