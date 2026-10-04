namespace TriasDev.Tabular;

/// <summary>How a cell's text looks: its colour, bold, italic. Family and size stay the format's default.</summary>
public sealed record CellFont
{
    /// <summary>The text colour; null keeps the default (black).</summary>
    public CellColor? Color { get; init; }

    /// <summary>Bold text.</summary>
    public bool Bold { get; init; }

    /// <summary>Italic text.</summary>
    public bool Italic { get; init; }
}
