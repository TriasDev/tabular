namespace TriasDev.Tabular;

/// <summary>The ceiling every row number in the library shares, for the cursors that count their rows.</summary>
internal static class RowNumbers
{
    /// <summary>
    /// The refusal of a row whose number would pass <paramref name="maximum"/>: an int that went on
    /// counting would wrap to a negative row number and point every error at nothing.
    /// </summary>
    public static TabularLimitException Exceeded(int maximum) =>
        new("MaxRows", maximum, $"The sheet has more rows than the largest row number, {maximum}, can count.");
}
