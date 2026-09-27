using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>A column that first appears late is profiled for the rows it missed, at a cost that does not grow with them.</summary>
public sealed class LateColumnTests
{
    /// <summary>Profiles a file whose second column appears only in the last row.</summary>
    private static long AllocatedProfilingALateColumn(int rowsBefore)
    {
        StringBuilder file = new("a\n");

        for (int i = 0; i < rowsBefore; i++)
        {
            file.Append("x\n");
        }

        file.Append("x;LATE\n");

        string csv = file.ToString();

        Profile(csv);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Profile(csv);

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static FileProfile Profile(string csv, AnalysisOptions? options = null)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        return TabularAnalyzer.Analyze(cursor, options);
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void CostsTheSamePerLateColumnHoweverManyRowsItMissed()
    {
        // The backfill first did this once per missed row, which a file whose rows grow one column at
        // a time turns into the product of its rows and its columns. Measured as work rather than as
        // time: a timing test on a staircase file cannot see it, because that fixture is quadratic in
        // rows by construction and the defect only adds a constant factor to it. Here the two files
        // have the same number of cells and differ only in how far the late column has to reach back.
        long near = AllocatedProfilingALateColumn(rowsBefore: 50);
        long far = AllocatedProfilingALateColumn(rowsBefore: 5_000);

        Assert.True(
            far < near * 4,
            $"reaching back 5,000 rows cost {(double)far / near:F1}× what reaching back 50 did.");
    }

    [Fact]
    public void GivesALateColumnTheRowsItMissed()
    {
        // Its counts have to add up to the sheet's, or "no row leaves this column empty" is read as
        // "every row carries a value" by everything downstream.
        FileProfile profile = Profile("a\n1\n2\n3;LATE\n");

        ColumnFacts late = profile.Sheets[0].Columns[1].Facts;

        Assert.Equal(1, late.NonEmptyCount);
        Assert.Equal(2, late.EmptyCount);
        Assert.Equal(profile.Sheets[0].RowCount, late.NonEmptyCount + late.EmptyCount);
    }
}
