using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>Excel's rules for a sheet name, which both workbook formats follow.</summary>
public sealed class SheetNamesTests
{
    [Theory]
    [InlineData("Portfolios")]
    [InlineData("R&D <2026>")]
    [InlineData("a")]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("Grüße 'quoted' inside")]
    public void AcceptsANameExcelAccepts(string name)
    {
        Assert.Null(SheetNames.Problem(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678901234567890123456789012")]
    [InlineData("a[b")]
    [InlineData("a]b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("'leading")]
    [InlineData("trailing'")]
    [InlineData("History")]
    [InlineData("history")]
    [InlineData("bad\u0001")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void RefusesANameExcelRefuses(string name)
    {
        Assert.NotNull(SheetNames.Problem(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A csv file writes no sheet name, so it holds none to a workbook's rules: refusing a name the
    /// file never contains would refuse a csv for a reason that belongs to another format.
    /// </summary>
    [Theory]
    [InlineData("Q1/Q2: [draft]*")]
    [InlineData("History")]
    [InlineData("'quoted'")]
    [InlineData("a name far longer than the thirty-one characters a workbook allows")]
    public async Task ACsvTakesAnyNameAndWritesNoneOfIt(string name)
    {
        Assert.Equal(await WriteCsv("data"), await WriteCsv(name));
    }

    private static async Task<byte[]> WriteCsv(string sheetName)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv))
        {
            writer.BeginSheet(sheetName, [new("v")]);
            writer.BeginRow();
            writer.Write("x");
            writer.EndRow();
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        return target.ToArray();
    }

    [Fact]
    public void RefusesANameAlreadyTakenIgnoringCase()
    {
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase) { "Portfolios" };

        Assert.NotNull(SheetNames.Problem("PORTFOLIOS", taken));
    }
}
