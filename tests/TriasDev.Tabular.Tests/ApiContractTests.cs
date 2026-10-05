using System.Text;

using TriasDev.Tabular.Archive;
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
    private static readonly TextImportField Name = ImportField.Text("name");

    private static readonly ImportSchema Schema = new() { Fields = [Name] };

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "name", FieldName = "name" }],
    };

    private static MemoryStream Csv() => new(Encoding.UTF8.GetBytes("name;x\na;b\n"), writable: false);

    private static MemoryStream Workbook() => new(new XlsxPackage()
        .WithSheet("S", """<row r="1"><c r="A1" t="inlineStr"><is><t>name</t></is></c></row>""")
        .Build(), writable: false);

    private static ITabularCursor[] Cursors() =>
        [
            new CsvCursor(Csv(), "t.csv"),
            new XlsxCursor(Workbook(), cancellationToken: TestContext.Current.CancellationToken),
            new GzipCursor(new MemoryStream(GzipFile.Of("name;x\na;b\n"), writable: false), "t.csv.gz", cancellationToken: TestContext.Current.CancellationToken),
            new ArchiveCursor(new MemoryStream(GzipFile.Of(TarArchive.Of(System.Formats.Tar.TarEntryFormat.Pax, ("t.csv", "name;x\na;b\n")))), cancellationToken: TestContext.Current.CancellationToken),
        ];

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
    public void TabularFileOpenIsTheOnlyWayToACursor()
    {
        Type[] exported = typeof(ITabularCursor).Assembly.GetExportedTypes();

        Assert.DoesNotContain(exported, t => !t.IsInterface && typeof(ITabularCursor).IsAssignableFrom(t));
        Assert.DoesNotContain(typeof(CsvDialectDetector), exported);
    }

    [Fact]
    public void OnlyACursorCreatesItsDiagnostics() =>
        // The counts are live on the cursor that repairs the file; a caller has nothing to count.
        Assert.Empty(typeof(CursorDiagnostics).GetConstructors());

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
            Assert.Throws<ObjectDisposedException>(() => cursor.MoveToSheet(0, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void ARunCanBeDisposedTwiceAndRefusesUseAfterwards()
    {
        using CsvCursor cursor = new(Csv(), "t.csv");
        ImportRun<string?> run = TabularImporter.Import(cursor, Plan, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken);

        run.Dispose();
        run.Dispose();

        Assert.Throws<ObjectDisposedException>(() => run.ReadRows(TestContext.Current.CancellationToken).ToList());
    }

    [Fact]
    public void ARunIsReadOnce()
    {
        using CsvCursor cursor = new(Csv(), "t.csv");
        using ImportRun<string?> run = TabularImporter.Import(cursor, Plan, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Single(run.ReadRows(TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => run.ReadRows(TestContext.Current.CancellationToken).ToList());
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

        Assert.Throws<ArgumentOutOfRangeException>(() => run.ReadChunks(size, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesAnAllLimitOfNothing()
    {
        using CsvCursor cursor = new(Csv(), "t.csv");
        using ImportRun<string?> run = TabularImporter.Import(cursor, Plan, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Throws<ArgumentOutOfRangeException>(() => run.ReadAll(limit: 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RefusesAForwardOnlyStreamForDetectionAndClosesIt()
    {
        ForwardOnly stream = new(Csv());

        Assert.Throws<ArgumentException>(() => TabularFile.Open(stream, "t.csv", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[1], 0, 1));
    }

    [Fact]
    public void ReadsAForwardOnlyZipThroughAnArchiveCursorAsItAlwaysDid()
    {
        // ZipArchive copies a stream it cannot seek; choosing the container must not seek it first.
        byte[] zip = new ZipArchiveBuilder().With("t.csv", "name;x\na;b\n").Build();

        using ArchiveCursor cursor = new(new ForwardOnly(new MemoryStream(zip)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TabularFormat.Zip, cursor.Format);
        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadsAForwardOnlyCsvWhenTheDialectIsGiven()
    {
        // Detection rewinds, so it needs a seekable stream; with the delimiter and the encoding both
        // stated there is nothing to detect and the head is not read.
        CsvCursorOptions options = new()
        {
            Encoding = new UTF8Encoding(false),
            Delimiter = ';',
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
