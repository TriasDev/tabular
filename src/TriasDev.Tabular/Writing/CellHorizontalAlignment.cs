namespace TriasDev.Tabular;

/// <summary>Where a cell's content sits across the cell.</summary>
/// <remarks>Open to new members, as <see cref="TabularFormat"/> is: a switch over it needs a default arm.</remarks>
public enum CellHorizontalAlignment
{
    /// <summary>The format's default: text left, numbers and dates right.</summary>
    General,

    /// <summary>Left.</summary>
    Left,

    /// <summary>Centred.</summary>
    Center,

    /// <summary>Right.</summary>
    Right,
}
