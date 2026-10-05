
namespace TriasDev.Tabular;

/// <summary>
/// What was measured about a column, over every row of the file.
/// </summary>
/// <remarks>
/// Facts, not conclusions. Everything here was counted; nothing was inferred. The inference lives in
/// the hypotheses beside them, and keeping the two in different types is what stops one being
/// mistaken for the other.
/// </remarks>
public sealed record ColumnFacts
{
    /// <summary>The column's position, zero-based.</summary>
    public required int Index { get; init; }

    /// <summary>The header as the file holds it. Empty when the header cell is.</summary>
    public required string Header { get; init; }

    /// <summary>Rows whose cell in this column holds nothing.</summary>
    public required int EmptyCount { get; init; }

    /// <summary>Rows whose cell in this column holds something.</summary>
    public required int NonEmptyCount { get; init; }

    /// <summary>Shortest non-empty value, or null when there is none.</summary>
    public required int? MinLength { get; init; }

    /// <summary>Longest non-empty value, or null when there is none.</summary>
    public required int? MaxLength { get; init; }

    /// <summary>Non-empty values that read as true or false.</summary>
    public required int BooleanCount { get; init; }

    /// <summary>How the values fare under each culture tried.</summary>
    public required IReadOnlyList<CultureParseCounts> ParseCounts { get; init => field = Equatable.List(value); }

    /// <summary>Smallest numeric value under the culture that parsed most of them.</summary>
    public required decimal? MinNumeric { get; init; }

    /// <summary>Largest numeric value under the culture that parsed most of them.</summary>
    public required decimal? MaxNumeric { get; init; }

    /// <summary>Earliest date under the culture that parsed most of them.</summary>
    public required DateTime? MinDate { get; init; }

    /// <summary>Latest date under the culture that parsed most of them.</summary>
    public required DateTime? MaxDate { get; init; }

    /// <summary>
    /// How many kinds the file itself declared, for a format that declares them.
    /// </summary>
    /// <remarks>
    /// A csv file declares nothing, so every non-empty cell is text here and the column says nothing
    /// beyond that. A workbook does declare, and a column of real dates is worth distinguishing from
    /// a column of text that looks like dates.
    /// </remarks>
    public required IReadOnlyDictionary<RawCellKind, int> NativeKinds { get; init => field = Equatable.Dictionary(value); }

    /// <summary>How many different values the column holds.</summary>
    public required int DistinctCount { get; init; }

    /// <summary>
    /// False when the tracking budget ran out, making <see cref="DistinctCount"/> a lower bound.
    /// </summary>
    public required bool DistinctCountIsExact { get; init; }

    /// <summary>
    /// Whether this column can identify its rows, or null when that could not be decided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question a caller is really asking of the distinct count. An empty cell answers it as
    /// surely as a repeated value does: a column that identifies a record must do so for every
    /// record, and a row with nothing in it is one the column cannot name.
    /// </para>
    /// <para>
    /// Null rather than false where nothing was measured — a column with no values at all, or one
    /// whose distinct count stopped being exact when the budget ran out. An explicit "not determined"
    /// is worth more than a confident wrong answer, and the two causes read differently to whoever
    /// is told about them.
    /// </para>
    /// </remarks>
    public required bool? IsUnique { get; init; }

    /// <summary>
    /// Whether the cultures that read the most values as dates read some row as different dates.
    /// </summary>
    /// <remarks>
    /// True for a column whose every day is twelve or less, written <c>11.01.2018</c> or
    /// <c>11/01/2018</c>: German reads it as 11 January, American as 1 November, and every value fits
    /// both. The hypotheses rank the culture whose separator the values use first, and the dates in
    /// these facts follow it, but that is a preference, not evidence — this says so, so a screen can
    /// ask rather than guess (#58).
    /// </remarks>
    public bool DateReadingsDisagree { get; init; }

    /// <summary>
    /// Whether the cultures that read the most values as numbers read some row as different numbers.
    /// </summary>
    /// <remarks>
    /// True for a column whose every value has three digits after one separator, <c>48.137</c>: a
    /// decimal under English, the grouped integer 48137 under German, and every value fits both. The
    /// hypotheses rank the reading the evidence favours first — the sheet's other columns, then a
    /// csv's delimiter, then the decimal — and the extremes in these facts follow it, but that is a
    /// preference, not proof; this says so, so a screen can ask rather than guess (#61).
    /// </remarks>
    public bool NumberReadingsDisagree { get; init; }

    /// <summary>
    /// A bounded sample of values with how often each was seen.
    /// </summary>
    /// <remarks>
    /// A sample, not a ranking. Naming the genuinely most frequent values would mean counting every
    /// distinct value in the column, which is exactly the unbounded memory the budget exists to
    /// prevent. These are the most frequent among those tracked, which is enough to let someone look
    /// into a column and recognise what is in it.
    /// </remarks>
    public required IReadOnlyList<ValueFrequency> DistinctSamples { get; init => field = Equatable.List(value); }

    /// <summary>The first non-empty values, in file order.</summary>
    public required IReadOnlyList<string> Samples { get; init => field = Equatable.List(value); }

    /// <summary>
    /// The column's distinct values, where there were few enough of them to keep.
    /// </summary>
    /// <remarks>
    /// Empty when the column holds more distinct values than were worth keeping — see
    /// <see cref="DistinctValuesAreComplete"/>, which is the only thing that makes this set safe to
    /// draw a conclusion from.
    /// </remarks>
    public required IReadOnlyList<string> DistinctValues { get; init => field = Equatable.List(value); }

    /// <summary>
    /// Whether <see cref="DistinctValues"/> is every value the column holds.
    /// </summary>
    /// <remarks>
    /// The difference between "no value in this column is a country code" and "none of the ones I
    /// kept is". Only the first is an answer, and only this flag distinguishes them. False also when
    /// the distinct budget ran out, since a set cannot be complete when the count behind it is not.
    /// </remarks>
    public required bool DistinctValuesAreComplete { get; init; }

    /// <summary>
    /// Every value of <see cref="DistinctValues"/> with the number of rows holding it, in the same
    /// order; empty whenever <see cref="DistinctValuesAreComplete"/> is false.
    /// </summary>
    /// <remarks>
    /// A tally, not a sample: the counts add up to <see cref="NonEmptyCount"/>. What lets the precheck
    /// say how many rows a rule fails rather than how many values, and a screen say "3 rows hold a
    /// material that is not allowed" (#63). Values are counted as written, trimmed, so <c>Wood</c> and
    /// <c>wood</c> are two entries a case-insensitive caller adds up.
    /// </remarks>
    public IReadOnlyList<ValueFrequency> DistinctValueCounts { get; init => field = Equatable.List(value); } = Equatable.Empty<ValueFrequency>();

    /// <summary>
    /// How many rows hold a value from the set, or null when the column's distinct values were too
    /// many to keep and the answer would be a guess.
    /// </summary>
    /// <remarks>
    /// Asked before anything is mapped — which column is the country code? — of every column in turn.
    /// Answered from <see cref="DistinctValueCounts"/>, so it costs nothing per row: the counting was
    /// done while analysing.
    /// </remarks>
    /// <param name="allowed">The set, compared as the constraint compares it.</param>
    public int? RowsWithValueIn(FieldConstraint.AllowedValues allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        return DistinctValuesAreComplete
            ? DistinctValueCounts.Where(v => allowed.Contains(v.Value)).Sum(v => v.Count)
            : null;
    }
}
