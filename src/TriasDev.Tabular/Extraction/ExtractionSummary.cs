namespace TriasDev.Tabular;

/// <summary>What a run amounted to, available once it has been read out.</summary>
public sealed class ExtractionSummary
{
    /// <summary>Data rows the reader saw, including those skipped and those that failed.</summary>
    public int RowsRead { get; internal set; }

    /// <summary>Rows that produced values.</summary>
    public int RowsProduced { get; internal set; }

    /// <summary>Rows whose mapped cells were all empty.</summary>
    public int RowsSkipped { get; internal set; }

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
    public int RowsWithNothingMapped { get; internal set; }

    /// <summary>Rows that produced errors instead of values.</summary>
    public int RowsFailed { get; internal set; }

    /// <summary>Errors reported across every row.</summary>
    public int ErrorCount { get; internal set; }

    /// <summary>
    /// True when the run stopped at its error limit rather than at the end of the file.
    /// </summary>
    /// <remarks>
    /// The distinction a caller must not lose: a run that stopped early has not seen the rest of the
    /// file, so "no further errors" would be a claim nobody checked.
    /// </remarks>
    public bool StoppedEarly { get; internal set; }
}
