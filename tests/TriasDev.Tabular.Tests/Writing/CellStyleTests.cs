using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The style model: colours, fonts, borders and styles compare by value.</summary>
public sealed class CellStyleTests
{
    [Theory]
    [InlineData(0x000000, "#000000")]
    [InlineData(0xF8696B, "#F8696B")]
    [InlineData(0xFFFFFF, "#FFFFFF")]
    public void AColourIsTwentyFourBitRgb(int rgb, string text)
    {
        CellColor color = CellColor.FromRgb(rgb);

        Assert.Equal(rgb, color.Rgb);
        Assert.Equal(text, color.ToString());
        Assert.Equal(color, CellColor.Parse(text));
        Assert.Equal(color, CellColor.Parse(text.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x1000000)]
    public void AColourOutsideRgbIsRefused(int rgb) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CellColor.FromRgb(rgb));

    [Theory]
    [InlineData("F8696B")]
    [InlineData("#F8696")]
    [InlineData("#F8696B0")]
    [InlineData("#G8696B")]
    [InlineData("")]
    public void AColourTextOtherThanHashAndSixHexDigitsIsRefused(string text) =>
        Assert.Throws<FormatException>(() => CellColor.Parse(text));

    [Fact]
    public void StylesWithTheSameSettingsAreEqual()
    {
        CellStyle a = new() { Fill = CellColor.FromRgb(0xFF0000), Font = new CellFont { Bold = true, Color = CellColor.FromRgb(0xFFFFFF) }, Border = CellBorder.Thin(CellColor.FromRgb(0x808080)), Horizontal = CellHorizontalAlignment.Center, Wrap = true };
        CellStyle b = new() { Fill = CellColor.FromRgb(0xFF0000), Font = new CellFont { Bold = true, Color = CellColor.FromRgb(0xFFFFFF) }, Border = CellBorder.Thin(CellColor.FromRgb(0x808080)), Horizontal = CellHorizontalAlignment.Center, Wrap = true };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Wrap = false });
    }

    [Fact]
    public void AnEmptyStyleChangesNothing()
    {
        CellStyle style = new();

        Assert.Null(style.Fill);
        Assert.Null(style.Font);
        Assert.Null(style.Border);
        Assert.Equal(CellHorizontalAlignment.General, style.Horizontal);
        Assert.False(style.Wrap);
    }

    [Fact]
    public void TheDefaultStyleIdIsTheUnstyledCell() => Assert.Equal(default, new StyleId());
}
