using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The supported subset of Excel format codes: parsed into what both workbook formats can state.</summary>
public sealed class FormatCodeTests
{
    [Theory]
    [InlineData("0", false, 1, 0, 0, false, "", "")]
    [InlineData("0.00", false, 1, 2, 2, false, "", "")]
    [InlineData("#,##0", true, 1, 0, 0, false, "", "")]
    [InlineData("#,##0.00", true, 1, 2, 2, false, "", "")]
    [InlineData("0.0#", false, 1, 2, 1, false, "", "")]
    [InlineData("#.##", false, 0, 2, 0, false, "", "")]
    [InlineData("0%", false, 1, 0, 0, true, "", "")]
    [InlineData("0.0%", false, 1, 1, 1, true, "", "")]
    [InlineData("\"€ \"#,##0.00", true, 1, 2, 2, false, "€ ", "")]
    [InlineData("0\" kg\"", false, 1, 0, 0, false, "", " kg")]
    public void ParsesANumberFormat(string code, bool grouping, int minInteger, int decimals, int minDecimals, bool percent, string prefix, string suffix)
    {
        NumberFormat format = NumberFormat.Parse(code);

        Assert.Equal(code, format.Code);
        Assert.Equal(grouping, format.Grouping);
        Assert.Equal(minInteger, format.MinIntegerDigits);
        Assert.Equal(decimals, format.DecimalPlaces);
        Assert.Equal(minDecimals, format.MinDecimalPlaces);
        Assert.Equal(percent, format.Percent);
        Assert.Equal(prefix, format.Prefix);
        Assert.Equal(suffix, format.Suffix);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("General", "'G'")]
    [InlineData("0;-0", "';'")]
    [InlineData("[Red]0", "'['")]
    [InlineData("0.00E+00", "'E'")]
    [InlineData("# ?/?", "' '")]
    [InlineData("@", "'@'")]
    [InlineData("0#", "a # after a 0")]
    [InlineData("0.#0", "a 0 after a #")]
    [InlineData("0.", "decimal point")]
    [InlineData("0,", "comma")]
    [InlineData(",0", "comma")]
    [InlineData("\"open", "quote")]
    [InlineData("0.0000000000000000000000000000000", "30")]
    public void RefusesANumberFormatOutsideTheSubset(string code, string named)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => NumberFormat.Parse(code));
        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NumberFormatsWithTheSameCodeAreEqual() =>
        Assert.Equal(NumberFormat.Parse("#,##0.00"), NumberFormat.Parse("#,##0.00"));

    [Theory]
    [InlineData("dd/mm/yyyy", "dd\\/mm\\/yyyy", "Day,Literal,Month,Literal,Year")]
    [InlineData("yyyy-mm-dd hh:mm:ss", "yyyy\\-mm\\-dd\\ hh:mm:ss", "Year,Literal,Month,Literal,Day,Literal,Hour,Literal,Minute,Literal,Second")]
    [InlineData("d.m.yy", "d\\.m\\.yy", "Day,Literal,Month,Literal,Year")]
    [InlineData("hh:mm", "hh:mm", "Hour,Literal,Minute")]
    [InlineData("mm:ss", "mm:ss", "Minute,Literal,Second")]
    [InlineData("h \"h\" m", "h\\ \\h\\ m", "Hour,Literal,Minute")]
    [InlineData("DD/MM/YYYY", "dd\\/mm\\/yyyy", "Day,Literal,Month,Literal,Year")]
    [InlineData("yyyy\\Wdd", "yyyy\\Wdd", "Year,Literal,Day")]
    public void ParsesADateFormat(string code, string canonical, string kinds)
    {
        DateFormat format = DateFormat.Parse(code);

        Assert.Equal(canonical, format.Code);
        Assert.Equal(kinds, string.Join(',', format.Parts.Select(p => p.Kind)));
    }

    [Fact]
    public void MonthAndMinuteFollowExcelsRule()
    {
        DateFormat format = DateFormat.Parse("mm/dd hh:mm");

        Assert.Equal(DatePartKind.Month, format.Parts[0].Kind);
        Assert.Equal(DatePartKind.Minute, format.Parts[^1].Kind);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("mmm yyyy", "mmm")]
    [InlineData("dddd", "dddd")]
    [InlineData("yyy", "yyy")]
    [InlineData("hh:mm AM/PM", "'A'")]
    [InlineData("[h]:mm", "'['")]
    [InlineData("hh:mm:ss.000", "'0'")]
    [InlineData("\"only text\"", "no date or time part")]
    [InlineData("dd\\", "backslash")]
    [InlineData("dd\"open", "quote")]
    public void RefusesADateFormatOutsideTheSubset(string code, string named)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => DateFormat.Parse(code));
        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DateFormatsCompareByTheirCode()
    {
        Assert.Equal(DateFormat.Parse("dd/mm/yyyy"), DateFormat.Parse("DD/MM/YYYY"));
        Assert.Equal(DateFormat.Parse("dd/mm/yyyy").GetHashCode(), DateFormat.Parse("DD/MM/YYYY").GetHashCode());
        Assert.NotEqual(DateFormat.Parse("dd/mm/yyyy"), DateFormat.Parse("dd.mm.yyyy"));
    }

    [Fact]
    public void ALiteralWithAForbiddenCharacterIsRefused() =>
        Assert.Throws<ArgumentException>(() => NumberFormat.Parse("0\"\u0007\""));

    [Fact]
    public void AnEscapedCharacterThatIsForbiddenIsRefused() =>
        Assert.Throws<ArgumentException>(() => DateFormat.Parse("dd\\" + "\u0007"));

    [Fact]
    public void AStyleCarriesBothFormats()
    {
        CellStyle style = new() { Number = NumberFormat.Parse("0.00"), Date = DateFormat.Parse("dd/mm/yyyy") };

        Assert.Equal(style, new CellStyle { Number = NumberFormat.Parse("0.00"), Date = DateFormat.Parse("dd/mm/yyyy") });
    }
}
