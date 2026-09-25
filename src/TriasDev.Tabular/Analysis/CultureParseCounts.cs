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

    /// <summary>Values that did not read as a number, up to the configured limit.</summary>
    public required IReadOnlyList<ValueLocation> NumericOutliers { get; init; }

    /// <summary>Values that did not read as a date, up to the configured limit.</summary>
    public required IReadOnlyList<ValueLocation> DateOutliers { get; init; }
}
