namespace TriasDev.Tabular;

/// <summary>
/// What a cell carries as the file itself presents it, before anything is inferred about it.
/// </summary>
/// <remarks>
/// A csv file only ever produces <see cref="Empty"/> and <see cref="Text"/>: it has no types, and
/// deciding that a column of text is really a column of dates belongs to the analyzer, not here.
/// </remarks>
public enum RawCellKind : byte
{
    /// <summary>No value: the cell is absent, or holds only whitespace.</summary>
    Empty,

    /// <summary>Text, taken verbatim.</summary>
    Text,

    /// <summary>A number the file declares as such.</summary>
    Number,

    /// <summary>A date the file declares as such, through its number format.</summary>
    Date,

    /// <summary>A boolean the file declares as such.</summary>
    Boolean,

    /// <summary>An error value such as <c>#N/A</c>, carried as the text the file holds.</summary>
    Error,
}
