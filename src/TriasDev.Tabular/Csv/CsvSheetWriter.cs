using System.Buffers;

namespace TriasDev.Tabular.Csv;

/// <summary>
/// Writes csv: a row is formatted into a reused character buffer and encoded as UTF-8 into the
/// spill buffer when it ends.
/// </summary>
/// <remarks>
/// RFC 4180: a field holding a delimiter the reader could detect, a quote or a line break is quoted,
/// its quotes doubled — numbers and dates included, whose decimal comma would otherwise count as one;
/// every record ends in CR LF. Nothing is allocated per row — the buffer is reused, and numbers and
/// dates are formatted straight into it.
/// </remarks>
internal sealed class CsvSheetWriter : ISheetWriter
{
    /// <summary>A formatted number or date this short is copied on the stack when it has to be quoted.</summary>
    private const int FormattedOnStack = 128;

    /// <summary>The line breaks one quoted field may hold before the reader takes its quote for a stray one.</summary>
    private static readonly int MaxLineBreaks = CsvCursorOptions.Default.MaxQuotedFieldLines;

    /// <summary>The longest field the reader reads.</summary>
    private static readonly int MaxFieldChars = CsvCursorOptions.Default.MaxFieldChars;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// What makes a field quoted: a quote, a line break, and every delimiter the reader may detect —
    /// not only the one written. The reader picks the delimiter from the file's lines, so a candidate
    /// left unquoted in every row (a column of German decimals, of <c>a;b</c>) can be taken for it.
    /// </summary>
    private static readonly SearchValues<char> NeedsQuotes = SearchValues.Create(",;\t|\"\r\n");

    private static readonly SearchValues<char> LineBreaks = SearchValues.Create("\r\n");

    private readonly SpillBuffer _out;
    private readonly CsvFormat _format;
    private int _columnCount;
    private readonly RowText _row = new();
    private bool _firstCell = true;

    public CsvSheetWriter(SpillBuffer output, CsvFormat format)
    {
        _out = output;
        _format = format;
    }

    /// <summary>Nothing to release: the buffer belongs to the caller.</summary>
    public void Dispose()
    {
    }

    public long MaxRows => long.MaxValue;

    public bool AllowsSeveralSheets => false;

    public bool NamesSheets => false;

