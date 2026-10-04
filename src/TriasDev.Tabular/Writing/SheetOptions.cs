namespace TriasDev.Tabular;

/// <summary>
/// How a sheet is laid out beyond its values: a style for its header row, rows and columns frozen
/// in view, an auto-filter on its header. Csv ignores all of it.
/// </summary>
public sealed record SheetOptions
{
    /// <summary>The options a sheet has when none are given.</summary>
    internal static readonly SheetOptions Default = new();

    /// <summary>The header row's style; null leaves it unstyled.</summary>
    public CellStyle? HeaderStyle { get; init; }

    /// <summary>How many rows from the top stay in view while scrolling — 1 keeps the header. 0 freezes none.</summary>
    public int FreezeRows { get; init; }

    /// <summary>How many columns from the left stay in view while scrolling. 0 freezes none.</summary>
    public int FreezeColumns { get; init; }

    /// <summary>An auto-filter on the header row, covering every row written.</summary>
    public bool AutoFilter { get; init; }
}
