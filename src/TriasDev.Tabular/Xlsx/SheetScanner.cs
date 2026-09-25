using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace TriasDev.Tabular.Xlsx;

/// <summary>What the scanner is currently sitting on.</summary>
internal enum XmlNodeKind
{
    /// <summary>Nothing yet.</summary>
    None,

    /// <summary>An opening tag, possibly self-closing.</summary>
    Element,

    /// <summary>A closing tag.</summary>
    EndElement,

    /// <summary>Character data between tags.</summary>
    Text,

    /// <summary>The end of the part.</summary>
    Eof,
}

/// <summary>
/// A streaming XML reader for worksheet parts, returning spans rather than strings.
/// </summary>
/// <remarks>
/// <para>
/// It exists for one measured reason. <see cref="System.Xml.XmlReader"/> has no span-returning
/// attribute API, so every cell pays for a string per attribute — <c>r</c>, <c>t</c>, <c>s</c> —
/// whether or not anything reads them. On a million-row workbook that is most of the cursor's
/// eighty-eight bytes per cell, against a budget of sixty set in ADR-0001.
/// </para>
/// <para>
/// It is not a general XML reader and must not become one. It handles what a worksheet part
/// contains: elements, attributes, character data, self-closing tags, comments, processing
/// instructions and CDATA. Namespaces are reduced to local names, since a sheet uses one. Everything
/// else in the XML specification is deliberately absent, and the golden fixtures are what say the
/// handled set is enough.
/// </para>
/// </remarks>
internal sealed class SheetScanner : IDisposable
{
    private const int InitialBufferSize = 64 * 1024;

    /// <summary>
    /// The most characters one value may occupy.
    /// </summary>
    /// <remarks>
    /// The package's expansion budget counts bytes off the wire, which is not the same quantity: text
    /// decoded to UTF-16 doubles, and a buffer that doubles to hold one token peaks at three times
    /// its content while copying. Sixteen million characters is far above any cell a spreadsheet
    /// program will write and far below what it takes to exhaust a host.
    /// </remarks>
    private const int MaxBufferChars = 16 * 1024 * 1024;
    private const int MaxAttributes = 16;

    private readonly TextReader _reader;
    private readonly bool _leaveOpen;

    private char[] _buffer = new char[InitialBufferSize];
    private int _length;                    // valid characters in the buffer
    private int _position;                  // where scanning continues
    private bool _endOfStream;

    private int _nameStart;
    private int _nameLength;
    private int _valueStart;
    private int _valueLength;
    private int _attributeCount;
    private readonly (int NameStart, int NameLength, int ValueStart, int ValueLength)[] _attributes =
        new (int, int, int, int)[MaxAttributes];

    private char[] _decoded = new char[256];
    private int _decodedLength;
    private bool _valueIsDecoded;

    public SheetScanner(TextReader reader, bool leaveOpen = false)
    {
        _reader = reader;
        _leaveOpen = leaveOpen;
    }

    /// <summary>What the scanner is sitting on.</summary>
    public XmlNodeKind Kind { get; private set; } = XmlNodeKind.None;

    /// <summary>True when the current element closed itself.</summary>
    public bool IsEmptyElement { get; private set; }

    /// <summary>The current element's local name.</summary>
    public ReadOnlySpan<char> Name => _buffer.AsSpan(_nameStart, _nameLength);

    /// <summary>The current text node's content, with entities resolved.</summary>
    public ReadOnlySpan<char> Value =>
        _valueIsDecoded ? _decoded.AsSpan(0, _decodedLength) : _buffer.AsSpan(_valueStart, _valueLength);

