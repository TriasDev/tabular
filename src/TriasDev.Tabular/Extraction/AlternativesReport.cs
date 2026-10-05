namespace TriasDev.Tabular;

/// <summary>What a run found about one set of alternatives, over the rows that produced values.</summary>
/// <remarks>
/// Rows that failed are left out: they are not imported, and the run's errors already say why. So
/// the percentages a screen derives from <see cref="RowsJudged"/> describe the rows that will arrive.
/// </remarks>
public sealed record AlternativesReport
{
    /// <summary>The set's name.</summary>
    public required string Name { get; init; }

    /// <summary>Rows that produced values, every one of them judged.</summary>
    public required int RowsJudged { get; init; }

    /// <summary>Per group, in the set's order.</summary>
    public required IReadOnlyList<AlternativeGroupReport> Groups { get; init => field = Equatable.List(value); }

    /// <summary>Rows no group made usable.</summary>
    public required int Unresolved { get; init; }

    /// <summary>The first of them, by spreadsheet row number, up to <see cref="ExtractionOptions.MaxReportedRows"/>.</summary>
    public required IReadOnlyList<int> UnresolvedRows { get; init => field = Equatable.List(value); }

    /// <summary>Whether <see cref="UnresolvedRows"/> holds every one.</summary>
    public required bool UnresolvedRowsComplete { get; init; }
}

/// <summary>What a run found about one group of a set of alternatives.</summary>
public sealed record AlternativeGroupReport
{
    /// <summary>The group's name.</summary>
    public required string Name { get; init; }

    /// <summary>Rows where every earlier group fell short, so this one was judged; every row for the first group.</summary>
    public required int RowsNeeding { get; init; }

    /// <summary>Rows this group located.</summary>
    public required int Won { get; init; }

    /// <summary>
    /// The deepest level the mapping can reach: a level with no mapped field ends the ladder for every row.
    /// </summary>
    /// <remarks>
    /// What <see cref="Incomplete"/> is measured against, so an unmapped house number does not turn
    /// every row into a warning — the mapping says that once, in the precheck.
    /// </remarks>
    public required int MaxReachableLevel { get; init; }

    /// <summary>Rows needing the group, by how many levels they reach: index 0 to the level count.</summary>
    public required IReadOnlyList<int> RowsAtLevel { get; init => field = Equatable.List(value); }

    /// <summary>Rows needing the group that stop short of <see cref="MaxReachableLevel"/>.</summary>
    public required int Incomplete { get; init; }

    /// <summary>Rows needing the group that write nothing into any of its fields.</summary>
    public required int Empty { get; init; }

    /// <summary>The first incomplete rows, by spreadsheet row number, up to <see cref="ExtractionOptions.MaxReportedRows"/>.</summary>
    public required IReadOnlyList<int> IncompleteRows { get; init => field = Equatable.List(value); }

    /// <summary>Whether <see cref="IncompleteRows"/> holds every one.</summary>
    public required bool IncompleteRowsComplete { get; init; }

    /// <summary>
    /// Values that would have failed validation, in rows where an earlier group won and this one was
    /// not judged; kept as the file holds them.
    /// </summary>
    public required int IgnoredInvalidValues { get; init; }
}
