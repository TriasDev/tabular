using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>Whether a column identifies its rows, as the profile can and cannot tell.</summary>
public sealed class UniquenessTests
{
    private static FileProfile Profile(string csv)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return TabularAnalyzer.Analyze(cursor);
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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
}
