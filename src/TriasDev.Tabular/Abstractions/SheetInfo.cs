namespace TriasDev.Tabular;

/// <summary>One sheet of a file, as the file names it.</summary>
public sealed record SheetInfo
{
    /// <summary>Position in the file, zero-based.</summary>
    public required int Index { get; init; }

    /// <summary>
    /// The sheet's name. A csv file has no sheets, so its single one is named after the file.
    /// </summary>
    public required string Name { get; init; }
}
