namespace TriasDev.Tabular;

/// <summary>A border on all four sides of a cell.</summary>
public sealed record CellBorder
{
    private CellBorder(CellColor color) => Color = color;

    /// <summary>The border's colour.</summary>
    public CellColor Color { get; }

    /// <summary>A thin line in a colour on all four sides.</summary>
    public static CellBorder Thin(CellColor color) => new(color);
}
