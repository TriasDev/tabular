using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>A blank row is padding, not data; a row with a value anywhere is data.</summary>
public sealed class BlankRowTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static FileProfile Profile(string csv)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return TabularAnalyzer.Analyze(cursor);
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
}
