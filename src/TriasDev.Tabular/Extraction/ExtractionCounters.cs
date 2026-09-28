namespace TriasDev.Tabular;

/// <summary>The counts a run keeps as it reads; <see cref="Snapshot"/> is what a caller is given.</summary>
internal sealed class ExtractionCounters
{
    public int RowsRead { get; set; }

    public int RowsProduced { get; set; }

    public int RowsSkipped { get; set; }

    public int RowsWithNothingMapped { get; set; }

    public int RowsFailed { get; set; }

    public int ErrorCount { get; set; }

    public bool StoppedEarly { get; set; }

    public int OtherSeparatorDecimals { get; set; }

    public ExtractionSummary Snapshot() => new()
    {
        RowsRead = RowsRead,
        RowsProduced = RowsProduced,
        RowsSkipped = RowsSkipped,
        RowsWithNothingMapped = RowsWithNothingMapped,
        RowsFailed = RowsFailed,
        ErrorCount = ErrorCount,
        StoppedEarly = StoppedEarly,
        OtherSeparatorDecimals = OtherSeparatorDecimals,
    };
}
