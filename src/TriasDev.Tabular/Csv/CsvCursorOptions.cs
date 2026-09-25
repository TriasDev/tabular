namespace TriasDev.Tabular.Csv;

/// <summary>Knobs for reading a csv file.</summary>
public sealed record CsvCursorOptions
{
    /// <summary>The defaults, which are what a caller reading a user-supplied file wants.</summary>
    public static CsvCursorOptions Default { get; } = new();

    /// <summary>
    /// A dialect the caller has decided. When null, the dialect is detected from the file's head.
    /// </summary>
    public CsvDialect? Dialect { get; init; }

    /// <summary>
    /// How many line endings one quoted field may contain before the reader concludes its opening
    /// quote was never meant as syntax.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A quoted field spanning lines is legal and common — an address, a comment. A quoted field
    /// spanning a thousand lines is not a field; it is a stray quote consuming the rest of the file.
    /// Without a bound the two are indistinguishable, and the second one wins silently: 302 stray
    /// quotes in a 572 MB export swallow about 39,000 records.
    /// </para>
    /// <para>
    /// A hundred is generous for any genuine note or address. It was four, which split a well-formed
    /// six-line note into rows (#10); it can be this high because a stray quote in a table of five
    /// columns or more is now caught by what it swallows — a whole record's worth of delimiters,
    /// counted in <see cref="CursorDiagnostics.RecoveredStrayQuotes"/> — long before this bound. The
    /// bound is what stands behind narrower tables, and every recovery by it is counted in
    /// <see cref="CursorDiagnostics.RecoveredUnterminatedQuotes"/>.
    /// </para>
    /// </remarks>
    public int MaxQuotedFieldLines { get; init; } = 100;

    /// <summary>
    /// The most characters one field may hold.
    /// </summary>
    /// <remarks>
    /// The format sets no limit, so without one here a single unterminated value in a modest upload
    /// can consume a host's memory — and it is held twice while a quoted field is open, once as the
    /// value and once as the raw text kept in case the quote proves not to be syntax. Sixteen million
    /// characters is far above any field a person will produce and far below what it takes to hurt.
    /// </remarks>
    public int MaxFieldChars { get; init; } = 16 * 1024 * 1024;

    /// <summary>How many bytes of the head are read to detect the dialect.</summary>
    public int DialectProbeBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// How many columns one row may have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The format has no width, so without a ceiling the row buffer grows with the file. A csv of
    /// nothing but delimiters is the cheapest attack there is — no quoting, no encoding, no structure
    /// to get right — and it costs nothing to produce: measured, 1.9 MB of commas becomes two million
    /// cells and 90 MB of heap inside a single read, which is a ratio of about fifty to one against
    /// an upload ceiling that allows fifty megabytes.
    /// </para>
    /// <para>
    /// The number is the workbook format's own, and deliberately so. A file wider than this cannot be
    /// opened in the tool the people sending us files use, so refusing it costs nobody anything.
    /// Length is the dimension a csv is allowed to be unreasonable in; width is not.
    /// </para>
    /// </remarks>
    public int MaxColumns { get; init; } = 16_384;

    internal CsvCursorOptions Checked()
    {
        OptionChecks.AtLeast(MaxQuotedFieldLines, 0, nameof(CsvCursorOptions), nameof(MaxQuotedFieldLines));
        OptionChecks.AtLeast(MaxFieldChars, 1, nameof(CsvCursorOptions), nameof(MaxFieldChars));
        OptionChecks.AtLeast(DialectProbeBytes, 1, nameof(CsvCursorOptions), nameof(DialectProbeBytes));
        OptionChecks.AtLeast(MaxColumns, 1, nameof(CsvCursorOptions), nameof(MaxColumns));
        return this;
    }
}
