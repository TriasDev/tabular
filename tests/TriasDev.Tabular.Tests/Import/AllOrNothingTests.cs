using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// <see cref="ImportPolicy.AllOrNothing"/> is honoured by the run, not only by the precheck.
/// </summary>
public sealed class AllOrNothingTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly IntegerField Count = ImportField.Integer("count").Require();

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "count", TargetFieldName = "count" }],
    };

    private static ImportRun<long> Run(ImportPolicy policy, string csv) =>
        TabularImporter.Import(
            new CsvCursor(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv"),
            Plan,
            new TargetSchema { Fields = [Count], Policy = policy },
            row => row[Count]!.Value,
            cancellationToken: TestContext.Current.CancellationToken);

    private const string OneBadRow = "count;x\n1;a\n2;a\nthree;a\n4;a\n5;a\n";

    [Fact]
    public void StopsAtTheFirstFailingRow()
    {
        // A streaming run cannot take back rows it already handed out, but it can refuse to go on:
        // the caller learns at the first failure, not after reading the rest of the file.
        using ImportRun<long> run = Run(ImportPolicy.AllOrNothing, OneBadRow);

        List<ImportOutcome<long>> outcomes = [.. run];

        Assert.Equal(3, outcomes.Count);
        Assert.True(outcomes[^1].HasErrors);
        Assert.True(run.Summary.StoppedEarly);
    }

    [Fact]
    public void ReturnsNothingFromAllWhenAnyRowFails()
    {
        using ImportRun<long> run = Run(ImportPolicy.AllOrNothing, OneBadRow);

        ImportResult<long> result = run.All();

        Assert.Empty(result.Items);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void ReturnsEverythingFromAllWhenEveryRowFits()
    {
        using ImportRun<long> run = Run(ImportPolicy.AllOrNothing, "count;x\n1;a\n2;a\n");

        Assert.Equal([1L, 2L], run.All().Items);
    }

    [Fact]
    public void CarriesNoItemsInTheChunkThatHoldsTheFailure()
    {
        using ImportRun<long> run = Run(ImportPolicy.AllOrNothing, OneBadRow);

        List<ImportChunk<long>> chunks = [.. run.InChunks(10)];

        ImportChunk<long> chunk = Assert.Single(chunks);
        Assert.Empty(chunk.Items);
        Assert.Single(chunk.Errors);
    }

    [Fact]
    public void KeepsImportingPastAFailureUnderBestEffort()
    {
        using ImportRun<long> run = Run(ImportPolicy.BestEffort, OneBadRow);

        ImportResult<long> result = run.All();

        Assert.Equal([1L, 2L, 4L, 5L], result.Items);
        Assert.False(run.Summary.StoppedEarly);
    }
}
