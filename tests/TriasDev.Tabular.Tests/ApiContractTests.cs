using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// What every public entry point promises about disposal, argument checks and the streams it takes —
/// the contract a caller builds on without reading the implementation.
/// </summary>
public sealed class ApiContractTests
{
    private static readonly TextField Name = ImportField.Text("name");

    private static readonly TargetSchema Schema = new() { Fields = [Name] };

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "name", TargetFieldName = "name" }],
    };

    private static MemoryStream Csv() => new(Encoding.UTF8.GetBytes("name;x\na;b\n"), writable: false);

    private static MemoryStream Workbook() => new(new XlsxPackage()
        .WithSheet("S", """<row r="1"><c r="A1" t="inlineStr"><is><t>name</t></is></c></row>""")
        .Build(), writable: false);

    private static ITabularCursor[] Cursors() =>
        [new CsvCursor(Csv(), "t.csv"), new XlsxCursor(Workbook(), cancellationToken: TestContext.Current.CancellationToken)];

    /// <summary>A stream that reads but cannot seek, like a request body or an archive entry.</summary>
    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void ACursorCanBeDisposedTwice()
    {
        foreach (ITabularCursor cursor in Cursors())
        {
            cursor.Dispose();

            Exception? second = Record.Exception(cursor.Dispose);

            Assert.Null(second);
        }
    }

    [Fact]
    public void ACursorRefusesUseAfterDisposal()
    {
        foreach (ITabularCursor cursor in Cursors())
        {
            cursor.Dispose();

            Assert.Throws<ObjectDisposedException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
            Assert.Throws<ObjectDisposedException>(() => cursor.MoveToSheet(0));
        }
    }

    [Fact]
    public void ARunCanBeDisposedTwiceAndRefusesUseAfterwards()
    {
        using CsvCursor cursor = new(Csv(), "t.csv");
        ImportRun<string?> run = TabularImporter.Import(cursor, Plan, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken);

        run.Dispose();
        run.Dispose();

        Assert.Throws<ObjectDisposedException>(() => run.Rows(TestContext.Current.CancellationToken).ToList());
    }

    [Fact]
    public void ARunIsReadOnce()
    {
        using CsvCursor cursor = new(Csv(), "t.csv");
        using ImportRun<string?> run = TabularImporter.Import(cursor, Plan, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(run.Rows(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => run.Rows(TestContext.Current.CancellationToken).ToList());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RefusesAChunkSizeWhereItIsGivenNotWhereItIsFirstRead(int size)
    {
        // An iterator checks its arguments only when enumerated, so a bad size used to surface far
        // from the line that passed it — or never, if the chunks were never read.
        using CsvCursor cursor = new(Csv(), "t.csv");
        using ImportRun<string?> run = TabularImporter.Import(cursor, Plan, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() => run.InChunks(size, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesAnAllLimitOfNothing()
    {
        using CsvCursor cursor = new(Csv(), "t.csv");
        using ImportRun<string?> run = TabularImporter.Import(cursor, Plan, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() => run.All(limit: 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesAForwardOnlyStreamForDetectionAndClosesIt()
    {
        ForwardOnly stream = new(Csv());

        Assert.Throws<ArgumentException>(() => TabularFile.Open(stream, "t.csv", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void ReadsAForwardOnlyCsvWhenTheDialectIsGiven()
    {
        // Detection rewinds, so it needs a seekable stream; a caller who states the dialect needs
        // none — the way to read a request body without buffering it.
        CsvCursorOptions options = new()
        {
            Dialect = new CsvDialect
            {
                Encoding = new UTF8Encoding(false),
                EncodingSource = DialectSource.Specified,
                Delimiter = ';',
                DelimiterSource = DialectSource.Specified,
                Quote = '"',
            },
        };

        using CsvCursor cursor = new(new ForwardOnly(Csv()), "t.csv", options);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(["a", "b"], cursor.CurrentRow.ToArray().Select(c => c.AsText()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void RefusesAFieldWithoutAName(string name)
    {
        Assert.Throws<ArgumentException>(() => ImportField.Text(name));
        Assert.Throws<ArgumentException>(() => ImportField.Integer(name));
        Assert.Throws<ArgumentException>(() => ImportField.Decimal(name));
        Assert.Throws<ArgumentException>(() => ImportField.Date(name));
        Assert.Throws<ArgumentException>(() => ImportField.Boolean(name));
    }

    [Fact]
    public void RefusesATranslatedFieldThatCannotBeAddressed()
    {
        Assert.Throws<ArgumentException>(() => ImportField.Translated("", ["en"]));
        Assert.Throws<ArgumentException>(() => ImportField.Translated("title", []));
        Assert.Throws<ArgumentException>(() => ImportField.Translated("title", ["en", "EN"]));
        Assert.Throws<ArgumentException>(() => ImportField.Translated("title", ["en", " "]));
        Assert.Throws<ArgumentException>(() => ImportField.Translated("title", ["en"])["de"]);
    }
}
