using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>A csv row is bounded by the width a spreadsheet opens, and a file up to it reads.</summary>
public sealed class CsvRowWidthTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // -- A csv has no width --------------------------------------------------------------------

    [Fact]
    public void RefusesARowWiderThanTheFormatPeopleOpenItIn()
    {
        // The cheapest attack there is: no quoting, no encoding, no structure to get right. Measured
        // before the ceiling existed, 1.9 MB of commas became two million cells and 90 MB of heap
        // inside a single read.
        byte[] commas = new byte[20_000];
        Array.Fill(commas, (byte)';');

        using MemoryStream stream = new(commas, writable: false);
        using CsvCursor cursor = new(stream, "bomb.csv");

        TabularLimitException failure = Assert.Throws<TabularLimitException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.Contains("16384", failure.Message);
    }

    [Fact]
    public void ReadsAFileAsWideAsTheCeilingAllows()
    {
        // The bound has to be reachable from below, or it is refusing ordinary files.
        string row = string.Join(';', Enumerable.Range(0, 16_384).Select(i => i.ToString()));

        using MemoryStream stream = new(Utf8NoBom.GetBytes(row), writable: false);
        using CsvCursor cursor = new(stream, "wide.csv");

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal(16_384, cursor.CurrentRow.Length);
    }
}
