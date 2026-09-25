namespace TriasDev.Tabular;

/// <summary>
/// A reading a column could be given, with how well it fits and what does not fit it.
/// </summary>
/// <remarks>
/// <para>
/// A suggestion, never a decision. Extraction runs on a mapping a person confirmed, and a hypothesis
/// exists to save that person clicks and to show them the risk before they commit — not to choose
/// on their behalf.
/// </para>
/// <para>
/// Why that distinction is not academic: a column of <c>1,00 2,00 3,00</c> yields a decimal
/// hypothesis at full confidence, which is arithmetically perfect and practically useless, because
/// the column is an identifier. Only the facts beside it — every value distinct — say so, and only a
/// person knows what the column is for.
/// </para>
/// </remarks>
public sealed record TypeHypothesis
{
    /// <summary>The reading being proposed.</summary>
    public required ColumnType Type { get; init; }

    /// <summary>
    /// The culture the values were read under, or null where the reading does not depend on one —
    /// text, booleans, and anything the file itself declared.
    /// </summary>
    public string? Culture { get; init; }

    /// <summary>The share of present values this reading accounts for, from zero to one.</summary>
    public required double Confidence { get; init; }

    /// <summary>Values this reading accounts for.</summary>
    public required int MatchedCount { get; init; }

    /// <summary>Values it does not.</summary>
    public required int UnmatchedCount { get; init; }

    /// <summary>Where the values it does not account for stand, up to the configured limit.</summary>
    public required IReadOnlyList<ValueLocation> Outliers { get; init; }
}
