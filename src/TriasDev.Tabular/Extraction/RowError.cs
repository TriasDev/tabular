namespace TriasDev.Tabular;

/// <summary>Something wrong with one value in one row.</summary>
/// <remarks>
/// Carries a code from a closed catalog rather than a message. The library knows nothing about who
/// will read this or in what language; the calling domain maps the code onto its own error envelope
/// and a frontend derives its wording from it.
/// </remarks>
public sealed record RowError
{
    /// <summary>The row as a person reading the file in a spreadsheet would count it.</summary>
    public required int RowNumber { get; init; }

    /// <summary>The column the value came from, zero-based.</summary>
    public required int SourceColumnIndex { get; init; }

    /// <summary>The field it was meant to fill.</summary>
    public required string TargetFieldName { get; init; }

    /// <summary>What is wrong, from the library's catalog.</summary>
    public required string Code { get; init; }

    /// <summary>The value exactly as the file holds it.</summary>
    public string? RawValue { get; init; }
}
