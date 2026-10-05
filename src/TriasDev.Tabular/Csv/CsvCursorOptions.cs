using System.Text;
using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular;

/// <summary>Knobs for reading a csv file.</summary>
public sealed record CsvCursorOptions
{
    /// <summary>The defaults, which are what a caller reading a user-supplied file wants.</summary>
    public static CsvCursorOptions Default { get; } = new();

    /// <summary>
    /// The character separating fields, when the caller knows it: one of <c>,</c> <c>;</c> tab
    /// <c>|</c>, the delimiters the reader detects. When null it is detected from the file's head.
    /// </summary>
    /// <remarks>
    /// A stated delimiter is reported as <see cref="DialectSource.Specified"/> in
    /// <see cref="CsvDialect.DelimiterSource"/>; the encoding is still detected unless
    /// <see cref="Encoding"/> is stated too. The same property as <see cref="CsvWriterOptions.Delimiter"/>,
    /// so a file written with one reads back with the other.
    /// </remarks>
    public char? Delimiter { get; init; }

    /// <summary>
    /// The encoding the file's bytes are read with, when the caller knows it. When null it is
    /// detected: a byte order mark, else valid UTF-8, else Windows-1252.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stated encoding is reported as <see cref="DialectSource.Specified"/> in
    /// <see cref="CsvDialect.EncodingSource"/>, and it wins over a byte order mark: a mark of the
    /// stated encoding is skipped, never read as content; the mark of another encoding is read as
    /// the characters its bytes are in the stated one.
    /// </para>
    /// <para>
    /// Stating it, or the whole dialect, does not stop a file that is not csv at all — a legacy
    /// workbook, an XML document, a binary file — from being refused as
    /// <see cref="TabularFormatException.Unsupported"/>: the file's head is still read for that.
    /// </para>
    /// </remarks>
    public Encoding? Encoding { get; init; }

    /// <summary>
    /// The character that quotes a field, when it is not <c>"</c>. When null it is <c>"</c>.
    /// </summary>
    /// <remarks>
    /// It may not be a line ending, nor one of the delimiters the reader knows (<c>,</c> <c>;</c> tab
    /// <c>|</c>), since a detected delimiter could then be the quote too. Delimiter detection honours
    /// it.
    /// </remarks>
    public char? Quote { get; init; }

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

        if (Delimiter is { } delimiter && !CsvDialectDetector.IsDelimiter(delimiter))
        {
#pragma warning disable S3928 // Justification: the options arrive through a parameter named options, as OptionChecks names it
            throw new ArgumentOutOfRangeException(
                "options",
                delimiter,
                $"{nameof(CsvCursorOptions)}.{nameof(Delimiter)} must be one of , ; tab | — the delimiters the reader detects.");
#pragma warning restore S3928
        }

        if (Quote is { } quote && (quote is '\r' or '\n' || CsvDialectDetector.IsDelimiter(quote)))
        {
#pragma warning disable S3928 // Justification: the options arrive through a parameter named options, as OptionChecks names it
            throw new ArgumentOutOfRangeException(
                "options",
                quote,
                $"{nameof(CsvCursorOptions)}.{nameof(Quote)} may be neither a line ending nor one of , ; tab |, the delimiters the reader detects.");
#pragma warning restore S3928
        }

        return this;
    }
}
