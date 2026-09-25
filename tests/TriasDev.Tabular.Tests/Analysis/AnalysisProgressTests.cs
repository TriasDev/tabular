using System.Text;

using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

public sealed class AnalysisProgressTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Collects reports synchronously, unlike <see cref="Progress{T}"/>, which posts them.</summary>
    private sealed class Recorder : IProgress<AnalysisProgress>
    {
        public List<AnalysisProgress> Reports { get; } = [];

        public void Report(AnalysisProgress value) => Reports.Add(value);
    }

    private static byte[] Csv(int rows)
    {
        StringBuilder text = new("id;name\n");

        for (int i = 1; i <= rows; i++)
        {
            text.Append(i).Append(";name ").Append(i).Append('\n');
        }

        return Utf8NoBom.GetBytes(text.ToString());
    }

    [Fact]
    public void ReportsOnTheIntervalAndOnceMoreWhenDone()
    {
        using MemoryStream stream = new(Csv(25), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        Recorder recorder = new();

        new TabularAnalyzer(new AnalysisOptions { ProgressInterval = 10, ProgressStep = 0 })
            .Analyze(cursor, recorder, TestContext.Current.CancellationToken);

        Assert.Equal([10L, 20L, 25L], recorder.Reports.Select(r => r.RowsRead));
        Assert.True(recorder.Reports[^1].IsComplete);
        Assert.All(recorder.Reports.SkipLast(1), r => Assert.False(r.IsComplete));
    }

    [Fact]
    public void GivesAFractionThatRisesToOneWithoutReadingTheFileTwice()
    {
        // The point of taking the fraction from the stream: a caller no longer counts lines first to
        // have something to divide by.
        using MemoryStream stream = new(Csv(5_000), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        Recorder recorder = new();

        new TabularAnalyzer(new AnalysisOptions { ProgressInterval = 500, ProgressStep = 0 })
            .Analyze(cursor, recorder, TestContext.Current.CancellationToken);

        List<double> fractions = [.. recorder.Reports.Select(r => r.Fraction ?? -1)];

        Assert.All(fractions, f => Assert.InRange(f, 0, 1));
        Assert.Equal(fractions.Order(), fractions);
        Assert.Equal(1, fractions[^1]);
    }

    [Fact]
    public void SaysTheFractionIsUnknownWhereTheStreamHasNoLength()
    {
        using NonSeekableStream stream = new(Csv(30));
        using CsvCursor cursor = new(stream, "test.csv", new CsvCursorOptions
        {
            Dialect = new CsvDialect
            {
                Encoding = Utf8NoBom,
                EncodingSource = DialectSource.Specified,
                Delimiter = ';',
                DelimiterSource = DialectSource.Specified,
                Quote = '"',
            },
        });
        Recorder recorder = new();

        new TabularAnalyzer(new AnalysisOptions { ProgressInterval = 10 })
            .Analyze(cursor, recorder, TestContext.Current.CancellationToken);

        // With no length there is no percentage to step by, so the row interval alone decides.
        Assert.Equal([10L, 20L, 30L, 30L], recorder.Reports.Select(r => r.RowsRead));
        Assert.All(recorder.Reports.SkipLast(1), r => Assert.Null(r.Fraction));
        Assert.Equal(30, recorder.Reports[^1].RowsRead);
        Assert.Equal(1, recorder.Reports[^1].Fraction);
    }

    [Fact]
    public void NamesTheSheetBeingReadAndCountsRowsAcrossSheets()
    {
        static string Rows(int count) =>
            string.Concat(Enumerable.Range(1, count).Select(r =>
                $"""<row r="{r}"><c r="A{r}"><v>{r}</v></c></row>"""));

        byte[] content = new XlsxPackage()
            .WithSheet("First", Rows(21))
            .WithSheet("Second", Rows(11))
            .Build();

        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);
        Recorder recorder = new();

        new TabularAnalyzer(new AnalysisOptions { ProgressInterval = 10, ProgressStep = 0 })
            .Analyze(cursor, recorder, TestContext.Current.CancellationToken);

        // The header row of each sheet is not a data row: 20 + 10.
        Assert.Equal([10L, 20L, 30L, 30L], recorder.Reports.Select(r => r.RowsRead));
        Assert.Equal(["First", "First", "Second", "Second"], recorder.Reports.Select(r => r.SheetName));
        Assert.All(recorder.Reports, r => Assert.Equal(2, r.SheetCount));

        List<double> fractions = [.. recorder.Reports.Select(r => r.Fraction ?? -1)];
        Assert.Equal(fractions.Order(), fractions);
        Assert.Equal(1, fractions[^1]);
    }

    [Fact]
    public void ReportsALargeFileOnceForEachStepOfTheFraction()
    {
        // A file of millions of rows wants a report per percent, not per ten thousand rows: the
        // fraction has to move by the step before the next report goes out.
        using MemoryStream stream = new(Csv(200_000), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        Recorder recorder = new();

        new TabularAnalyzer(new AnalysisOptions { ProgressInterval = 1_000, ProgressStep = 0.1 })
            .Analyze(cursor, recorder, TestContext.Current.CancellationToken);

        List<double> fractions = [.. recorder.Reports.SkipLast(1).Select(r => r.Fraction!.Value)];

        Assert.InRange(fractions.Count, 5, 10);
        Assert.All(fractions.Zip(fractions.Skip(1)), pair => Assert.True(pair.Second - pair.First >= 0.1));
    }

    [Fact]
    public void ReportsASmallFileNoMoreOftenThanTheRowInterval()
    {
        // A percent of twenty thousand rows is two hundred rows — a hundred reports for a file read in
        // milliseconds. The row interval is a floor under the step.
        using MemoryStream stream = new(Csv(20_000), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        Recorder recorder = new();

        new TabularAnalyzer().Analyze(cursor, recorder, TestContext.Current.CancellationToken);

        Assert.InRange(recorder.Reports.Count, 1, 3);
        Assert.True(recorder.Reports[^1].IsComplete);
    }

    [Fact]
    public void AnalysesExactlyAsBeforeWhenNobodyAsksForProgress()
    {
        byte[] content = Csv(40);

        FileProfile Profile(IProgress<AnalysisProgress>? progress)
        {
            using MemoryStream stream = new(content, writable: false);
            using CsvCursor cursor = new(stream, "test.csv");
            return new TabularAnalyzer().Analyze(cursor, progress, TestContext.Current.CancellationToken);
        }

        FileProfile without = Profile(null);
        FileProfile with = Profile(new Recorder());

        Assert.Equal(without.Sheets[0].RowCount, with.Sheets[0].RowCount);
        Assert.Equal(
            without.Sheets[0].Columns.Select(c => c.Facts.DistinctCount),
            with.Sheets[0].Columns.Select(c => c.Facts.DistinctCount));
    }

    /// <summary>A stream that can be read forward and nothing else, like one arriving over a network.</summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
