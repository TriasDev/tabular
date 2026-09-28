namespace TriasDev.Tabular;

/// <summary>A source column feeding a target field.</summary>
public sealed record ColumnBinding
{
    /// <summary>The column's position in the sheet, zero-based.</summary>
    public required int ColumnIndex { get; init; }

    /// <summary>
    /// The header that stood at that position when the file was analysed.
    /// </summary>
    /// <remarks>
    /// Carried as a checksum, not as an address. Analysis and extraction are separate reads of the
    /// file, with however long a user spends in a mapping screen between them, so extraction compares
    /// this against what it finds and refuses a file that changed underneath — rather than loading
    /// street names into the country column and reporting success.
    /// </remarks>
    public required string Header { get; init; }

    /// <summary>The field this column fills.</summary>
    public required string FieldName { get; init; }

    /// <summary>Values to read as absent, such as a placeholder a spreadsheet uses for "unknown".</summary>
    public IReadOnlyList<string> TreatAsEmpty { get; init => field = Equatable.List(value); } = Equatable.Empty<string>();

    /// <summary>
    /// Whether a decimal field reads a value that writes the decimal separator the other way from
    /// the plan's culture, where that is the only way it can be read — <c>34.020367</c> under de-DE.
    /// </summary>
    /// <remarks>
    /// Off by default: the reading is unambiguous, but it is still a guess about a file mixing two
    /// conventions, and a person decides whether to make it. The profile counts such values per
    /// culture (<see cref="CultureParseCounts.OtherSeparatorDecimals"/>), the precheck judges the plan
    /// with this setting, and a run counts the values it read this way
    /// (<see cref="ExtractionSummary.OtherSeparatorDecimals"/>), so the leniency is never silent.
    /// </remarks>
    public bool AcceptOtherDecimalSeparator { get; init; }
}
