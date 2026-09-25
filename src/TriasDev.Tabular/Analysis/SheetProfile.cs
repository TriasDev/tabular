namespace TriasDev.Tabular;

/// <summary>What is known about one sheet.</summary>
public sealed record SheetProfile
{
    /// <summary>Position in the file, zero-based.</summary>
    public required int Index { get; init; }

    /// <summary>The sheet's name, or the file's for a csv.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Rows of data, not counting the header.
    /// </summary>
    public required int RowCount { get; init; }

    /// <summary>One entry per column, in file order.</summary>
    public required IReadOnlyList<ColumnProfile> Columns { get; init; }

    /// <summary>
    /// The row the header was read from, zero-based.
    /// </summary>
    /// <remarks>
    /// Recorded because a mapping names one too, and a profile measured against a different one
    /// describes a different file: the real header row and everything above it were counted as data,
    /// so lengths, types and distinct values all include cells the import will never read. Carrying
    /// the number is what lets a precheck notice, rather than answer confidently about the wrong
    /// thing.
    /// </remarks>
    public required int HeaderRowIndex { get; init; }
}
