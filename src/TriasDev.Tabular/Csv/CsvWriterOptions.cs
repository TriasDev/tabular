using System.Globalization;

namespace TriasDev.Tabular.Csv;

/// <summary>Knobs for writing a csv file.</summary>
/// <remarks>
/// Whatever is chosen, the import reads the file back: a culture is accepted only if a probe of its
/// numbers and dates, written as the writer writes them, reads back through the import's own reader.
/// </remarks>
public sealed record CsvWriterOptions
{
    private const string IsoDate = "yyyy'-'MM'-'dd";

    private const string IsoDateTime = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff";

    /// <summary>The defaults: invariant culture, comma, ISO dates, a byte order mark, no formula guard.</summary>
    public static CsvWriterOptions Default { get; } = new();

    /// <summary>
    /// The culture numbers and dates are written in, by name, or null or empty for the invariant one —
    /// decimal point, ISO dates.
    /// </summary>
    /// <remarks>
    /// <c>de-DE</c> writes <c>1234,56</c> and <c>03.10.2026</c>, which a German Excel opens by double
    /// click. Import such a file with the culture named in the plan rather than taken from analysis:
    /// when every day in a column is 12 or less, day-first and month-first cannot be told apart.
    /// </remarks>
    public string? Culture { get; init; }

    /// <summary>
    /// The field delimiter: one of <c>,</c> <c>;</c> tab <c>|</c>, the ones the reader detects. Null
    /// chooses <c>;</c> when the culture's decimal separator is a comma, otherwise <c>,</c>.
    /// </summary>
    public char? Delimiter { get; init; }

    /// <summary>
    /// Starts the file with the UTF-8 byte order mark — without it, Excel reads the file in the
    /// system's code page and mangles every umlaut.
    /// </summary>
    public bool ByteOrderMark { get; init; } = true;

    /// <summary>
    /// Prefixes text starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return with
    /// an apostrophe, so a spreadsheet does not run it as a formula — the OWASP defence against csv
    /// injection.
    /// </summary>
    /// <remarks>
    /// Off by default because it changes the value the import reads back: <c>=1+2</c> returns as
    /// <c>'=1+2</c>. Turn it on when exporting other people's data to be opened in a spreadsheet.
    /// Numbers are never prefixed.
    /// </remarks>
    public bool FormulaGuard { get; init; }

    internal CsvFormat Resolve()
    {
        const string CultureParam = nameof(Culture);
        const string DelimiterParam = nameof(Delimiter);

        if (!CultureCatalog.TryGet(Culture, out CultureInfo culture))
        {
#pragma warning disable S3928 // Justification: Parameter names identify the properties being validated, not method parameters
            throw new ArgumentException(
                $"{nameof(CsvWriterOptions)}.{CultureParam} '{Culture}' is not a culture this runtime has.",
                CultureParam);
#pragma warning restore S3928
        }

        if (culture.DateTimeFormat.Calendar is not GregorianCalendar)
        {
#pragma warning disable S3928 // Justification: Parameter names identify the properties being validated, not method parameters
            throw new ArgumentException(
                $"{nameof(CsvWriterOptions)}.{CultureParam} '{Culture}' does not use the Gregorian calendar, which the import reads dates in.",
                CultureParam);
#pragma warning restore S3928
        }

        string separator = culture.NumberFormat.NumberDecimalSeparator;
        char delimiter = Delimiter ?? (separator == "," ? ';' : ',');

        if (delimiter is not (',' or ';' or '\t' or '|'))
        {
#pragma warning disable S3928 // Justification: Parameter names identify the properties being validated, not method parameters
            throw new ArgumentOutOfRangeException(
                DelimiterParam,
                delimiter,
                $"{nameof(CsvWriterOptions)}.{DelimiterParam} must be one of , ; tab | — the delimiters the reader detects.");
#pragma warning restore S3928
        }

        if (separator.Contains(delimiter, StringComparison.Ordinal))
        {
#pragma warning disable S3928 // Justification: Parameter names identify the properties being validated, not method parameters
            throw new ArgumentException(
                $"{nameof(CsvWriterOptions)}.{DelimiterParam} '{delimiter}' is the decimal separator of culture '{Culture}'.",
                DelimiterParam);
#pragma warning restore S3928
        }

        bool invariant = culture.Name.Length == 0;
        CsvFormat format = new(
            culture,
            delimiter,
            invariant ? IsoDate : culture.DateTimeFormat.ShortDatePattern,
            invariant ? IsoDateTime : culture.DateTimeFormat.ShortDatePattern + " HH:mm:ss.fff",
            ByteOrderMark,
            FormulaGuard);

        Probe(format);
        return format;
    }

    /// <summary>
    /// Writes a value of each kind as the writer would and reads it back with the import's reader.
    /// </summary>
    /// <remarks>
    /// 1801 and 9999 catch a pattern with a two-digit year; the 13th catches day and month swapped;
    /// the negatives catch a minus sign the reader does not take.
    /// </remarks>
    private static void Probe(CsvFormat format)
    {
        CultureInfo culture = format.Culture;
        DateTime moment = new(1801, 2, 13, 14, 5, 6, 789, DateTimeKind.Unspecified);
        DateTime last = new(9999, 12, 31, 0, 0, 0, 0, DateTimeKind.Unspecified);

        Check(format, ColumnType.Decimal, (-1_234_567.891m).ToString(culture), MappedValue.FromDecimal(-1_234_567.891m));
        Check(format, ColumnType.Decimal, (-0.5).ToString("R", culture), MappedValue.FromDecimal(-0.5m));
        Check(format, ColumnType.Integer, (-42L).ToString(culture), MappedValue.FromInteger(-42));
        Check(format, ColumnType.Date, moment.ToString(format.DateTimeFormat, culture), MappedValue.FromDate(moment));
        Check(format, ColumnType.Date, last.ToString(format.DateFormat, culture), MappedValue.FromDate(last));
    }

    private static void Check(CsvFormat format, ColumnType type, string text, MappedValue expected)
    {
        const string CultureParam = nameof(Culture);
        RawCell cell = RawCell.FromText(text);

        if (!ValueReading.TryRead(cell, cell.Text ?? string.Empty, type, format.Culture, out MappedValue read) || read != expected)
        {
#pragma warning disable S3928 // Justification: Parameter names identify the properties being validated, not method parameters
            throw new ArgumentException(
                $"{nameof(CsvWriterOptions)}.{CultureParam} '{format.Culture.Name}' writes a {type} the import does not read back: \"{text}\".",
                CultureParam);
#pragma warning restore S3928
        }
    }
}
