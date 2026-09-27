namespace TriasDev.Tabular;

/// <summary>What a run amounted to, as it stood when this was taken.</summary>
/// <remarks>
/// A snapshot: the run's own counters keep moving while it reads, and a summary taken before the
/// end keeps the numbers it had. Take it again, or take the one an <see cref="ImportResult{T}"/>
/// carries, for the final count.
/// </remarks>
public sealed record ExtractionSummary
{
    /// <summary>Data rows the reader saw, including those skipped and those that failed.</summary>
    public int RowsRead { get; init; }

    /// <summary>Rows that produced values.</summary>
    public int RowsProduced { get; init; }

    /// <summary>Rows whose mapped cells were all empty.</summary>
    public int RowsSkipped { get; init; }

    /// <summary>
    /// How many of the skipped rows held a value, just not in any mapped column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A subset of <see cref="RowsSkipped"/>, and the part of it worth looking at. A row that is
    /// blank from end to end is padding — a spreadsheet accumulates it below the data as a matter of
    /// course. A row carrying an internal note, or a comment in a column nobody mapped, is a record
    /// the file contains and the import silently drops.
    /// </para>
    /// <para>
    /// Counted rather than failed. Faulting such a row would bury a real import under errors about
    /// footnotes, and skipping it without a word is the worse mistake: data disappearing quietly is
    /// harder to notice than a number that does not add up. This is the number that lets whoever
    /// uploaded the file see that a hundred of their rows went nowhere.
    /// </para>
    /// </remarks>
    public int RowsWithNothingMapped { get; init; }

    /// <summary>Rows that produced errors instead of values.</summary>
    public int RowsFailed { get; init; }

    /// <summary>Errors reported across every row.</summary>
    public int ErrorCount { get; init; }

    /// <summary>
    /// True when the run stopped at its error limit rather than at the end of the file.
    /// </summary>
    /// <remarks>
    /// The distinction a caller must not lose: a run that stopped early has not seen the rest of the
    /// file, so "no further errors" would be a claim nobody checked.
    /// </remarks>
    public bool StoppedEarly { get; init; }
}
