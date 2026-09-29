namespace TriasDev.Tabular;

/// <summary>How a column's values fare when read under one culture.</summary>
/// <remarks>
/// Kept per culture rather than collapsed into one verdict because the collapse is the interesting
/// part and belongs to whoever is looking. <c>1,00</c> is one under a German reading and a hundred
/// under an English one; both are arithmetically correct, and only a person who knows what the
/// column means can say which was intended.
/// </remarks>
public sealed record CultureParseCounts
{
    /// <summary>The culture's name; the empty string is the invariant culture, as in .NET.</summary>
    public required string Culture { get; init; }

    /// <summary>Non-empty values that read as a whole number.</summary>
    public required int Integer { get; init; }

    /// <summary>Non-empty values that read as a decimal number.</summary>
    public required int Decimal { get; init; }

    /// <summary>Non-empty values that read as a date.</summary>
    public required int Date { get; init; }

    /// <summary>
    /// How many of <see cref="Date"/> are written with this culture's own date separator — a dot for
    /// de-DE, a slash for en-US. What breaks a tie between cultures that read every value as a date.
    /// </summary>
    public int DatesWithOwnSeparator { get; init; }

    /// <summary>
    /// Non-empty values that did not read as a number here only because they write the decimal
    /// separator the other way, and can be read no other way: <c>34.020367</c> under a German reading.
    /// They are also among the numeric outliers; a binding may accept them with
    /// <see cref="ColumnBinding.AcceptOtherDecimalSeparator"/>.
    /// </summary>
    public int OtherSeparatorDecimals { get; init; }

    /// <summary>Values that did not read as a number, up to the configured limit.</summary>
    public required IReadOnlyList<ValueLocation> NumericOutliers { get; init => field = Equatable.List(value); }

    /// <summary>Values that did not read as a date, up to the configured limit.</summary>
    public required IReadOnlyList<ValueLocation> DateOutliers { get; init => field = Equatable.List(value); }
}
