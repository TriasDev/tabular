namespace TriasDev.Tabular.Analysis;

/// <summary>How far an analysis has got, as reported while it runs.</summary>
/// <remarks>
/// Reported on a stride — every <see cref="AnalysisOptions.ProgressInterval"/> data rows — and once
/// more when the analysis is complete, never per row.
/// </remarks>
public sealed record AnalysisProgress
{
    /// <summary>The sheet being read.</summary>
    public required int SheetIndex { get; init; }

    /// <summary>The name of the sheet being read.</summary>
    public required string SheetName { get; init; }

    /// <summary>How many sheets the file holds.</summary>
    public required int SheetCount { get; init; }

    /// <summary>Data rows read so far, across every sheet; header rows and blank rows are not counted.</summary>
    public required long RowsRead { get; init; }

    /// <summary>
    /// How much of the file has been read, from 0 to 1, or null where the file's size cannot be known.
    /// </summary>
    /// <remarks>
    /// Taken from how much of the file the reader has consumed, not from a count of rows made in
    /// advance, so nothing has to read the file twice to have a denominator. It is exactly 1 in the
    /// final report.
    /// </remarks>
    public double? Fraction { get; init; }

    /// <summary>Whether this is the report sent when the analysis finished.</summary>
    public bool IsComplete { get; init; }
}
