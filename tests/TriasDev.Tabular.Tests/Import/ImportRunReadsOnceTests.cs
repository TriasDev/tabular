using System.Collections;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>A run reads its file once, through one of three reads that each take a token, and its summary holds still.</summary>
public sealed class ImportRunReadsOnceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly TextImportField Name = ImportField.Text("name");

    private static readonly ImportSchema Schema = new() { Fields = [Name] };

    private static readonly MappingPlan Plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "name", Header = "name" }] };

    private static ImportRun<string?> Start(int rows = 3) =>
        TabularImporter.Import(
            new MemoryStream(Encoding.UTF8.GetBytes("name\n" + string.Concat(Enumerable.Range(1, rows).Select(i => $"n{i}\n")))),
            "t.csv", Plan, Schema, row => row[Name], cancellationToken: Token);

    public static TheoryData<string, string> ReadPairs => new()
    {
        { "rows", "rows" }, { "rows", "chunks" }, { "rows", "all" },
        { "chunks", "rows" }, { "all", "all" }, { "all", "chunks" },
    };

    private static void Read(ImportRun<string?> run, string how)
    {
        switch (how)
        {
            case "rows":
                _ = run.ReadRows(Token).ToList();
                break;
            case "chunks":
                _ = run.ReadChunks(2, Token).ToList();
                break;
            default:
                _ = run.ReadAll(cancellationToken: Token);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(ReadPairs))]
    public void RefusesASecondRead(string first, string second)
    {
        using ImportRun<string?> run = Start();

        Read(run, first);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Read(run, second));
        Assert.Contains("once", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CountsAReadAsStartedOnlyOnceItIsEnumerated()
    {
        // ReadRows is lazy: asking for it and never enumerating it reads nothing, so it is no read.
        using ImportRun<string?> run = Start();

        _ = run.ReadRows(Token);

        Assert.Equal(3, run.ReadAll(cancellationToken: Token).Items.Count);
    }

    [Fact]
    public void GivesASummaryThatDoesNotChangeInTheCallersHands()
    {
        using ImportRun<string?> run = Start();

        ExtractionSummary before = run.Summary;
        ImportResult<string?> result = run.ReadAll(cancellationToken: Token);

        Assert.Equal(0, before.RowsProduced);
        Assert.Equal(3, run.Summary.RowsProduced);
        Assert.Equal(3, result.Summary.RowsProduced);
    }

    [Fact]
    public void StillReportsTheRowsReadAfterBeingDisposedMidway()
    {
        ImportRun<string?> run = Start(rows: 10);

        using (IEnumerator<ImportOutcome<string?>> rows = run.ReadRows(Token).GetEnumerator())
        {
            Assert.True(rows.MoveNext());
            Assert.True(rows.MoveNext());
        }

        run.Dispose();

        Assert.Equal(2, run.Summary.RowsProduced);
    }

    [Fact]
    public void IsNotItselfASequence()
    {
        Assert.False(typeof(IEnumerable).IsAssignableFrom(typeof(ImportRun<string?>)));
    }
}
