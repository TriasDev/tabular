namespace TriasDev.Tabular.Archive;

/// <summary>Bounds on what a zip archive or a gzip-compressed file may make the reader hold or do.</summary>
/// <remarks>
/// The container's own quantities only. The files inside are read with the csv, xlsx and ods options
/// that <see cref="TabularOpenOptions"/> already carries, so each format has one set of options
/// whether it arrives on its own or in an archive.
/// </remarks>
public sealed record ArchiveCursorOptions
{
    /// <summary>The defaults.</summary>
    public static ArchiveCursorOptions Default { get; } = new();

    /// <summary>How many entries the archive's directory may list.</summary>
    public int MaxEntries { get; init; } = 16_384;

    /// <summary>How many bytes the archive's entries may declare, uncompressed, all together — or a compressed file expand to.</summary>
    /// <remarks>
    /// A zip's ratio is unbounded by design. The default admits a zipped OpenDocument spreadsheet at
    /// that format's own budget.
    /// </remarks>
    public long MaxUncompressedBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>How large a workbook inside the archive or the compressed file may be, as the file it is.</summary>
    /// <remarks>
    /// A workbook is itself a zip and must be read with random access, which an entry stream does not
    /// give, so it is held in memory while it is read — one at a time. Any positive value is allowed,
    /// past two gigabytes too: the buffer is held in pieces, not as one array. The workbook's own
    /// options still bound what it expands to.
    /// </remarks>
    public long MaxEmbeddedWorkbookBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>How many sheets the archive's files may hold, all together.</summary>
    /// <remarks>
    /// One descriptor per sheet, held for the cursor's life, as a workbook's are. The archive has a
    /// bound of its own because its sheets come from files of every kind: a csv file is one sheet and
    /// is bound by no workbook option. Each workbook inside still obeys its own format's
    /// <c>MaxSheets</c>.
    /// </remarks>
    public int MaxSheets { get; init; } = 4_096;

    internal ArchiveCursorOptions Checked()
    {
        OptionChecks.AtLeast(MaxEntries, 1, nameof(ArchiveCursorOptions), nameof(MaxEntries));
        OptionChecks.AtLeast(MaxUncompressedBytes, 1, nameof(ArchiveCursorOptions), nameof(MaxUncompressedBytes));
        OptionChecks.AtLeast(MaxEmbeddedWorkbookBytes, 1, nameof(ArchiveCursorOptions), nameof(MaxEmbeddedWorkbookBytes));
        OptionChecks.AtLeast(MaxSheets, 1, nameof(ArchiveCursorOptions), nameof(MaxSheets));
        return this;
    }
}
