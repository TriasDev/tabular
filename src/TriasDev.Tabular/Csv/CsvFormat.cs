using System.Globalization;

namespace TriasDev.Tabular.Csv;

/// <summary>What <see cref="CsvWriterOptions"/> resolve to: everything the csv writer needs, checked.</summary>
internal sealed class CsvFormat(CultureInfo culture, char delimiter, string dateFormat, string dateTimeFormat, bool byteOrderMark, bool formulaGuard)
{
    /// <summary>The culture numbers and dates are formatted in.</summary>
    public CultureInfo Culture { get; } = culture;

    /// <summary>The field delimiter.</summary>
    public char Delimiter { get; } = delimiter;

    /// <summary>The format of a date without a time.</summary>
    public string DateFormat { get; } = dateFormat;

    /// <summary>The format of a date with a time, to the millisecond.</summary>
    public string DateTimeFormat { get; } = dateTimeFormat;

    /// <summary>Whether the file starts with the UTF-8 byte order mark.</summary>
    public bool ByteOrderMark { get; } = byteOrderMark;

    /// <summary>Whether text that a spreadsheet would run as a formula is prefixed with an apostrophe.</summary>
    public bool FormulaGuard { get; } = formulaGuard;
}
