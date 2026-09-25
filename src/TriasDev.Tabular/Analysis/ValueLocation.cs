namespace TriasDev.Tabular.Analysis;

/// <summary>Where a value stands in the file, and what it says.</summary>
/// <remarks>
/// The point of the whole profile. A user told that 99.8% of a column parses as a number can do
/// nothing with that; a user told that rows 812, 4471 and 9033 hold <c>k.A.</c> can fix the file.
/// </remarks>
public readonly record struct ValueLocation
{
    /// <summary>The row as a person reading the file in a spreadsheet would count it.</summary>
    public required int RowNumber { get; init; }

    /// <summary>The value exactly as the file holds it.</summary>
    public required string RawValue { get; init; }
}
