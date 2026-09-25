using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// What a second, adversarial review round found — mostly in the fixes the first round produced.
/// </summary>
public sealed class SecondRoundFixesTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // -- A ceiling has to count what the memory is spent on ---------------------------------------

    [Fact]
    public void RefusesASharedStringTableTooLargeToHoldEvenWithinItsEntryCount()
    {
        // The entry ceiling bounds the wrong dimension, which was the mistake it was added to fix,
        // one storey up: a million entries of two thousand characters each satisfies it and costs
        // 3.9 GB — measured, from a workbook of three megabytes. What a table costs is its characters.
        byte[] package = SharedStringWorkbook(entries: 10, charsEach: 100);

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
        using CancellingStream stream = new(inner, source, cancelAfterBytes: 4_096);
        using CsvCursor cursor = new(stream, "long.csv", new CsvCursorOptions { MaxColumns = 100_000 });

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));          // the header, before anything is armed
        stream.Arm();

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
        using CancellingStream stream = new(inner, source, cancelAfterBytes: 512);
        using XlsxCursor cursor = new(stream);

        stream.Arm();

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
        using CancellingStream stream = new(inner, source, cancelAfterBytes: 512);
        using XlsxCursor cursor = new(stream);

        stream.Arm();

        Assert.Throws<OperationCanceledException>(() => cursor.ReadRow(source.Token));
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

    // -- A malformed reference is refused, not placed --------------------------------------------

    [Fact]
    public void RefusesACellReferenceThatOverflowsInsteadOfPlacingIt()
    {
        // The accumulation wrapped to a negative number, which passed the width guard and was then
        // normalised into the next column: a malformed reference silently accepted as data.
        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c r="BBBBBBBBBBBBB1" t="inlineStr"><is><t>x</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.Throws<TabularFormatException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    // -- A value that does not name a date completely is not a date -------------------------------

    [Theory]
    [InlineData("Jan 5", "en-US")]
    [InlineData("3/15", "en-US")]
    [InlineData("15.01", "de-DE")]
    [InlineData("January 2023", "en-US")]
    [InlineData("08:30", "en-US")]
    [InlineData("1.5", "en-US")]
    public void DoesNotInventTheMissingHalfOfADate(string value, string culture)
    {
        // The parser fills in whatever the text leaves out, from the clock: a bare time becomes
        // today, a day and month become this year. The first was fixed and the second was not, so the
        // nondeterminism simply moved from the day boundary to the year boundary.
        FileProfile profile = Profile(
            $"when\n{value}\n",
            new AnalysisOptions { Cultures = [culture] });

        ColumnFacts facts = profile.Sheets[0].Columns[0].Facts;

        Assert.Null(facts.MinDate);
        Assert.All(facts.ParseCounts, counts => Assert.Equal(0, counts.Date));
    }

    [Fact]
    public void TheExtractorRefusesWhatTheProfilerRefuses()
    {
        // They disagreed, and the disagreement was structural: the profiler applied a shape test and
        // the extractor did not. So "3/15" was text to one half and March of the current year to the
        // other, and the precheck blocked files the import accepted. One rule, in one place, now.
        TargetSchema schema = new() { Fields = [ImportField.Date("when")] };

        MappingPlan plan = new()
        {
            Culture = "en-US",
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "when", TargetFieldName = "when" }],
        };

        using MemoryStream stream = new(Utf8NoBom.GetBytes("when\n3/15\n2023-05-06\n"), writable: false);

        using ImportRun<DateTime?> run = TabularImporter.Import(
            stream,
            "t.csv",
            plan,
            schema,
            row => row[schema.Fields[0] as DateField ?? throw new InvalidOperationException()], cancellationToken: TestContext.Current.CancellationToken);

        run.All(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, run.Summary.RowsProduced);
        Assert.Equal(1, run.Summary.RowsFailed);
    }

    [Fact]
    public void StillReadsADateThatNamesItselfCompletely()
    {
        FileProfile profile = Profile("when\n2023-01-15\n2023-02-20\n");
        ColumnFacts facts = profile.Sheets[0].Columns[0].Facts;

        Assert.Equal(new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Unspecified), facts.MinDate);
        Assert.Equal(new DateTime(2023, 2, 20, 0, 0, 0, DateTimeKind.Unspecified), facts.MaxDate);
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

    [Fact]
    public void ShipsTheDefaultsTheDocumentsPromise()
    {
        // The ceilings are tested through small overrides, which proves the mechanism and says
        // nothing about what actually ships. These numbers are quoted in the guide's bounds table
        // and reasoned about in the options' own remarks, so a silent change to one of them would
        // make those documents wrong with nothing to notice.
        XlsxCursorOptions xlsx = XlsxCursorOptions.Default;

        Assert.Equal(2L * 1024 * 1024 * 1024, xlsx.MaxUncompressedBytes);
        Assert.Equal(16_384, xlsx.MaxPackageEntries);
        Assert.Equal(4_096, xlsx.MaxSheets);
        Assert.Equal(8_192, xlsx.MaxRelationships);
        Assert.Equal(1_048_576, xlsx.MaxSharedStrings);
        Assert.Equal(64 * 1024 * 1024, xlsx.MaxSharedStringChars);
        Assert.Equal(100_000, xlsx.MaxCellFormats);
        Assert.Equal(16 * 1024 * 1024, xlsx.MaxValueChars);

        CsvCursorOptions csv = CsvCursorOptions.Default;

        Assert.Equal(16_384, csv.MaxColumns);
        Assert.Equal(16 * 1024 * 1024, csv.MaxFieldChars);
        Assert.Equal(4, csv.MaxQuotedFieldLines);

        Assert.Equal(1_000, ExtractionOptions.Default.MaxErrorRows);
        Assert.Equal(2_000_000, AnalysisOptions.Default.DistinctTrackingBudget);
        Assert.Equal(1_000, AnalysisOptions.Default.RetainedDistinctValues);
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

    [Fact]
    public void CostsTheSamePerLateColumnHoweverManyRowsItMissed()
    {
        // The backfill first did this once per missed row, which a file whose rows grow one column at
        // a time turns into the product of its rows and its columns. Measured as work rather than as
        // time: a timing test on a staircase file cannot see it, because that fixture is quadratic in
        // rows by construction and the defect only adds a constant factor to it. Here the two files
        // have the same number of cells and differ only in how far the late column has to reach back.
        long near = AllocatedProfilingALateColumn(rowsBefore: 50);
        long far = AllocatedProfilingALateColumn(rowsBefore: 5_000);

        Assert.True(
            far < near * 4,
            $"reaching back 5,000 rows cost {(double)far / near:F1}× what reaching back 50 did.");
    }

    /// <summary>Profiles a file whose second column appears only in the last row.</summary>
    private static long AllocatedProfilingALateColumn(int rowsBefore)
    {
        StringBuilder file = new("a\n");

        for (int i = 0; i < rowsBefore; i++)
        {
            file.Append("x\n");
        }

        file.Append("x;LATE\n");

        string csv = file.ToString();

        Profile(csv);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Profile(csv);

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void SaysNoNumberForAReferenceTooLongToMeanOne()
    {
        // It used to report column 16385 for a reference naming column 321,272,406 — the ceiling
        // presented as if it were the answer.
        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c r="ZZZZZZ1" t="inlineStr"><is><t>x</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(stream);

        TabularFormatException failure = Assert.Throws<TabularFormatException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.DoesNotContain("16385", failure.Message);
        Assert.Contains("beyond", failure.Message);
    }

    [Fact]
    public void GivesALateColumnTheRowsItMissed()
    {
        // Its counts have to add up to the sheet's, or "no row leaves this column empty" is read as
        // "every row carries a value" by everything downstream.
        FileProfile profile = Profile("a\n1\n2\n3;LATE\n");

        ColumnFacts late = profile.Sheets[0].Columns[1].Facts;

        Assert.Equal(1, late.NonEmptyCount);
        Assert.Equal(2, late.EmptyCount);
        Assert.Equal(profile.Sheets[0].RowCount, late.NonEmptyCount + late.EmptyCount);
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

    private static FileProfile Profile(string csv, AnalysisOptions? options = null)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return new TabularAnalyzer(options).Analyze(cursor);
    }

    private static byte[] SharedStringWorkbook(int entries, int charsEach)
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

    /// <summary>A stream that cancels a token once it has served a given number of bytes.</summary>
    private sealed class CancellingStream(MemoryStream inner, CancellationTokenSource source, int cancelAfterBytes)
        : Stream
    {
        private long _servedSinceArmed;
        private bool _armed;

        public void Arm() => _armed = true;

        public override bool CanRead => true;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override int ReadByte() => Count(inner.ReadByte());

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            if (_armed)
            {
                _servedSinceArmed += Math.Max(read, 1);

                if (_servedSinceArmed > cancelAfterBytes)
                {
                    source.Cancel();
                }
            }

            return read;
        }
    }
}
