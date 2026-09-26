namespace TriasDev.Tabular.Ods;

/// <summary>Bounds on what an OpenDocument spreadsheet may make the reader hold or do.</summary>
/// <remarks>
/// The same quantities as for a workbook, and for the same reason: every structure read from an
/// upload has a ceiling, because a small file can describe a very large one. Two are OpenDocument's
/// own — the column and row ceilings, which is where its repeat attributes are an attack vector.
/// </remarks>
public sealed record OdsCursorOptions
{
    /// <summary>The defaults.</summary>
    public static OdsCursorOptions Default { get; } = new();

    /// <summary>How many bytes the package's parts may declare, uncompressed, all together.</summary>
    /// <remarks>
    /// Four times the workbook's, because OpenDocument spends about that many more bytes on the same
    /// cells: a million rows of seventeen columns is 1.9 GB of content, where the xlsx is half a
    /// gigabyte. At the workbook's two it would refuse a sheet a little taller or wider than that.
    /// The reading streams, so the budget bounds time spent, not memory; the cells a file can ask
    /// for are bounded by the row and column ceilings.
    /// </remarks>
    public long MaxUncompressedBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>How many entries the package may hold.</summary>
    public int MaxPackageEntries { get; init; } = 16_384;

    /// <summary>How many sheets the spreadsheet may declare.</summary>
    public int MaxSheets { get; init; } = 4_096;

    /// <summary>
    /// How many columns a row may fill, counting a repeated cell as many times as it repeats.
    /// </summary>
    /// <remarks>
    /// An empty repeat only moves the column along and costs nothing; a repeated cell holding a value
    /// is expanded, so twenty characters of markup could otherwise ask for a billion cells. The
    /// default is the workbook format's own width, which LibreOffice also uses.
    /// </remarks>
    public int MaxColumns { get; init; } = 16_384;

    /// <summary>
    /// The highest row number a row holding a value may have, counting a repeated row as many times
    /// as it repeats.
    /// </summary>
    /// <remarks>
    /// LibreOffice writes "the rest of the sheet is empty" as one row repeated a million times; that
    /// is skipped, not expanded. A repeated row that holds a value is expanded, and this is what stops
    /// one element from producing a billion of them.
    /// </remarks>
    public int MaxRows { get; init; } = 1_048_576;

    /// <summary>The most characters one cell's value may assemble to.</summary>
    public int MaxValueChars { get; init; } = 16 * 1024 * 1024;

    internal OdsCursorOptions Checked()
    {
        OptionChecks.AtLeast(MaxUncompressedBytes, 1, nameof(OdsCursorOptions), nameof(MaxUncompressedBytes));
        OptionChecks.AtLeast(MaxPackageEntries, 1, nameof(OdsCursorOptions), nameof(MaxPackageEntries));
        OptionChecks.AtLeast(MaxSheets, 1, nameof(OdsCursorOptions), nameof(MaxSheets));
        OptionChecks.AtLeast(MaxColumns, 1, nameof(OdsCursorOptions), nameof(MaxColumns));
        OptionChecks.AtLeast(MaxRows, 1, nameof(OdsCursorOptions), nameof(MaxRows));
        OptionChecks.AtLeast(MaxValueChars, 1, nameof(OdsCursorOptions), nameof(MaxValueChars));
        return this;
    }
}
