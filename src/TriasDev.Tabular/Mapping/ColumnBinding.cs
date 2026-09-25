namespace TriasDev.Tabular.Mapping;

/// <summary>A source column feeding a target field.</summary>
public sealed record ColumnBinding
{
    /// <summary>The column's position in the sheet, zero-based.</summary>
    public required int SourceColumnIndex { get; init; }

    /// <summary>
    /// The header that stood at that position when the file was analysed.
    /// </summary>
    /// <remarks>
    /// Carried as a checksum, not as an address. Analysis and extraction are separate reads of the
    /// file, with however long a user spends in a mapping screen between them, so extraction compares
    /// this against what it finds and refuses a file that changed underneath — rather than loading
    /// street names into the country column and reporting success.
    /// </remarks>
    public required string SourceHeader { get; init; }

    /// <summary>The field this column fills.</summary>
    public required string TargetFieldName { get; init; }

    /// <summary>Values to read as absent, such as a placeholder a spreadsheet uses for "unknown".</summary>
    public IReadOnlyList<string> TreatAsEmpty { get; init; } = [];
}
