namespace TriasDev.Tabular;

/// <summary>What a user decided about how to read a file.</summary>
public sealed record MappingPlan
{
    /// <summary>Which sheet to read.</summary>
    public int SheetIndex { get; init; }

    /// <summary>
    /// Which spreadsheet row carries the headers, zero-based: 2 is the row a person calls row 3.
    /// Counted as <see cref="AnalysisOptions.HeaderRowIndex"/> counts it.
    /// </summary>
    /// <remarks>
    /// Analysis always treats the first row as the header, because guessing otherwise produces a
    /// wrong answer nobody checks. This is where a user says the guess was wrong — a file with a
    /// title line above its table being the ordinary case.
    /// </remarks>
    public int HeaderRowIndex { get; init; }

    /// <summary>
    /// The culture numbers and dates are read under, or null for the invariant one.
    /// </summary>
    /// <remarks>
    /// Explicit here, where analysis only ever proposed. A frontend fills in what analysis suggested
    /// and a user may overrule it, which is the whole point of separating the two.
    /// </remarks>
    public string? Culture { get; init; }

    /// <summary>What each mapped column feeds.</summary>
    public required IReadOnlyList<ColumnBinding> Bindings { get; init; }
}
