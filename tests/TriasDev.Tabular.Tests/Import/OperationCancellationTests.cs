using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// Every operation that reads takes its own token, as the BCL's do; the token given when a run
/// starts still stops all of it.
/// </summary>
public sealed class OperationCancellationTests
{
    private static readonly TextImportField Name = ImportField.Text("name");

    private static readonly ImportSchema Schema = new() { Fields = [Name] };

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "name", FieldName = "name" }],
    };

    private static readonly CancellationToken Cancelled = new(canceled: true);

    private static CsvCursor Cursor() =>
        new(new MemoryStream(Encoding.UTF8.GetBytes("name;x\na;1\nb;2\n"), writable: false), "t.csv");

    private static ImportRun<string?> Run(CsvCursor cursor, CancellationToken runToken = default) =>
        TabularImporter.Import(cursor, Plan, Schema, row => row[Name], cancellationToken: runToken);

    [Fact]
    public void ASessionReadStopsOnItsOwnToken()
    {
        using CsvCursor cursor = Cursor();
        ExtractionSession session = TabularExtractor.Start(cursor, Plan, Schema, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(session.ReadRow(TestContext.Current.CancellationToken));
        Assert.ThrowsAny<OperationCanceledException>(() => session.ReadRow(Cancelled));
    }

    [Fact]
    public void RowsStopOnTheirOwnToken()
    {
        using CsvCursor cursor = Cursor();
        using ImportRun<string?> run = Run(cursor, TestContext.Current.CancellationToken);

        Assert.ThrowsAny<OperationCanceledException>(() => run.ReadRows(Cancelled).ToList());
    }

    [Fact]
    public void ChunksStopOnTheirOwnToken()
    {
        using CsvCursor cursor = Cursor();
        using ImportRun<string?> run = Run(cursor, TestContext.Current.CancellationToken);

        Assert.ThrowsAny<OperationCanceledException>(() => run.ReadChunks(10, Cancelled).ToList());
    }

    [Fact]
    public void AllStopsOnItsOwnToken()
    {
        using CsvCursor cursor = Cursor();
        using ImportRun<string?> run = Run(cursor, TestContext.Current.CancellationToken);

        Assert.ThrowsAny<OperationCanceledException>(() => run.ReadAll(cancellationToken: Cancelled));
    }

    [Fact]
    public void TheRunsTokenStillStopsAReadGivenATokenOfItsOwn()
    {
        using CancellationTokenSource source = new();
        using CsvCursor cursor = Cursor();
        using ImportRun<string?> run = Run(cursor, source.Token);

        using IEnumerator<ImportOutcome<string?>> rows = run.ReadRows(TestContext.Current.CancellationToken).GetEnumerator();

        Assert.True(rows.MoveNext());
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => rows.MoveNext());
    }
}
