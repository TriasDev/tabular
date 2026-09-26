using System.Text;

using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular.Ods;

/// <summary>
/// The names of the tables in a content part, found by a pass over its bytes rather than its XML.
/// </summary>
/// <remarks>
/// <para>
/// All of a spreadsheet's tables live in one <c>content.xml</c>, and the sheets have to be listed
/// before any is read, so the cursor passes over the part once at construction. Tokenizing it as
/// the reading does cost almost half of a whole read — the part is large, a cell being half a
/// dozen tags — while the pass needs only the few start tags named <c>table</c>. So it searches
/// for <c>&lt;</c>, looks at the name behind it, and takes a tag apart only when that is a table.
/// </para>
/// <para>
/// It must see the same tables the reading does: an element whose local name is <c>table</c>,
/// with content, wherever it stands; not one inside a comment, CDATA or processing instruction.
/// Other tags are not taken apart, which is sound for well-formed XML, where no <c>&lt;</c> stands
/// inside an attribute value. A malformed file that puts one there can make the two passes count
/// differently; the list then names a sheet the reading cannot find, and moving to it fails as a
/// truncated file — refused, never read as another sheet's rows.
/// </para>
/// </remarks>
internal sealed class TableNameScan
{
    /// <summary>
    /// The longest table start tag held whole: the reading's ceiling for one node, in characters,
    /// at the most bytes UTF-8 spends on one.
    /// </summary>
    private const int MaxTagBytes = 3 * 16 * 1024 * 1024;

    /// <summary>An element name longer than this is not a table's, whatever its prefix.</summary>
    private const int MaxNameBytes = 256;

    private readonly Stream _stream;
    private byte[] _buffer = new byte[64 * 1024];
    private int _position;
    private int _length;
    private bool _endOfStream;

    private TableNameScan(Stream stream) => _stream = stream;

    /// <summary>
    /// The tables' names in document order, or null when the part is not UTF-8 and the caller has
    /// to tokenize it instead.
    /// </summary>
    public static List<string>? TryRead(Stream content, int maxSheets, CancellationToken cancellationToken)
    {
        TableNameScan scan = new(content);

        if (scan.Ensure(2) && (scan._buffer[0], scan._buffer[1]) is (0xFE, 0xFF) or (0xFF, 0xFE))
        {
            return null;                    // a UTF-16 byte order mark
        }

        return scan.Read(maxSheets, cancellationToken);
    }

    private List<string> Read(int maxSheets, CancellationToken cancellationToken)
    {
        List<string> names = [];
        int sinceCheck = 0;

        while (true)
        {
            if (++sinceCheck == 4096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!SeekTagStart() || !Ensure(2))
            {
                return names;
            }

            if (!Step(names, maxSheets))
            {
                return names;
            }
        }
    }

    /// <summary>
    /// Deals with the markup at a <c>&lt;</c>: skips it, or lists the table it opens; false when the
    /// part ends inside it.
    /// </summary>
    private bool Step(List<string> names, int maxSheets)
    {
        byte next = _buffer[_position + 1];

        if (next is (byte)'?' or (byte)'!')
        {
            return SkipMarkup(next);
        }

        if (next == (byte)'/' || !NamesTable())
        {
            _position++;
            return true;
        }

        return ReadTable(names, maxSheets);
    }

