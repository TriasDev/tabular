using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// What a review round found, each pinned by the case that showed it.
/// </summary>
public sealed class ReviewFixesTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // -- A csv has no width --------------------------------------------------------------------

    [Fact]
    public void RefusesARowWiderThanTheFormatPeopleOpenItIn()
    {
        // The cheapest attack there is: no quoting, no encoding, no structure to get right. Measured
        // before the ceiling existed, 1.9 MB of commas became two million cells and 90 MB of heap
        // inside a single read.
        byte[] commas = new byte[20_000];
        Array.Fill(commas, (byte)';');

        using MemoryStream stream = new(commas, writable: false);
        using CsvCursor cursor = new(stream, "bomb.csv");

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.Contains("16384", failure.Message);
    }

    [Fact]
    public void ReadsAFileAsWideAsTheCeilingAllows()
    {
        // The bound has to be reachable from below, or it is refusing ordinary files.
        string row = string.Join(';', Enumerable.Range(0, 16_384).Select(i => i.ToString()));

        using MemoryStream stream = new(Utf8NoBom.GetBytes(row), writable: false);
        using CsvCursor cursor = new(stream, "wide.csv");

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(16_384, cursor.CurrentRow.Length);
    }

    // -- A row with nothing in it is not a row -------------------------------------------------

    [Fact]
    public void DoesNotCountBlankRowsAsData()
    {
        // A spreadsheet accumulates them below its data as a matter of course. Counting them made
        // every fact describe the padding: the column below reads as 99% empty when it is in fact
        // full, and extraction — which skips them — would then disagree with the profile meant to
        // predict it.
        StringBuilder file = new("iso\n");

        for (int i = 0; i < 99; i++)
        {
            file.Append('\n');
        }

        file.Append("DE\n");

        ColumnFacts facts = Profile(file.ToString()).Sheets[0].Columns[0].Facts;

        Assert.Equal(1, Profile(file.ToString()).Sheets[0].RowCount);
        Assert.Equal(0, facts.EmptyCount);
        Assert.Equal(1, facts.NonEmptyCount);
    }

    [Fact]
    public void KeepsARowWhereAnySingleColumnHasAValue()
    {
        // Only a row with nothing at all is padding. One value anywhere makes it a record, and its
        // other columns are then genuinely empty rather than absent.
        ColumnFacts second = Profile("a;b\n;x\n").Sheets[0].Columns[1].Facts;
        ColumnFacts first = Profile("a;b\n;x\n").Sheets[0].Columns[0].Facts;

        Assert.Equal(1, second.NonEmptyCount);
        Assert.Equal(1, first.EmptyCount);
    }

    // -- An empty cell disqualifies a column from identifying its rows -------------------------

    [Fact]
    public void AColumnWithAnEmptyCellCannotIdentifyItsRows()
    {
        // The decision: a column that identifies a record must do so for every record. Counting only
        // the non-empty values would call this unique and let it be mapped to an identifying field.
        ColumnFacts facts = Profile("id;other\nA1;x\n;y\nA2;z\n").Sheets[0].Columns[0].Facts;

        Assert.Equal(1, facts.EmptyCount);
        Assert.False(facts.IsUnique);
    }

    [Fact]
    public void AColumnThatIsEmptyThroughoutIsNotUnique()
    {
        // It used to report true: nothing distinct, nothing non-empty, and zero equals zero.
        ColumnFacts facts = Profile("id;other\n;x\n;y\n").Sheets[0].Columns[0].Facts;

        Assert.Equal(0, facts.NonEmptyCount);
        Assert.False(facts.IsUnique);
    }

    [Fact]
    public void SaysNothingAboutAColumnOfAFileWithNoRows()
    {
        // Not false — there was no question. Null is the answer to "not determined".
        ColumnFacts facts = Profile("id;other\n").Sheets[0].Columns[0].Facts;

        Assert.Null(facts.IsUnique);
    }

    [Fact]
    public void StillRecognisesAColumnThatIdentifiesEveryRow()
    {
        ColumnFacts facts = Profile("id\nA1\nA2\nA3\n").Sheets[0].Columns[0].Facts;

        Assert.True(facts.IsUnique);
    }

    // -- A time of day is not a date -----------------------------------------------------------

    [Fact]
    public void DoesNotReadATimeOfDayAsADate()
    {
        // DateTime.TryParse fills the missing half of "08:30" with today, so a column of shift times
        // read as dates — and the same bytes analysed on two days gave different answers, when the
        // whole promise of the profile is that it is a function of the file.
        ColumnFacts facts = Profile("shift\n08:30\n17:00\n12:15\n").Sheets[0].Columns[0].Facts;

        Assert.Null(facts.MinDate);
        Assert.Null(facts.MaxDate);
        Assert.All(facts.ParseCounts, counts => Assert.Equal(0, counts.Date));
    }

    [Fact]
    public void DoesNotStampADateOnToATimeAmongRealDates()
    {
        // The stray value is text that will not read as a date, not a value silently carrying the day
        // the import happened to run.
        ColumnFacts facts = Profile("when\n2023-01-15\n2023-02-20\n08:30\n").Sheets[0].Columns[0].Facts;

        Assert.Equal(new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Unspecified), facts.MinDate);
        Assert.Equal(new DateTime(2023, 2, 20, 0, 0, 0, DateTimeKind.Unspecified), facts.MaxDate);
    }

    // -- A rule about numbers cannot be satisfied by something that is not one -------------------

    [Theory]
    [InlineData("Acme GmbH")]
    [InlineData("")]
    public void ARangeRuleRefusesAValueThatIsNotANumber(string text)
    {
        // MappedValue.Number reads anything that is not a number as zero, so MaxValue(100) used to
        // accept a company name and MinValue(0) accepted everything.
        MappedValue value = text.Length == 0 ? MappedValue.Absent : MappedValue.FromText(text);

        Assert.False(new FieldConstraint.MaxValue(100).IsSatisfiedBy(value));
        Assert.False(new FieldConstraint.MinValue(0).IsSatisfiedBy(value));
        Assert.False(new FieldConstraint.MinValue(-5).IsSatisfiedBy(value));
    }

    [Fact]
    public void ARangeRuleRefusesADate()
    {
        MappedValue date = MappedValue.FromDate(new DateTime(2023, 1, 15, 0, 0, 0, DateTimeKind.Unspecified));

        Assert.False(new FieldConstraint.MaxValue(decimal.MaxValue).IsSatisfiedBy(date));
    }

    [Fact]
    public void ARangeRuleStillJudgesNumbers()
    {
        Assert.True(new FieldConstraint.MinValue(18).IsSatisfiedBy(MappedValue.FromInteger(21)));
        Assert.False(new FieldConstraint.MinValue(18).IsSatisfiedBy(MappedValue.FromInteger(17)));
        Assert.True(new FieldConstraint.MaxValue(100).IsSatisfiedBy(MappedValue.FromDecimal(99.5m)));
        Assert.False(new FieldConstraint.MaxValue(100).IsSatisfiedBy(MappedValue.FromDecimal(100.5m)));
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

        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

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

        Assert.Throws<InvalidDataException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
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
        using CancellingStream stream = new(inner, source, cancelAfterBytes: 4_096);
        using CsvCursor cursor = new(stream, "long.csv", new CsvCursorOptions { MaxColumns = 100_000 });

        stream.Arm();

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
        using CancellingStream stream = new(inner, source, cancelAfterBytes: 4_096);
        using XlsxCursor cursor = new(stream);

        stream.Arm();

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

    /// <summary>
    /// A stream that cancels a token once it has served a given number of bytes, so that a read can
    /// be interrupted at a point of the test's choosing rather than before it begins.
    /// </summary>
    private sealed class CancellingStream(MemoryStream inner, CancellationTokenSource source, int cancelAfterBytes)
        : Stream
    {
        private long _servedSinceArmed;
        private bool _armed;

        /// <summary>Starts counting. Anything read before this — opening a package — does not count.</summary>
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);

            if (_armed)
            {
                _servedSinceArmed += read;

                if (_servedSinceArmed > cancelAfterBytes)
                {
                    source.Cancel();
                }
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // -- A row the import drops is worth a number -----------------------------------------------

    [Fact]
    public void CountsRowsThatHeldSomethingJustNotInAMappedColumn()
    {
        // Padding below the data is expected and uninteresting. A row carrying a note in a column
        // nobody mapped is a record the run drops, and dropping records without a word is the worse
        // mistake — harder to notice than a number that does not add up.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso")] };

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "iso", TargetFieldName = "iso" }],
        };

        // Row 1 imports. Row 2 is padding. Row 3 carries a note in the unmapped column.
        using MemoryStream stream = new(Utf8NoBom.GetBytes("iso;note\nDE;\n;\n;see appendix\n"), writable: false);

        using ImportRun<string?> run = TabularImporter.Import(
            stream,
            "t.csv",
            plan,
            schema,
            row => row[schema.Fields[0] as TextField ?? throw new InvalidOperationException()], cancellationToken: TestContext.Current.CancellationToken);

        run.All();

        Assert.Equal(1, run.Summary.RowsProduced);
        Assert.Equal(2, run.Summary.RowsSkipped);
        Assert.Equal(1, run.Summary.RowsWithNothingMapped);
    }

    [Fact]
    public void CountsNoSuchRowWhenEveryDroppedRowIsPadding()
    {
        TargetSchema schema = new() { Fields = [ImportField.Text("iso")] };

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "iso", TargetFieldName = "iso" }],
        };

        using MemoryStream stream = new(Utf8NoBom.GetBytes("iso\nDE\n\n\n"), writable: false);

        using ImportRun<string?> run = TabularImporter.Import(
            stream,
            "t.csv",
            plan,
            schema,
            row => row[schema.Fields[0] as TextField ?? throw new InvalidOperationException()], cancellationToken: TestContext.Current.CancellationToken);

        run.All();

        Assert.Equal(0, run.Summary.RowsWithNothingMapped);
    }

    [Fact]
    public void OpensNoStreamForAMappingThatCannotWork()
    {
        // Opening a file is not free — a csv's head is read to detect the dialect, a package's whole
        // central directory to open it — and a mapping that does not fit its schema is answerable
        // without any of that.
        TargetSchema schema = new() { Fields = [ImportField.Text("iso").Require()] };

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "x", TargetFieldName = "other" }],
        };

        CountingStream stream = new(Utf8NoBom.GetBytes("iso\nDE\n"));

        Assert.Throws<TabularStructureException>(
            () => TabularImporter.Import(stream, "t.csv", plan, schema, row => row.RowNumber, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, stream.Reads);
    }

    /// <summary>A stream that counts how often anything asked it for bytes.</summary>
    private sealed class CountingStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public int Reads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Reads++;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            Reads++;
            return base.Read(buffer);
        }
    }

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

    private static FileProfile Profile(string csv)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return new TabularAnalyzer().Analyze(cursor);
    }
}
