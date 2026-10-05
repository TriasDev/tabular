namespace TriasDev.Tabular;

/// <summary>
/// The file shapes this library reads — <see cref="Csv"/>, <see cref="Xlsx"/>, <see cref="Ods"/>, and
/// the containers <see cref="Zip"/>, <see cref="Gzip"/> and <see cref="Tar"/> — and the ones it writes:
/// <see cref="Csv"/>, <see cref="Xlsx"/>, <see cref="Ods"/> and <see cref="Zip"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TabularWriter.Create"/> refuses <see cref="Gzip"/> and <see cref="Tar"/> with an
/// <see cref="ArgumentOutOfRangeException"/>: they are read, not written.
/// </para>
/// <para>
/// Open to new members as the library learns to read or write more. A switch over it needs a default arm, or the day a member is added breaks the build of
/// whoever wrote it.
/// </para>
/// </remarks>
public enum TabularFormat
{
    /// <summary>An OOXML workbook.</summary>
    Xlsx,

    /// <summary>A delimiter-separated text file.</summary>
    Csv,

    /// <summary>An OpenDocument spreadsheet, as LibreOffice writes it.</summary>
    Ods,

    /// <summary>
    /// A zip archive of files, read as one workbook whose sheets are theirs. The container's format:
    /// each sheet keeps its own. Written as csv sheets: one <c>&lt;sheet name&gt;.csv</c> entry per sheet.
    /// </summary>
    /// <remarks>
    /// Read, a zip's entries keep their own formats (csv, xlsx, ods); written, a zip holds csv sheets only.
    /// </remarks>
    Zip,

    /// <summary>
    /// A gzip-compressed file, read as the file inside it. The container's format: its sheets keep
    /// the inner file's. Not written.
    /// </summary>
    Gzip,

    /// <summary>
    /// A tar archive, plain or compressed with gzip, read as one workbook whose sheets are its files'.
    /// The container's format: each sheet keeps its own. Not written.
    /// </summary>
    Tar,
}
