namespace TriasDev.Tabular;

/// <summary>What to do about a file that does not import cleanly.</summary>
public enum ImportPolicy
{
    /// <summary>Import the rows that fit and report the rest.</summary>
    BestEffort,

    /// <summary>
    /// Import nothing unless everything fits.
    /// </summary>
    /// <remarks>
    /// The precheck refuses such a file outright, which is the point: discovering it row by row
    /// means discovering it after some rows have already been written.
    /// </remarks>
    AllOrNothing,
}
