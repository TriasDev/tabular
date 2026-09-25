using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// One rule for every entry point: a stream handed over is closed — whatever happens, failure
/// included — unless the caller said to leave it open.
/// </summary>
public sealed class StreamOwnershipTests
{
    private static readonly byte[] Csv = Encoding.UTF8.GetBytes("name;x\na;b\n");

    private static readonly byte[] Ole2 = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[512]];

    private static readonly byte[] BrokenZip = [0x50, 0x4B, 0x03, 0x04, .. new byte[64]];

    private static readonly TextField Name = ImportField.Text("name");

    private static readonly TargetSchema Schema = new() { Fields = [Name] };

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "name", TargetFieldName = "name" }],
    };

    private sealed class TrackedStream(byte[] content) : MemoryStream(content, writable: false)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACsvCursorThatFailsToOpenFollowsLeaveOpen(bool leaveOpen)
    {
        TrackedStream stream = new(Ole2);

        Assert.Throws<TabularFormatException>(() => new CsvCursor(stream, "t.csv", leaveOpen: leaveOpen));

        Assert.Equal(!leaveOpen, stream.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnXlsxCursorWhoseArchiveFailsToOpenFollowsLeaveOpen(bool leaveOpen)
    {
        TrackedStream stream = new(BrokenZip);

        Assert.Throws<TabularFormatException>(() => new XlsxCursor(stream, leaveOpen: leaveOpen));

        Assert.Equal(!leaveOpen, stream.IsDisposed);
    }

    [Fact]
    public void ACsvCursorHonoursCancellationWhileOpening()
    {
        TrackedStream stream = new(Csv);

        Assert.ThrowsAny<OperationCanceledException>(() => new CsvCursor(stream, "t.csv", cancellationToken: new CancellationToken(canceled: true)));
        Assert.True(stream.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningByDetectionFollowsLeaveOpen(bool leaveOpen)
    {
        TrackedStream stream = new(Csv);

        using (TabularFile.Open(stream, "t.csv", new TabularOpenOptions { LeaveOpen = leaveOpen }, TestContext.Current.CancellationToken))
        {
            Assert.False(stream.IsDisposed);
        }

        Assert.Equal(!leaveOpen, stream.IsDisposed);
    }

    [Fact]
    public void OpeningByDetectionClosesTheStreamOfAFileItRefuses()
    {
        TrackedStream stream = new(Ole2);

        Assert.Throws<TabularFormatException>(() => TabularFile.Open(stream, "upload", cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public void OpeningByDetectionPassesTheCursorOptionsOn()
    {
        // The only way to raise or lower a ceiling used to be to skip detection and build the cursor
        // by hand.
        TabularOpenOptions options = new()
        {
            Csv = new CsvCursorOptions { MaxColumns = 1 },
            Xlsx = new XlsxCursorOptions { MaxSheets = 0 },
        };

        using (ITabularCursor csv = TabularFile.Open(new MemoryStream(Csv), "t.csv", options, TestContext.Current.CancellationToken))
        {
            Assert.Throws<TabularLimitException>(() => csv.ReadRow(TestContext.Current.CancellationToken));
        }

        byte[] workbook = new XlsxPackage().WithSheet("S", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""").Build();

        Assert.Throws<TabularLimitException>(() => TabularFile.Open(new MemoryStream(workbook), "w.xlsx", options, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnImportFromAStreamFollowsLeaveOpen(bool leaveOpen)
    {
        // An upload the host still wants afterwards — to archive it, to hash it — used to be closed
        // by the run with no way to say otherwise.
        TrackedStream stream = new(Csv);
        ImportOptions options = new() { Open = new TabularOpenOptions { LeaveOpen = leaveOpen } };

        using (ImportRun<string?> run = TabularImporter.Import(stream, "t.csv", Plan, Schema, row => row[Name], options, TestContext.Current.CancellationToken))
        {
            Assert.Single(run.All(cancellationToken: TestContext.Current.CancellationToken).Items);
        }

        Assert.Equal(!leaveOpen, stream.IsDisposed);
    }

    [Fact]
    public void AnImportFromAStreamClosesItEvenWhenThePlanIsRefused()
    {
        TrackedStream stream = new(Csv);
        MappingPlan unknownField = Plan with { Bindings = [Plan.Bindings[0] with { TargetFieldName = "nope" }] };

        Assert.Throws<MappingPlanException>(() => TabularImporter.Import(stream, "t.csv", unknownField, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public void AnExtractionSessionDoesNotPretendToOwnAnything()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(ExtractionSession)));
    }
}
