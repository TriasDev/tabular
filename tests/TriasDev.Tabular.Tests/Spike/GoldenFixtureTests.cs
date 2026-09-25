using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;
using Xunit;

namespace TriasDev.Tabular.Tests.Spike;

/// <summary>
/// Holds the reader to the sixteen behaviours the fixtures pin.
/// </summary>
/// <remarks>
/// During the spike these fixtures fed a report rather than assertions, because a candidate failing
/// one was the measurement being taken. That is over: the choice is recorded in ADR-0001, so from
/// here a disagreement is a regression and reads as one.
/// </remarks>
public sealed class GoldenFixtureTests
{
    public static TheoryData<GoldenFixture> XlsxFixtures => Load(XlsxGoldenFixtures.All);

    public static TheoryData<GoldenFixture> CsvFixtures => Load(CsvGoldenFixtures.All);

    private static TheoryData<GoldenFixture> Load(IReadOnlyList<GoldenFixture> fixtures)
    {
        TheoryData<GoldenFixture> data = [];

        foreach (GoldenFixture fixture in fixtures)
        {
            data.Add(fixture);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(XlsxFixtures))]
    public void Xlsx(GoldenFixture fixture)
    {
        using MemoryStream stream = new(fixture.Content, writable: false);
        using XlsxCursor cursor = new(stream);

        AssertReads(cursor, fixture);
    }

    [Theory]
    [MemberData(nameof(CsvFixtures))]
    public void Csv(GoldenFixture fixture)
    {
        using MemoryStream stream = new(fixture.Content, writable: false);
        using CsvCursor cursor = new(stream, fixture.FileName);

        AssertReads(cursor, fixture);
    }

    private static void AssertReads(ITabularCursor cursor, GoldenFixture fixture)
    {
        // Copied on the way out: the cursor reuses its row buffer, so anything kept has to be taken
        // now.
        List<string?[]> actual = [];

        while (cursor.ReadRow())
        {
            string?[] row = new string?[cursor.CurrentRow.Length];

            for (int i = 0; i < row.Length; i++)
            {
                row[i] = cursor.CurrentRow[i].AsText();
            }

            actual.Add(row);
        }

        Assert.Equal(fixture.ExpectedCells.Length, actual.Count);

        for (int r = 0; r < fixture.ExpectedCells.Length; r++)
        {
            string?[] expectedRow = fixture.ExpectedCells[r];
            string?[] actualRow = actual[r];

            Assert.Equal(expectedRow.Length, actualRow.Length);

            for (int c = 0; c < expectedRow.Length; c++)
            {
                Assert.Equal(expectedRow[c], actualRow[c]);
            }
        }
    }
}