    /// <summary>
    /// Takes apart the table start tag at the scan position and lists its name; false when the part
    /// ends inside the tag.
    /// </summary>
    private bool ReadTable(List<string> names, int maxSheets)
    {
        int end = FindTagEnd();

        if (end < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> tag = _buffer.AsSpan(_position + 1, end - _position - 1);
        _position = end + 1;

        if (tag[^1] == (byte)'/')
        {
            return true;                    // a table with no content, which the reading passes over too
        }

        if (names.Count >= maxSheets)
        {
            throw new TabularLimitException(nameof(OdsCursorOptions.MaxSheets), maxSheets,
                $"The spreadsheet declares more than the {maxSheets} sheets allowed.");
        }

        names.Add(NameAttribute(tag));
        return true;
    }

    /// <summary>Moves to the next <c>&lt;</c>, false at the end of the part.</summary>
    private bool SeekTagStart()
    {
        while (true)
        {
            int found = _buffer.AsSpan(_position, _length - _position).IndexOf((byte)'<');

            if (found >= 0)
            {
                _position += found;
                return true;
            }

            _position = _length;

            if (!Refill())
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Skips a declaration, processing instruction, comment or CDATA section, as the reading does;
    /// false when the part ends inside it.
    /// </summary>
    private bool SkipMarkup(byte next)
    {
        if (next == (byte)'?')
        {
            return SkipThrough("?>"u8);
        }

        if (Starts("<!--"u8))
        {
            return SkipThrough("-->"u8);
        }

        return Starts("<![CDATA["u8) ? SkipThrough("]]>"u8) : SkipThrough(">"u8);
    }

    /// <summary>Whether the tag at the scan position is an element whose local name is <c>table</c>.</summary>
    private bool NamesTable()
    {
        int length = 0;

        while (true)
        {
            if (!Ensure(length + 2))
            {
                return false;
            }

            byte b = _buffer[_position + 1 + length];

            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'/' or (byte)'>')
            {
                break;
            }

            if (++length > MaxNameBytes)
            {
                return false;
            }
        }

        ReadOnlySpan<byte> name = _buffer.AsSpan(_position + 1, length);
        int colon = name.IndexOf((byte)':');

        return name[(colon + 1)..].SequenceEqual("table"u8);
    }

    /// <summary>
    /// The index of the bracket closing the tag at the scan position, brackets inside an attribute
    /// value aside; -1 when the part ends first.
    /// </summary>
    private int FindTagEnd()
    {
        int offset = 1;
        byte quote = 0;

        while (true)
        {
            for (; _position + offset < _length; offset++)
            {
                byte b = _buffer[_position + offset];

                if (quote != 0)
                {
                    quote = b == quote ? (byte)0 : quote;
                }
                else if (b is (byte)'"' or (byte)'\'')
                {
                    quote = b;
                }
                else if (b == (byte)'>')
                {
                    return _position + offset;
                }
            }

            if (!Ensure(offset + 1))
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// The value of the tag's first attribute whose local name is <c>name</c>, entities resolved;
    /// empty when it has none.
    /// </summary>
    private static string NameAttribute(ReadOnlySpan<byte> tag)
    {
        int space = tag.IndexOfAny(Whitespace);

        if (space < 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> rest = tag[space..];

        while (TryNextAttribute(ref rest, out ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> value))
        {
            if (attribute[(attribute.IndexOf((byte)':') + 1)..].SequenceEqual("name"u8))
            {
                return SheetScanner.DecodeAttribute(Encoding.UTF8.GetString(value));
            }
        }

        return string.Empty;
    }

    private static ReadOnlySpan<byte> Whitespace => " \t\r\n"u8;

    /// <summary>Takes the next <c>name="value"</c> off the front of a tag's attributes.</summary>
    private static bool TryNextAttribute(
        ref ReadOnlySpan<byte> rest, out ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> value)
    {
        attribute = value = default;
        rest = rest.TrimStart(Whitespace);
        int equals = rest.IndexOf((byte)'=');

        if (equals < 0)
        {
            return false;
        }

        attribute = rest[..equals].TrimEnd(Whitespace);
        rest = rest[(equals + 1)..].TrimStart(Whitespace);

        if (rest.IsEmpty || rest[0] is not ((byte)'"' or (byte)'\''))
        {
            return false;
        }

        int close = rest[1..].IndexOf(rest[0]);

        if (close < 0)
        {
            return false;
        }

        value = rest.Slice(1, close);
        rest = rest[(close + 2)..];
        return true;
    }

    private bool Starts(ReadOnlySpan<byte> text) =>
        Ensure(text.Length) && _buffer.AsSpan(_position, text.Length).SequenceEqual(text);

    /// <summary>Moves past the next occurrence of a terminator; false when the part ends first.</summary>
    private bool SkipThrough(ReadOnlySpan<byte> terminator)
    {
        _position += 2;

        while (true)
        {
            int found = _buffer.AsSpan(_position, _length - _position).IndexOf(terminator);

            if (found >= 0)
            {
                _position += found + terminator.Length;
                return true;
            }

            // Kept back in case the terminator straddles the refill.
            _position = Math.Max(_position, _length - terminator.Length + 1);

            if (!Refill())
            {
                return false;
            }
        }
    }

    /// <summary>Makes <paramref name="count"/> bytes from the scan position available, if the part has them.</summary>
    private bool Ensure(int count)
    {
        while (_length - _position < count)
        {
            if (count > _buffer.Length)
            {
                if (_buffer.Length >= MaxTagBytes)
                {
                    throw new TabularLimitException("MaxValueChars", MaxTagBytes / 3,
                        $"A single tag in the spreadsheet exceeds the {MaxTagBytes / 3} characters allowed.");
                }

                Array.Resize(ref _buffer, Math.Min(Math.Max(count, _buffer.Length * 2), MaxTagBytes));
            }

            if (!Refill())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Moves the unread bytes to the front and reads more behind them; false at the end.</summary>
    private bool Refill()
    {
        if (_endOfStream)
        {
            return false;
        }

        int kept = _length - _position;
        _buffer.AsSpan(_position, kept).CopyTo(_buffer);
        _position = 0;
        _length = kept;

        int read = _stream.Read(_buffer, _length, _buffer.Length - _length);

        if (read == 0)
        {
            _endOfStream = true;
            return false;
        }

        _length += read;
        return true;
    }
}
