using System.Text;

using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Extraction;
using TriasDev.Tabular.Mapping;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Input built to break the reader rather than to be read.
/// </summary>
/// <remarks>
/// Every file this library sees was chosen by someone else, so every one of them is untrusted. These
/// cases are not hypothetical: each was reproduced against the reader before it was fixed, and most
/// are a few hundred bytes. A reader that survives a malformed file but not a hostile one is a reader
/// that has only been tested by its friends.
/// </remarks>
public sealed class HostileInputTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static void ReadAll(byte[] content)
    {
        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        while (cursor.ReadRow())
        {
            // Reading to the end is the test: it must neither hang nor exhaust memory.
        }
    }

    private static List<string?[]> ReadCsv(string text)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(text), writable: false);
        using CsvCursor cursor = new(stream, "hostile.csv");

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

    private static byte[] Workbook(string rowsXml) =>
        new XlsxPackage().WithSheet("Sheet1", rowsXml).Build();

    [Theory]
    [InlineData("AAAAA1")]
    [InlineData("AAAAAA1")]
    [InlineData("ZZZZZZ1")]
    [InlineData("BBBBBBBBBB1")]
    public void RefusesACellReferenceBeyondTheFormatsLastColumn(string reference)
    {
        // A single attribute value. `ZZZZZZ` is column 321,272,406, and a reader that sizes a row
        // from it allocates gigabytes from a file smaller than a favicon. The format itself stops at
        // XFD, so anything past it is not a large file — it is a malformed one.
        byte[] content = Workbook($"""<row r="1"><c r="{reference}" t="inlineStr"><is><t>x</t></is></c></row>""");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ReadAll(content));

        Assert.Contains("column", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsTheFormatsLastColumn()
    {
        // The boundary is XFD, and it is legal.
        byte[] content = Workbook("""<row r="1"><c r="XFD1" t="inlineStr"><is><t>last</t></is></c></row>""");

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(16_384, cursor.CurrentRow.Length);
        Assert.Equal("last", cursor.CurrentRow[16_383].Text);
    }

    [Fact]
    public void RefusesANegativeSharedStringIndex()
    {
        // `int.TryParse` accepts a leading sign, so -1 passed the "is it below the table's length"
        // check and indexed the array.
        byte[] content = new XlsxPackage()
            .WithSharedStrings("<si><t>a</t></si>")
            .WithSheet("Sheet1", """<row r="1"><c r="A1" t="s"><v>-1</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.CurrentRow[0].IsEmpty);
    }

    [Fact]
    public void RefusesANegativeStyleIndex()
    {
        byte[] content = Workbook("""<row r="1"><c r="A1" s="-1"><v>44927</v></c></row>""");

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(RawCellKind.Number, cursor.CurrentRow[0].Kind);
    }

    [Theory]
    [InlineData("&#xD800;")]
    [InlineData("&#x110000;")]
    [InlineData("&#0;")]
    [InlineData("&#99999999999999;")]
    public void KeepsAnImpossibleCharacterReferenceAsTextRatherThanThrowing(string entity)
    {
        byte[] content = Workbook($"""<row r="1"><c r="A1" t="inlineStr"><is><t>a{entity}b</t></is></c></row>""");

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.NotNull(cursor.CurrentRow[0].Text);
    }

    [Theory]
    [InlineData("1e20")]
    [InlineData("-1e20")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void KeepsAnImpossibleDateSerialAsANumberRatherThanThrowing(string serial)
    {
        byte[] content = new XlsxPackage()
            .WithStyles("""<?xml version="1.0"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font/></fonts><fills count="1"><fill/></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0" xfId="0"/><xf numFmtId="14" xfId="0" applyNumberFormat="1"/></cellXfs></styleSheet>""")
            .WithSheet("Sheet1", $"""<row r="1"><c r="A1" s="1"><v>{serial}</v></c></row>""")
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.NotEqual(RawCellKind.Date, cursor.CurrentRow[0].Kind);
    }

    [Fact]
    public void RefusesACellLongerThanTheReadersCeiling()
    {
        // The package guard counts bytes off the wire. One enormous cell costs several times that in
        // memory — decoded to UTF-16, then buffered, then copied while the buffer grows — so the
        // scanner needs a ceiling of its own.
        string enormous = new('a', 40_000_000);
        byte[] content = Workbook($"""<row r="1"><c r="A1" t="inlineStr"><is><t>{enormous}</t></is></c></row>""");

        Assert.Throws<InvalidDataException>(() => ReadAll(content));
    }

    [Fact]
    public void ReadsAQuoteStormWithoutTakingQuadraticWork()
    {
        // Every closing quote used to replay a character through a list that was copied whole on each
        // push, so a file drained its own pushback in O(n²): 768 KB took eighteen seconds, and a few
        // megabytes would hold a request open for hours.
        //
        // Measured as allocation against two sizes rather than as a wall-clock ceiling. A ceiling
        // cannot catch this any more: the width limit bounds a row at 16,384 fields, and at that size
        // even the quadratic version finished in well under a second, so any threshold loose enough
        // not to flake was also loose enough to pass the defect. A ratio does not care how fast the
        // machine is — four times the input costs four times as much when the cost is linear, and
        // sixteen times when it is not.
        long small = AllocatedReadingAQuoteStorm(1_000);
        long large = AllocatedReadingAQuoteStorm(4_000);

        double ratio = (double)large / small;

        Assert.True(
            ratio < 8,
            $"four times the input cost {ratio:F1} times the allocation, which is not linear "
            + $"({small} bytes against {large})");
    }

    /// <summary>Reads a row of nothing but empty quoted fields, and says what that allocated.</summary>
    private static long AllocatedReadingAQuoteStorm(int quotes)
    {
        StringBuilder text = new();
        text.Append('"');

        for (int i = 0; i < quotes; i++)
        {
            text.Append(";\"\"");
        }

        text.Append("\n\n\n\n\n");

        byte[] bytes = Utf8NoBom.GetBytes(text.ToString());

        using MemoryStream stream = new(bytes, writable: false);
        using CsvCursor cursor = new(stream, "storm.csv");

        long before = GC.GetAllocatedBytesForCurrentThread();

        while (cursor.ReadRow())
        {
            // Nothing is kept, so what is measured is the reader's own allocation.
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void RecoveryFromAnUnterminatedQuoteLosesNoRecordAndInventsNoValue()
    {
        // The line ending that trips the bound was consumed before the raw text was kept, so it was
        // never replayed: the record it terminated fused with the next one, producing a value that
        // was never in the file. A repair that corrupts is worse than the damage it repairs.
        List<string?[]> rows = ReadCsv("\"a\nb\nc\nd\ne\nf\ng\n");

        Assert.Equal(new string?[] { "\"a" }, rows[0]);
        Assert.Equal(new string?[] { "b" }, rows[1]);
        Assert.Equal(new string?[] { "c" }, rows[2]);
        Assert.Equal(new string?[] { "d" }, rows[3]);
        Assert.Equal(new string?[] { "e" }, rows[4]);
        Assert.Equal(new string?[] { "f" }, rows[5]);
        Assert.Equal(new string?[] { "g" }, rows[6]);
        Assert.Equal(7, rows.Count);
    }

    [Fact]
    public void BoundsAnUnterminatedQuoteInAFileWithCarriageReturnLineEndings()
    {
        // The bound counted carriage returns and then tested only line feeds, so on a classic-Mac
        // file the bound that exists to stop a stray quote consuming the file did not apply to it.
        List<string?[]> rows = ReadCsv("\"a\rb\rc\rd\re\rf\rg\r");

        Assert.True(rows.Count >= 6, $"a stray quote swallowed the file: {rows.Count} rows");
        Assert.Equal(new string?[] { "g" }, rows[^1]);
    }

    [Fact]
    public void RefusesACsvFieldLongerThanTheReadersCeiling()
    {
        byte[] content = Utf8NoBom.GetBytes("a\n" + new string('x', 40_000_000));

        using MemoryStream stream = new(content, writable: false);
        using CsvCursor cursor = new(stream, "huge.csv");

        Assert.Throws<InvalidDataException>(() =>
        {
            while (cursor.ReadRow(TestContext.Current.CancellationToken))
            {
                // The bound trips somewhere inside the read; where is not the point.
            }
        });
    }

    [Fact]
    public void KeepsAFileUtf8WhenACharacterStraddlesTheDetectionProbe()
    {
        // Detection reads a prefix. A multi-byte character cut in half by that prefix is not invalid
        // UTF-8 — it is an incomplete view of valid UTF-8 — and reading it as invalid demotes the
        // whole file to Windows-1252, turning every umlaut into two characters. This is the exact
        // defect the detector exists to prevent, reached from the other side.
        const int probe = 64 * 1024;

        for (int offset = probe - 2; offset <= probe; offset++)
        {
            StringBuilder text = new("Name\n");
            text.Append('x', offset - text.Length);
            text.Append("Müller\n");

            using MemoryStream stream = new(Utf8NoBom.GetBytes(text.ToString()), writable: false);
            using CsvCursor cursor = new(stream, "boundary.csv");

            Assert.True(
                cursor.Dialect.Encoding.CodePage == 65001,
                $"a character at offset {offset} demoted the file to {cursor.Dialect.Encoding.WebName}");
        }
    }

    [Fact]
    public void FindsTheDelimiterInAFileThatOpensWithATitleLine()
    {
        // A title above the table is ordinary in banking and ERP exports. Requiring the delimiter on
        // the very first line discards the real answer and falls back to a guess, after which every
        // row is read as one undivided field.
        List<string?[]> rows = ReadCsv("Report 2026\nName,Amount\nAcme,1\nBeta,2\n");

        Assert.Equal(new string?[] { "Name", "Amount" }, rows[1]);
        Assert.Equal(new string?[] { "Acme", "1" }, rows[2]);
    }

    [Fact]
    public void FindsTheDelimiterDespiteAStrayQuoteInTheProbe()
    {
        // One unbalanced quote used to make the probe's line splitting swallow everything after it,
        // so no candidate scored and the fallback took over — after which every row read as one
        // undivided field.
        //
        // What the reader then does with that quote is a separate matter and is the documented
        // recovery: it opens a field that never closes, and with fewer lines than the bound allows,
        // the field runs to the end of the file. Detection is what is under test here.
        using MemoryStream stream = new(Utf8NoBom.GetBytes("\"Name,Amount\nAcme,1\nBeta,2\n"), writable: false);
        using CsvCursor cursor = new(stream, "stray.csv");

        Assert.Equal(',', cursor.Dialect.Delimiter);
        Assert.Equal(DialectSource.Detected, cursor.Dialect.DelimiterSource);
    }

    [Theory]
    [InlineData("1e300", ColumnType.Decimal)]
    [InlineData("-1e300", ColumnType.Decimal)]
    [InlineData("1e30", ColumnType.Integer)]
    public void RefusesANumberTheTargetTypeCannotHoldRatherThanThrowingOrTruncating(string literal, ColumnType type)
    {
        // One of these threw and the other silently saturated, writing long.MaxValue into the row as
        // though the file had said so. Both are now a reported type mismatch.
        byte[] content = Workbook($"""<row r="1"><c r="A1" t="inlineStr"><is><t>Wert</t></is></c></row><row r="2"><c r="A2"><v>{literal}</v></c></row>""");

        TargetSchema schema = new() { Fields = [new TargetField { Name = "amount", Type = type }] };

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "Wert", TargetFieldName = "amount" }],
        };

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);
        using ExtractionSession session = TabularExtractor.Start(cursor, plan, schema, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(session.ReadRow());
        Assert.True(session.CurrentRowHasErrors);
        Assert.Equal("value.type-mismatch", Assert.Single(session.CurrentErrors).Code);
    }

    [Fact]
    public void ProfilesAWideRowWithoutRunningOutOfMemory()
    {
        // The analyzer builds a profiler per column, each carrying several collections. Bounding the
        // column count is what keeps that from being a lever.
        byte[] content = Workbook("""<row r="1"><c r="XFD1" t="inlineStr"><is><t>x</t></is></c></row><row r="2"><c r="XFD2" t="inlineStr"><is><t>y</t></is></c></row>""");

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        FileProfile profile = new TabularAnalyzer().Analyze(cursor, TestContext.Current.CancellationToken);

        Assert.Equal(16_384, Assert.Single(profile.Sheets).Columns.Count);
    }
}
