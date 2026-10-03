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

    [Fact]
    public void RefusesANameAlreadyTakenIgnoringCase()
    {
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase) { "Portfolios" };

        Assert.NotNull(SheetNames.Problem("PORTFOLIOS", taken));
    }
}
