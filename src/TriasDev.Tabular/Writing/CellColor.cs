using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>A colour for a cell's fill, font or border: 24-bit RGB.</summary>
public readonly record struct CellColor
{
    private CellColor(int rgb) => Rgb = rgb;

    /// <summary>The colour as 0xRRGGBB.</summary>
    public int Rgb { get; }

    /// <summary>A colour from 0xRRGGBB.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rgb"/> is not between 0 and 0xFFFFFF.</exception>
    public static CellColor FromRgb(int rgb)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rgb);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rgb, 0xFFFFFF);
        return new CellColor(rgb);
    }

    /// <summary>A colour from <c>#RRGGBB</c>, hex digits in either case.</summary>
    /// <exception cref="FormatException">The text is not <c>#</c> and six hex digits.</exception>
    public static CellColor Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length != 7 || text[0] != '#' || !int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
        {
            throw new FormatException($"\"{text}\" is not a colour; write it as #RRGGBB.");
        }

        return new CellColor(rgb);
    }

    /// <summary>The colour as <c>#RRGGBB</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"#{Rgb:X6}");
}
