namespace TriasDev.Tabular;

/// <summary>One sheet of a file, as the file names it.</summary>
/// <remarks>
/// A sheet of cells. An xlsx chart sheet, macro sheet or dialog sheet holds none, and is not listed.
/// </remarks>
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
    /// <para>
    /// What a screen groups an archive's sheets by, and — with <see cref="Name"/> — what tells two
    /// workbooks' <c>Sheet1</c> apart.
    /// </para>
    /// <para>
    /// The path exactly as the archive stores it, not normalised: a leading slash or a <c>..</c>
    /// segment stays as written, so a tar's <c>/a.csv</c> and <c>a.csv</c> are two sources,
    /// and a zip's <c>../up.csv</c> is that, not <c>up.csv</c>. Normalising would merge files the
    /// archive keeps apart, and a mapping plan records this path to find its sheet again. Nothing is
    /// ever written to disk under it, so a path that climbs out of the archive is only a name here.
    /// </para>
    /// </remarks>
    public string? Source { get; init; }

    /// <summary>Whether the file's author left the sheet showing; a hidden sheet is still read.</summary>
    public SheetVisibility Visibility { get; init; }
}
