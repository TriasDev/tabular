namespace TriasDev.Tabular;

/// <summary>One sheet of a file, as the file names it.</summary>
public sealed record SheetInfo
{
    /// <summary>Position in the file, zero-based.</summary>
    public required int Index { get; init; }

    /// <summary>
    /// The sheet's name. A csv file has no sheets, so its single one is named after the file.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The format of the file the sheet was read from.</summary>
    /// <remarks>
    /// The same as the cursor's for a plain file. Carried per sheet because an archive holds files of
    /// either kind, and a sheet read from a csv inside it has a dialect a workbook sheet beside it
    /// does not.
    /// </remarks>
    public required TabularFormat Format { get; init; }

    /// <summary>
    /// Where inside an archive the sheet's file lies, or null for a sheet of a plain file.
    /// </summary>
    /// <remarks>
    /// What a screen groups an archive's sheets by, and — with <see cref="Name"/> — what tells two
    /// workbooks' <c>Sheet1</c> apart.
    /// </remarks>
    public string? Source { get; init; }
}
