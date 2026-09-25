namespace TriasDev.Tabular.Xlsx;

/// <summary>Knobs for reading a workbook.</summary>
public sealed record XlsxCursorOptions
{
    /// <summary>The defaults.</summary>
    public static XlsxCursorOptions Default { get; } = new();

    /// <summary>
    /// How many bytes the package may expand to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An xlsx file is a zip, and a zip's expansion ratio is unbounded by design. A real workbook is
    /// modest about it — a measured 101 MB package holds a 545 MB sheet, so roughly five to one — but
    /// a file built to be hostile reaches a thousand to one, which turns a 50 MB upload into fifty
    /// gigabytes of allocation. The limit is what makes the difference between a refused file and an
    /// exhausted host.
    /// </para>
    /// <para>
    /// Two gigabytes leaves generous room above anything a spreadsheet program produces at the sizes
    /// this library is used for.
    /// </para>
    /// </remarks>
    public long MaxUncompressedBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// How many entries the shared string table may hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MaxUncompressedBytes"/> bounds the bytes a part expands to, which is not what the
    /// table costs. <c>&lt;si&gt;&lt;t&gt;a&lt;/t&gt;&lt;/si&gt;</c> is seventeen bytes on the wire
    /// and roughly forty in the heap, so the byte budget converts into several times its own size in
    /// objects: measured, a 41 KB workbook holding one million shared strings and a single-cell sheet
    /// cost 63 MB, a ratio of about fifteen hundred to one.
    /// </para>
    /// <para>
    /// The number is the sheet's own row count. Anything larger means the file holds more distinct
    /// strings than a column can hold cells, and a round million — the first value here — was
    /// forty-eight thousand short of it: measured, an honest 2.63 MB workbook of one full-height
    /// column of unique keys was refused.
    /// </para>
    /// </remarks>
    public int MaxSharedStrings { get; init; } = 1_048_576;

    /// <summary>
    /// How many characters the shared string table may hold in total.
    /// </summary>
    /// <remarks>
    /// The entry ceiling alone bounds the wrong dimension, which is the mistake it was added to fix,
    /// one storey up: a million entries of two thousand characters each satisfies it and costs
    /// 3.9 GB — measured, from a workbook of three megabytes. What a table costs is its characters,
    /// so that is what is counted. Sixty-four million of them is 128 MB of text, past any real
    /// workbook and far below what it takes to hurt.
    /// </remarks>
    public int MaxSharedStringChars { get; init; } = 64 * 1024 * 1024;

    /// <summary>
    /// How many parts the package may hold.
    /// </summary>
    /// <remarks>
    /// Every entry's metadata is materialised to find the parts by name, before any budget can be
    /// consulted — measured, six hundred thousand empty entries in a 52 MB upload retain 405 MB for
    /// the cursor's whole life. A workbook of two hundred sheets with drawings and comments runs to
    /// perhaps a thousand parts.
    /// </remarks>
    public int MaxPackageEntries { get; init; } = 16_384;

    /// <summary>
    /// How many worksheets the workbook may declare.
    /// </summary>
    /// <remarks>
    /// One <c>SheetInfo</c> and one path per sheet, held for the cursor's whole life and walked by
    /// anything that analyses the file. Measured before this existed: a 2.35 MB upload declaring
    /// sixteen million sheets retained 2,441 MB, and the package budget would have allowed five times
    /// that.
    /// </remarks>
    public int MaxSheets { get; init; } = 4_096;

    /// <summary>
    /// How long any one string taken from the package's own bookkeeping may be.
    /// </summary>
    /// <remarks>
    /// A sheet name, a relationship target, a number-format code. Each of those had a ceiling on how
    /// many there could be and none on how long each could be, which is the same mistake three times:
    /// measured, four thousand sheets — exactly the permitted number — with four-hundred-thousand-
    /// character names is a 1.65 MB upload that holds 3,125 MB for as long as the file is open. A real
    /// sheet name is at most thirty-one characters, and a relationship target a path.
    /// </remarks>
    public int MaxMetadataChars { get; init; } = 2_048;

    /// <summary>
    /// How many relationships the workbook part may declare.
    /// </summary>
    /// <remarks>
    /// A map built before anything is read from it, and one entry per relationship: measured, a
    /// 10.44 MB upload cost 954 MB. A workbook declares roughly one per sheet plus a handful.
    /// </remarks>
    public int MaxRelationships { get; init; } = 8_192;

    /// <summary>
    /// How many entries the style table may hold, of either kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read in the constructor, before a caller can cancel anything: measured, a style table of a
    /// hundred and ten million formats spent 8.7 seconds and retained 105 MB before the first row was
    /// read. The workbook format itself stops at 65,490.
    /// </para>
    /// <para>
    /// Of either kind, because the first version of this counted the cell formats and left the number
    /// formats beside them unbounded — and those cost more each, carrying a string. A ceiling on one
    /// of two lists in the same loop is not a ceiling.
    /// </para>
    /// </remarks>
    public int MaxCellFormats { get; init; } = 100_000;

    /// <summary>
    /// How many characters one cell's value may hold.
    /// </summary>
    /// <remarks>
    /// The scanner bounds a single token, which is not the same thing: a value is assembled from as
    /// many formatting runs as the file cares to write, each of them small. Measured, a 307 KB
    /// workbook produced one cell of eighty million characters — 160 MB for one value — with no token
    /// anywhere near the scanner's ceiling. This bounds the value itself, on both the inline and the
    /// shared-string paths.
    /// </remarks>
    public int MaxValueChars { get; init; } = 16 * 1024 * 1024;

    internal XlsxCursorOptions Checked()
    {
        OptionChecks.AtLeast(MaxUncompressedBytes, 1, nameof(XlsxCursorOptions), nameof(MaxUncompressedBytes));
        OptionChecks.AtLeast(MaxSharedStrings, 0, nameof(XlsxCursorOptions), nameof(MaxSharedStrings));
        OptionChecks.AtLeast(MaxSharedStringChars, 0, nameof(XlsxCursorOptions), nameof(MaxSharedStringChars));
        OptionChecks.AtLeast(MaxPackageEntries, 1, nameof(XlsxCursorOptions), nameof(MaxPackageEntries));
        OptionChecks.AtLeast(MaxSheets, 1, nameof(XlsxCursorOptions), nameof(MaxSheets));
        OptionChecks.AtLeast(MaxMetadataChars, 1, nameof(XlsxCursorOptions), nameof(MaxMetadataChars));
        OptionChecks.AtLeast(MaxRelationships, 1, nameof(XlsxCursorOptions), nameof(MaxRelationships));
        OptionChecks.AtLeast(MaxCellFormats, 0, nameof(XlsxCursorOptions), nameof(MaxCellFormats));
        OptionChecks.AtLeast(MaxValueChars, 1, nameof(XlsxCursorOptions), nameof(MaxValueChars));
        return this;
    }
}
