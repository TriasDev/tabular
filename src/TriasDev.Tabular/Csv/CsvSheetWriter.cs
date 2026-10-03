using System.Buffers;
using System.Text;

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
    private const int InitialRowChars = 4 * 1024;

    /// <summary>A row buffer past this is let go after its row, rather than kept for the rest of the file.</summary>
    private const int RetainedRowChars = 1024 * 1024;

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
    private char[] _row = new char[InitialRowChars];
    private int _length;
    private bool _firstCell = true;

    public CsvSheetWriter(SpillBuffer output, CsvFormat format)
    {
        _out = output;
        _format = format;
    }

    public long MaxRows => long.MaxValue;

    public bool AllowsSeveralSheets => false;

    /// <summary>The row buffer's current size, for the test that a huge row does not keep it.</summary>
    internal int RowBufferLength => _row.Length;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
        _columnCount = columns.Length;

        if (_format.ByteOrderMark)
        {
            _out.Write(Utf8Bom);
        }
    }

    public void BeginRow()
    {
        _length = 0;
        _firstCell = true;
    }

    public string? WriteHeader(string value) => WriteField(value, guard: false, column: -1);

    public string? WriteText(string value, int column)
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
                Append('\'');
            }

            Append(text);
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

    public string? WriteLong(long value)
    {
        Separate();
        AppendFormatted(value, default);
        return null;
    }

    public string? WriteDecimal(decimal value)
    {
        Separate();
        AppendFormatted(value, default);
        return null;
    }

    public string? WriteDouble(double value)
    {
        Separate();
        AppendFormatted(value, "R");
        return null;
    }

    public string? WriteDate(DateTime value, bool hasTime)
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

    public void WriteBoolean(bool value)
    {
        Separate();
        Append(value ? "true" : "false");
    }

    public void WriteEmpty() => Separate();

    public void EndRow()
    {
        Append("\r\n");
        Span<byte> target = _out.GetSpan(Encoding.UTF8.GetMaxByteCount(_length));
        _out.Advance(Encoding.UTF8.GetBytes(_row.AsSpan(0, _length), target));

        if (_row.Length > RetainedRowChars)
        {
            _row = new char[InitialRowChars];
        }
    }

    public void Complete()
    {
        // A csv file ends with its last record.
    }

    private void Separate()
    {
        if (!_firstCell)
        {
            Append(_format.Delimiter);
        }

        _firstCell = false;
    }

    private void Append(char value)
    {
        Reserve(1);
        _row[_length++] = value;
    }

    private void Append(ReadOnlySpan<char> value)
    {
        Reserve(value.Length);
        value.CopyTo(_row.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>
    /// Formats a value straight into the row buffer, growing it until the value fits, and quotes it
    /// when it holds a character that needs quoting — a culture's decimal comma.
    /// </summary>
    private void AppendFormatted<T>(T value, ReadOnlySpan<char> format)
        where T : ISpanFormattable
    {
        Reserve(64);
        int start = _length;
        int written;

        while (!value.TryFormat(_row.AsSpan(start), out written, format, _format.Culture))
        {
            Array.Resize(ref _row, _row.Length * 2);
        }

        _length += written;
        ReadOnlySpan<char> formatted = _row.AsSpan(start, written);

        if (formatted.IndexOfAny(NeedsQuotes) < 0)
        {
            return;
        }

        // Copied out first: the quoted value is written over the place it was formatted into.
        Span<char> copy = written <= FormattedOnStack ? stackalloc char[FormattedOnStack] : new char[written];
        formatted.CopyTo(copy);
        _length = start;
        AppendQuoted(copy[..written], guard: false);
    }

    /// <summary>Appends a field in quotes, its quotes doubled, after the formula guard's apostrophe if asked.</summary>
    private void AppendQuoted(ReadOnlySpan<char> text, bool guard)
    {
        Append('"');

        if (guard)
        {
            Append('\'');
        }

        while (true)
        {
            int quote = text.IndexOf('"');

            if (quote < 0)
            {
                Append(text);
                break;
            }

            Append(text[..(quote + 1)]);
            Append('"');
            text = text[(quote + 1)..];
        }

        Append('"');
    }

    private void Reserve(int extra)
    {
        if (_length + extra > _row.Length)
        {
            Array.Resize(ref _row, Math.Max(_row.Length * 2, _length + extra));
        }
    }
}
