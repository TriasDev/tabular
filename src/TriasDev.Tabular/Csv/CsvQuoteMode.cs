namespace TriasDev.Tabular;

/// <summary>How the csv reader treats quoting that RFC 4180 does not allow.</summary>
/// <remarks>
/// The choice is between two kinds of failure. A repaired file is read whole, but a repair is a guess,
/// and a guess can split a value that was written correctly. A strict read never guesses, and refuses
/// a file a repair would have read. A caller importing millions of rows from many producers wants the
/// first; a caller turning each of a few rows into work, for whom one misread row is worse than a
/// refused upload, wants the second.
/// </remarks>
public enum CsvQuoteMode
{
    /// <summary>
    /// Repairs what it can and counts each repair in <see cref="CursorDiagnostics"/>: a quoted field
    /// that spans lines and holds a record's worth of delimiters, or more than
    /// <see cref="CsvCursorOptions.MaxQuotedFieldLines"/> line breaks, or that the file ends inside, is
    /// read again as records; text after a closing quote (<c>"C" Road</c>) is kept as text.
    /// </summary>
    Repair = 0,

    /// <summary>
    /// Reads quotes as RFC 4180 does and refuses, as <see cref="TabularFormatException.Corrupt"/>,
    /// what <see cref="Repair"/> would repair: a quoted field the file ends inside, and text after a
    /// closing quote. A quoted field may span any number of lines and hold any number of delimiters;
    /// only <see cref="CsvCursorOptions.MaxFieldChars"/> bounds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whitespace before an opening quote and after a closing one is not part of the field, so
    /// <c>1, "Payroll", "monthly, HR"</c> reads as three values. That is any whitespace a cell is
    /// trimmed of anyway — a no-break space included — except a tab that is the delimiter, so
    /// <see cref="CsvCursorOptions.Quote"/> may not be whitespace in this mode. The whitespace before
    /// a quote is held until the quote shows it is not content, so it counts towards
    /// <see cref="CsvCursorOptions.MaxFieldChars"/>.
    /// </para>
    /// <para>
    /// A quote inside a field that does not start with one (<c>21"st AVE</c>) is a character of the
    /// text, as it is in every spreadsheet program. Nothing is repaired, so
    /// <see cref="CursorDiagnostics.IsClean"/> stays true.
    /// </para>
    /// <para>
    /// An opening quote that is never closed reads on to the end of the file and is refused there, as
    /// <see cref="TabularFormatException.Corrupt"/>; in an archive that message also names the file the
    /// row is in. When more than <see cref="CsvCursorOptions.MaxFieldChars"/> characters follow the
    /// quote, it reaches that limit first and is refused as a <see cref="TabularLimitException"/> whose
    /// message names the row and the open quote.
    /// </para>
    /// </remarks>
    Strict = 1,
}
