using System.Buffers;
using System.Text;

namespace TriasDev.Tabular.Csv;

/// <summary>
/// Writes csv: a row is formatted into a reused character buffer and encoded as UTF-8 into the
/// spill buffer when it ends.
/// </summary>
/// <remarks>
/// RFC 4180: a field holding the delimiter, a quote or a line break is quoted, its quotes doubled;
/// every record ends in CR LF. Nothing is allocated per row — the buffer is reused, and numbers and
/// dates are formatted straight into it.
/// </remarks>
internal sealed class CsvSheetWriter : ISheetWriter
{
    private const int InitialRowChars = 4 * 1024;

    /// <summary>A row buffer past this is let go after its row, rather than kept for the rest of the file.</summary>
    private const int RetainedRowChars = 1024 * 1024;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly SpillBuffer _out;
    private readonly CsvFormat _format;
    private readonly SearchValues<char> _needsQuotes;
    private char[] _row = new char[InitialRowChars];
    private int _length;
    private bool _firstCell = true;

    public CsvSheetWriter(SpillBuffer output, CsvFormat format)
    {
        _out = output;
        _format = format;
        _needsQuotes = SearchValues.Create($"{format.Delimiter}\"\r\n");
    }

    public long MaxRows => long.MaxValue;

    public bool AllowsSeveralSheets => false;

    /// <summary>The row buffer's current size, for the test that a huge row does not keep it.</summary>
    internal int RowBufferLength => _row.Length;

    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
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

    public string? WriteText(string value)
    {
        Separate();
        ReadOnlySpan<char> text = value;

        if (text.IndexOfAny(_needsQuotes) < 0)
        {
            Append(text);
            return null;
        }

        Append('"');

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
        return null;
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

    /// <summary>Formats a value straight into the row buffer, growing it until the value fits.</summary>
    private void AppendFormatted<T>(T value, ReadOnlySpan<char> format)
        where T : ISpanFormattable
    {
        Reserve(64);
        int written;

        while (!value.TryFormat(_row.AsSpan(_length), out written, format, _format.Culture))
        {
            Array.Resize(ref _row, _row.Length * 2);
        }

        _length += written;
    }

    private void Reserve(int extra)
    {
        if (_length + extra > _row.Length)
        {
            Array.Resize(ref _row, Math.Max(_row.Length * 2, _length + extra));
        }
    }
}