    /// <summary>Whether the current element carries an attribute, and what it says.</summary>
    /// <remarks>
    /// The value is returned raw. A worksheet's structural attributes — a cell reference, a type, a
    /// style index — never contain an entity, and decoding every one of them for the sake of a case
    /// that does not arise would give back the allocation this class exists to avoid.
    /// </remarks>
    public bool TryGetAttribute(ReadOnlySpan<char> name, out ReadOnlySpan<char> value)
    {
        for (int i = 0; i < _attributeCount; i++)
        {
            (int nameStart, int nameLength, int valueStart, int valueLength) = _attributes[i];

            if (_buffer.AsSpan(nameStart, nameLength).SequenceEqual(name))
            {
                value = _buffer.AsSpan(valueStart, valueLength);
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Advances to the next node.</summary>
    /// <remarks>
    /// The node's extent is settled first, growing or compacting the buffer as needed, and only then
    /// are any offsets into it taken. The other order is a trap: compaction moves everything down,
    /// and an offset captured before it silently points at the wrong characters — which is a defect
    /// small fixtures cannot show, because they never fill a buffer.
    /// </remarks>
    public bool Read()
    {
        _attributeCount = 0;
        IsEmptyElement = false;
        _valueIsDecoded = false;

        while (true)
        {
            if (!EnsureAvailable(1))
            {
                Kind = XmlNodeKind.Eof;
                return false;
            }

            if (_buffer[_position] != '<')
            {
                return ReadText();
            }

            if (!EnsureAvailable(2))
            {
                Kind = XmlNodeKind.Eof;
                return false;
            }

            char next = _buffer[_position + 1];

            if (next is '?' or '!')
            {
                switch (ReadMarkup(next))
                {
                    case Markup.Skipped:
                        continue;

                    case Markup.CharacterData:
                        return ReadCharacterData();

                    default:
                        Kind = XmlNodeKind.Eof;
                        return false;
                }
            }

            return ReadTag(isEnd: next == '/');
        }
    }

    /// <summary>Reads the opening or closing tag that starts at the scan position.</summary>
    private bool ReadTag(bool isEnd)
    {
        int end = FindTagEnd();

        if (end < 0)
        {
            Kind = XmlNodeKind.Eof;
            return false;
        }

        return isEnd ? ReadEndElement(end) : ReadElement(end);
    }

    /// <summary>What a <c>&lt;?</c> or <c>&lt;!</c> construct turned out to be.</summary>
    private enum Markup
    {
        /// <summary>A declaration, processing instruction or comment, now behind the scan position.</summary>
        Skipped,

        /// <summary>A CDATA section, not yet consumed; it carries text.</summary>
        CharacterData,

        /// <summary>The part ended inside the construct.</summary>
        Eof,
    }

    /// <summary>
    /// Deals with the constructs a worksheet may carry but this reader has no use for, apart from
    /// CDATA, which it only recognises.
    /// </summary>
    /// <remarks>
    /// Kept out of <see cref="Read"/> because they are rare, and the loop that runs once per node is
    /// better off holding only the text-or-element path it takes almost every time.
    /// </remarks>
    private Markup ReadMarkup(char next)
    {
        if (next == '?')
        {
            return SkipThrough("?>") ? Markup.Skipped : Markup.Eof;
        }

        if (Matches("<!--"))
        {
            return SkipThrough("-->") ? Markup.Skipped : Markup.Eof;
        }

        if (Matches("<![CDATA["))
        {
            return Markup.CharacterData;
        }

        return SkipThrough(">") ? Markup.Skipped : Markup.Eof;
    }

    private bool ReadText()
    {
        int end = Find('<');

        if (end < 0)
        {
            end = _length;                  // the part ends in character data
        }

        int start = _position;
        _valueStart = start;
        _valueLength = end - start;
        _position = end;
        Kind = XmlNodeKind.Text;

        if (_buffer.AsSpan(start, _valueLength).Contains('&'))
        {
            Decode(start, _valueLength);
        }

        return _valueLength > 0;
    }

    private bool ReadCharacterData()
    {
        int end = Find("]]>");

        if (end < 0)
        {
            Kind = XmlNodeKind.Eof;
            return false;
        }

        int start = _position + "<![CDATA[".Length;

        Kind = XmlNodeKind.Text;
        _valueStart = start;
        _valueLength = end - start;
        _position = end + 3;
        return true;
    }

    private bool ReadEndElement(int end)
    {
        int start = _position + 2;          // past "</"

        _nameStart = start;
        _nameLength = end - start;
        TrimPrefix();

        // A closing tag may carry trailing whitespace before the bracket.
        while (_nameLength > 0 && char.IsWhiteSpace(_buffer[_nameStart + _nameLength - 1]))
        {
            _nameLength--;
        }

        _position = end + 1;
        Kind = XmlNodeKind.EndElement;
        return true;
    }

    private bool ReadElement(int end)
    {
        int start = _position + 1;          // past '<'
        int nameEnd = start;

        while (nameEnd < end && !char.IsWhiteSpace(_buffer[nameEnd]) && _buffer[nameEnd] != '/')
        {
            nameEnd++;
        }

        _nameStart = start;
        _nameLength = nameEnd - start;
        TrimPrefix();

        IsEmptyElement = _buffer[end - 1] == '/';
        ReadAttributes(nameEnd, IsEmptyElement ? end - 1 : end);

        _position = end + 1;
        Kind = XmlNodeKind.Element;
        return true;
    }

    /// <summary>Whether an attribute is one the cursor asks for: <c>r</c>, <c>t</c> or <c>s</c>.</summary>
    private bool IsKept(int nameStart, int nameLength) =>
        nameLength == 1 && _buffer[nameStart] is ('r' or 't' or 's');

    [SuppressMessage("Critical Code Smell", "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "A per-character scan over every tag of the worksheet. The nested loops are the tokenizer itself; a helper per step adds a call per character on the hottest path of xlsx reading.")]
    private void ReadAttributes(int from, int to)
    {
        int i = from;

        while (i < to)
        {
            while (i < to && char.IsWhiteSpace(_buffer[i]))
            {
                i++;
            }

            if (i >= to)
            {
                return;
            }

            int nameStart = i;

            while (i < to && _buffer[i] != '=' && !char.IsWhiteSpace(_buffer[i]))
            {
                i++;
            }

            int nameLength = i - nameStart;

            while (i < to && _buffer[i] != '=')
            {
                i++;
            }

            i++;                            // past '='

            while (i < to && char.IsWhiteSpace(_buffer[i]))
            {
                i++;
            }

            if (i >= to)
            {
                return;
            }

            char quote = _buffer[i];
            i++;

            int valueStart = i;

            while (i < to && _buffer[i] != quote)
            {
                i++;
            }

            // Only the attributes the cursor reads are kept — `r`, `t` and `s`, which are never
            // prefixed. Keeping every attribute up to a ceiling refused whole workbooks over
            // extension elements that carry twenty and more (sparkline groups do), and dropping the
            // ones past it would have read t="s" behind sixteen others as a number. Kept by name, a
            // cell's type survives any number of attributes it has no use for.
            if (IsKept(nameStart, nameLength))
            {
                if (_attributeCount >= MaxAttributes)
                {
                    // Only a hostile element repeats these; refused rather than truncated, so a
                    // value is never read under the wrong one.
                    throw new InvalidDataException(
                        $"An element repeats its r, t or s attribute more than {MaxAttributes} times.");
                }

                _attributes[_attributeCount++] = (nameStart, nameLength, valueStart, i - valueStart);
            }

            i++;                            // past the closing quote
        }
    }

    /// <summary>Drops a namespace prefix from the current name, keeping the local part.</summary>
    private void TrimPrefix()
    {
        for (int i = _nameStart; i < _nameStart + _nameLength; i++)
        {
            if (_buffer[i] == ':')
            {
                _nameLength = _nameStart + _nameLength - (i + 1);
                _nameStart = i + 1;
                return;
            }
        }
    }

    private bool Matches(string token)
    {
        return EnsureAvailable(token.Length)
            && _buffer.AsSpan(_position, token.Length).SequenceEqual(token);
    }

    /// <summary>Moves past a construct, or reports that the part ended inside it.</summary>
    private bool SkipThrough(string token)
    {
        int end = Find(token);

        if (end < 0)
        {
            return false;
        }

        _position = end + token.Length;
        return true;
    }

    /// <summary>
    /// Finds a character at or after the scan position, reading more of the part until it appears.
    /// </summary>
    /// <remarks>
    /// May compact or grow the buffer, which moves the scan position. Nothing may hold an offset
    /// across this call.
    /// </remarks>
    private int Find(char target)
    {
        while (true)
        {
            int found = _buffer.AsSpan(_position, _length - _position).IndexOf(target);

            if (found >= 0)
            {
                return _position + found;
            }

            if (!Refill())
            {
                return -1;
            }
        }
    }

    /// <inheritdoc cref="Find(char)" />
    private int Find(string target)
    {
        while (true)
        {
            int found = _buffer.AsSpan(_position, _length - _position).IndexOf(target);

            if (found >= 0)
            {
                return _position + found;
            }

            if (!Refill())
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// Finds the bracket that closes the tag beginning at the scan position, ignoring brackets that
    /// stand inside an attribute value.
    /// </summary>
    /// <remarks>
    /// A greater-than sign needs no escaping in XML — only <c>&lt;</c> and <c>&amp;</c> do — so
    /// <c>r="A&gt;1"</c> is a perfectly legal attribute. Searching for the first bracket ends the tag
    /// in the middle of that value, and everything after it is misread: the remaining attributes are
    /// lost, so a shared-string cell quietly becomes a number, with no exception and no diagnostic.
    /// </remarks>
    [SuppressMessage("Critical Code Smell", "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "A per-character scan run for every tag. Its quote state and resume-after-refill logic belong to one loop, and a call per chunk or character would cost on the hottest path of xlsx reading.")]
    private int FindTagEnd()
    {
        int i = _position;
        char quote = '\0';

        while (true)
        {
            for (; i < _length; i++)
            {
                char c = _buffer[i];

                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (c is '"' or '\'')
                {
                    quote = c;
                    continue;
                }

                if (c == '>')
                {
                    return i;
                }
            }

            int scanned = i - _position;

            if (!Refill())
            {
                return -1;
            }

            // Refill moves everything down to the scan position, so resume where we left off.
            i = _position + scanned;
        }
    }

    /// <summary>
    /// Makes at least this many characters available at the scan position.
    /// </summary>
    private bool EnsureAvailable(int count)
    {
        while (_length - _position < count)
        {
            if (!Refill())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Discards what is already scanned, then reads more. Returns false at the end of the part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It resets <see cref="_position"/> itself, and that is the point. An earlier version compacted
    /// the buffer and left the caller to reset the position only when more data arrived — so at the
    /// end of the part the data had moved and the position had not. The consequences were a value
    /// read from the middle of itself, a negative length, and worst of all a `false` from
    /// <see cref="Read"/> that the cursor took for a clean end of sheet: a truncated file read as a
    /// shorter but entirely plausible table, silently.
    /// </para>
    /// <para>
    /// When the token being scanned already fills the buffer the buffer grows instead, up to a
    /// ceiling. A single very long cell is legal; an unbounded one is a way to spend a host's memory
    /// from a small upload.
    /// </para>
    /// </remarks>
    private bool Refill()
    {
        if (_position > 0)
        {
            Array.Copy(_buffer, _position, _buffer, 0, _length - _position);
            _length -= _position;
            _position = 0;
        }
        else if (_length == _buffer.Length)
        {
            if (_buffer.Length >= MaxBufferChars)
            {
                throw new InvalidDataException(
                    $"A single value in the worksheet exceeds the {MaxBufferChars} characters allowed.");
            }

            Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, MaxBufferChars));
        }

        if (_endOfStream)
        {
            return false;
        }

        int read = _reader.Read(_buffer, _length, _buffer.Length - _length);

        if (read == 0)
        {
            _endOfStream = true;
            return false;
        }

        _length += read;
        return true;
    }

    /// <summary>
    /// Resolves XML entities into the scratch buffer.
    /// </summary>
    /// <remarks>
    /// Only reached when the text actually contains an ampersand, which in a worksheet means a string
    /// cell holding one of five characters. Numbers and dates never take this path.
    /// </remarks>
    private void Decode(int from, int length)
    {
        if (_decoded.Length < length)
        {
            _decoded = new char[Math.Max(length, _decoded.Length * 2)];
        }

        int written = 0;
        int i = from;
        int end = from + length;

        while (i < end)
        {
            char c = _buffer[i];

            if (c != '&')
            {
                _decoded[written++] = c;
                i++;
                continue;
            }

            int semicolon = -1;

            for (int j = i + 1; j < end && j <= i + 12; j++)
            {
                if (_buffer[j] == ';')
                {
                    semicolon = j;
                    break;
                }
            }

            if (semicolon < 0)
            {
                _decoded[written++] = c;    // a stray ampersand, which is not our problem to reject
                i++;
                continue;
            }

            ReadOnlySpan<char> entity = _buffer.AsSpan(i + 1, semicolon - i - 1);
            written += WriteEntity(entity, _decoded.AsSpan(written));
            i = semicolon + 1;
        }

        _decodedLength = written;
        _valueIsDecoded = true;
    }

    private static int WriteEntity(ReadOnlySpan<char> entity, Span<char> destination)
    {
        if (entity.SequenceEqual("amp"))
        {
            destination[0] = '&';
            return 1;
        }

        if (entity.SequenceEqual("lt"))
        {
            destination[0] = '<';
            return 1;
        }

        if (entity.SequenceEqual("gt"))
        {
            destination[0] = '>';
            return 1;
        }

        if (entity.SequenceEqual("quot"))
        {
            destination[0] = '"';
            return 1;
        }

        if (entity.SequenceEqual("apos"))
        {
            destination[0] = '\'';
            return 1;
        }

        // A character reference may name something that is not a character at all: a lone surrogate,
        // a code point above the last one, or zero. Constructing a Rune from any of those throws, and
        // a reader contracted to recover from malformed input must not fail on it — the reference is
        // kept verbatim instead, which is what the unknown-entity path below already does.
        if (entity.Length > 1 && entity[0] == '#')
        {
            int value = ParseCharacterReference(entity);

            if (value > 0 && Rune.TryCreate(value, out Rune rune))
            {
                return rune.EncodeToUtf16(destination);
            }
        }

        // Unknown: kept as written, since dropping it would silently change a value.
        destination[0] = '&';
        entity.CopyTo(destination[1..]);
        destination[entity.Length + 1] = ';';
        return entity.Length + 2;
    }

    private static int ParseCharacterReference(ReadOnlySpan<char> entity)
    {
        ReadOnlySpan<char> digits = entity[1..];
        bool hex = digits.Length > 0 && (digits[0] == 'x' || digits[0] == 'X');

        if (hex)
        {
            digits = digits[1..];
        }

        NumberStyles styles = hex ? NumberStyles.HexNumber : NumberStyles.Integer;

        return int.TryParse(digits, styles, CultureInfo.InvariantCulture, out int value) ? value : -1;
    }

    public void Dispose()
    {
        if (!_leaveOpen)
        {
            _reader.Dispose();
        }
    }
}