    /// <summary>The row buffer's current size, for the test that a huge row does not keep it.</summary>
    internal int RowBufferLength => _row.Capacity;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions options)
    {
        _columnCount = columns.Length;

        if (_format.ByteOrderMark)
        {
            _out.Write(Utf8Bom);
        }
    }

    public void BeginRow()
    {
        _row.Clear();
        _firstCell = true;
    }

    public string? WriteHeader(string value, int style) => WriteField(value, guard: false, column: -1);

    public string? WriteText(string value, int column, int style)
    {
        bool guard = _format.FormulaGuard && value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';
        return WriteField(value, guard, column);
    }

    /// <summary>Writes a text field; <paramref name="column"/> is negative for a header.</summary>
    private string? WriteField(ReadOnlySpan<char> text, bool guard, int column)
    {
        if (text.Length + (guard ? 1 : 0) > MaxFieldChars)
        {
            return ErrorCodes.Write.TextTooLong;
        }

        bool quoted = text.IndexOfAny(NeedsQuotes) >= 0;

        if (quoted && text.IndexOfAny(LineBreaks) is var firstBreak and >= 0)
        {
            if (CountLineBreaks(text[firstBreak..]) > MaxLineBreaks)
            {
                return ErrorCodes.Write.TooManyLines;
            }

            // The reader learns the record's width from the header, so the header itself is never judged.
            if (column >= 0 && ReadAsStrayQuote(text, firstBreak, column))
            {
                return ErrorCodes.Write.AmbiguousLineBreaks;
            }
        }

        Separate();

        if (!quoted)
        {
            if (guard)
            {
                _row.Append('\'');
            }

            _row.Append(text);
            return null;
        }

        AppendQuoted(text, guard);
        return null;
    }

    /// <summary>
    /// Whether the reader would take this quoted field's opening quote for a stray one and split the
    /// field into records — <see cref="CsvCursor"/>'s rule, mirrored exactly.
    /// </summary>
    /// <remarks>
    /// The reader replays a quoted field that crosses a line break once it holds a record's worth of
    /// delimiters (one fewer than the columns), in a table of five columns or more — unless the line
    /// the quote opened on, read with the quote as stray, would hold more fields than a record: the
    /// cells before this one, plus the delimiters before the field's first line break, reach the
    /// column count.
    /// </remarks>
    private bool ReadAsStrayQuote(ReadOnlySpan<char> text, int firstBreak, int column)
    {
        if (_columnCount < 5)
        {
            return false;
        }

        int beforeBreak = text[..firstBreak].Count(_format.Delimiter);

        if (column + beforeBreak >= _columnCount)
        {
            return false;
        }

        return beforeBreak + text[firstBreak..].Count(_format.Delimiter) >= _columnCount - 1;
    }

    /// <summary>Line breaks as the reader counts them: a line feed, or a carriage return not followed by one.</summary>
    private static int CountLineBreaks(ReadOnlySpan<char> text)
    {
        int count = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
            {
                count++;
            }
        }

        return count;
    }

    public string? WriteLong(long value, int style)
    {
        Separate();
        AppendFormatted(value, default);
        return null;
    }

    public string? WriteDecimal(decimal value, int style)
    {
        Separate();
        AppendFormatted(value, default);
        return null;
    }

    public string? WriteDouble(double value, int style)
    {
        Separate();
        AppendFormatted(value, "R");
        return null;
    }

    public string? WriteDate(DateTime value, bool hasTime, int style)
    {
        // The import's date reader refuses year 1 — it cannot tell it from a date with no year.
        if (value.Year == 1)
        {
            return ErrorCodes.Write.DateOutOfRange;
        }

        Separate();
        AppendFormatted(value, hasTime ? _format.DateTimeFormat : _format.DateFormat);
        return null;
    }

    public void WriteBoolean(bool value, int style)
    {
        Separate();
        _row.Append(value ? "true" : "false");
    }

    public int MaxMerges => int.MaxValue;

    public void Merge(int rows, int columns)
    {
    }

    public void WriteCovered() => Separate();

    public void WriteEmpty(int style) => Separate();

    public void EndRow()
    {
        _row.Append("\r\n");
        _row.WriteUtf8To(_out);
        _row.Clear();
    }

    public void Complete()
    {
        // A csv file ends with its last record.
    }

    private void Separate()
    {
        if (!_firstCell)
        {
            _row.Append(_format.Delimiter);
        }

        _firstCell = false;
    }

    /// <summary>
    /// Formats a value straight into the row buffer, growing it until the value fits, and quotes it
    /// when it holds a character that needs quoting — a culture's decimal comma.
    /// </summary>
    private void AppendFormatted<T>(T value, ReadOnlySpan<char> format)
        where T : ISpanFormattable
    {
        int start = _row.AppendFormatted(value, format, _format.Culture);
        ReadOnlySpan<char> formatted = _row.Written[start..];

        if (formatted.IndexOfAny(NeedsQuotes) < 0)
        {
            return;
        }

        // Copied out first: the quoted value is written over the place it was formatted into.
        int written = formatted.Length;
        Span<char> copy = written <= FormattedOnStack ? stackalloc char[FormattedOnStack] : new char[written];
        formatted.CopyTo(copy);
        _row.Truncate(start);
        AppendQuoted(copy[..written], guard: false);
    }

    /// <summary>Appends a field in quotes, its quotes doubled, after the formula guard's apostrophe if asked.</summary>
    private void AppendQuoted(ReadOnlySpan<char> text, bool guard)
    {
        _row.Append('"');

        if (guard)
        {
            _row.Append('\'');
        }

        while (true)
        {
            int quote = text.IndexOf('"');

            if (quote < 0)
            {
                _row.Append(text);
                break;
            }

            _row.Append(text[..(quote + 1)]);
            _row.Append('"');
            text = text[(quote + 1)..];
        }

        _row.Append('"');
    }
}
