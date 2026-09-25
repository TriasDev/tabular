using System.Diagnostics.CodeAnalysis;
using System.Text;

using TriasDev.Tabular.Abstractions;

namespace TriasDev.Tabular.Csv;

/// <summary>
/// Reads a csv file record by record.
/// </summary>
/// <remarks>
/// <para>
/// A record is defined by quoting, not by lines. Counting lines is the single mistake that makes a
/// reader disagree with itself about how large a file is, and it is the one the prior art makes.
/// </para>
/// <para>
/// The reader repairs input no specification allows, because such input arrives constantly: quotes
/// standing inside fields that were never quoted, text after a closing quote, quotes that are never
/// closed at all. Refusing the file would be defensible and useless — one malformed address must not
/// fail an import of five million rows — so it recovers and counts what it repaired.
/// </para>
/// </remarks>
public sealed class CsvCursor : ITabularCursor
{
    private const int BufferSize = 64 * 1024;

    private readonly StreamReader _reader;
    private readonly CsvCursorOptions _options;
    private readonly char[] _buffer = new char[BufferSize];
    private readonly StringBuilder _field = new();

    /// <summary>
    /// Characters put back to be read again, which is how a record is re-parsed after an
    /// unterminated quote is abandoned.
    /// </summary>
    private readonly List<char> _pushback = [];
    private int _pushbackPosition;

    /// <summary>
    /// One character read ahead and not yet consumed.
    /// </summary>
    /// <remarks>
    /// Its own slot rather than the pushback list, because looking ahead happens at every line ending
    /// — to tell a lone carriage return from half of a pair — and routing five million of those
    /// through a list, as a one-character string, is five million allocations for one character each.
    /// </remarks>
    private int _peeked = -1;

    /// <summary>The raw text consumed since the current quoted field opened, kept so it can be replayed.</summary>
    private readonly StringBuilder _quotedRaw = new();

    private RawCell[] _cells = new RawCell[16];
    private int _cellCount;
    private int _bufferLength;
    private int _bufferPosition;
    private bool _endOfStream;
    private bool _disposed;
    private bool _faulted;

    /// <summary>Opens a cursor over a csv stream.</summary>
    /// <param name="stream">The file. Must be seekable when the dialect is to be detected.</param>
    /// <param name="sheetName">What to call the file's single sheet, normally the file's name.</param>
    /// <param name="options">Reading options, or null for the defaults.</param>
    /// <param name="leaveOpen">Whether disposing the cursor leaves the stream open.</param>
    public CsvCursor(Stream stream, string sheetName, CsvCursorOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(sheetName);

        _options = options ?? CsvCursorOptions.Default;
        Dialect = _options.Dialect ?? CsvDialectDetector.Detect(stream, _options.DialectProbeBytes);

        _reader = new StreamReader(
            stream,
            Dialect.Encoding,
            detectEncodingFromByteOrderMarks: true,
            BufferSize,
            leaveOpen);

        Sheets = [new SheetInfo { Index = 0, Name = sheetName }];
    }

    /// <summary>How this file is encoded and punctuated, and how that was decided.</summary>
    public CsvDialect Dialect { get; }

    /// <inheritdoc />
    public TabularFormat Format => TabularFormat.Csv;

    /// <inheritdoc />
    public IReadOnlyList<SheetInfo> Sheets { get; }

    /// <inheritdoc />
    public int CurrentSheetIndex => 0;

    /// <inheritdoc />
    public int CurrentRowNumber { get; private set; }

    /// <inheritdoc />
    public CursorDiagnostics Diagnostics { get; } = new();

    /// <inheritdoc />
    public ReadOnlySpan<RawCell> CurrentRow => _cells.AsSpan(0, _cellCount);

    /// <inheritdoc />
    /// <remarks>A csv file has exactly one sheet, so only index zero exists.</remarks>
    public bool MoveToSheet(int index) => index == 0;

    /// <inheritdoc />
    public bool ReadRow(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        cancellationToken.ThrowIfCancellationRequested();

        _cellCount = 0;
        _field.Clear();

        try
        {
            return ReadRowCore(cancellationToken);
        }
        catch
        {
            // Whatever it was, the characters this row consumed are gone from the stream and what
            // remains of it is not a row.
            _faulted = true;
            throw;
        }
    }

    /// <remarks>
    /// The body runs inside a catch that poisons the cursor, rather than each throwing site marking
    /// itself. Marking sites was tried and it was wrong: the flag went on one of four, and the one
    /// that mattered most needed no cancellation at all — an over-long field threw from the middle of
    /// a record and the next read presented the remainder as a whole row, correctly numbered and
    /// silently missing its beginning. A rule that has to be remembered at every new throw will be
    /// forgotten at the next one; this one cannot be.
    /// </remarks>
    [SuppressMessage("Critical Code Smell", "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "A per-character state machine. Its six state variables live in locals the JIT keeps in registers; splitting it means passing them by reference or holding them in fields on every character of the file.")]
    private bool ReadRowCore(CancellationToken cancellationToken)
    {
        bool inQuotes = false;
        bool fieldWasQuoted = false;
        bool anythingSeen = false;
        bool atFieldStart = true;
        bool suppressQuote = false;         // set while replaying a field whose quote proved literal
        int quotedLines = 0;

        int sinceCheck = 0;

        while (true)
        {
            // Checked on a stride rather than per character: the check is cheap but not free, and a
            // row is normally over in a few hundred characters. Sixty-four kilobytes is far below a
            // delay anyone notices and far above the cost of asking.
            if (++sinceCheck >= 64 * 1024)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            int next = ReadChar();

            if (next < 0)
            {
                if (inQuotes)
                {
                    // The file ended inside a quoted field. Nothing to recover towards, so the text
                    // gathered so far is the value.
                    Diagnostics.RecoveredUnterminatedQuotes++;
                }

                if (!anythingSeen && _field.Length == 0 && _cellCount == 0)
                {
                    return false;
                }

                CompleteField(fieldWasQuoted);
                CurrentRowNumber++;
                return true;
            }

            char c = (char)next;

            if (inQuotes)
            {
                // Kept before anything is decided about it, so that the character which trips the
                // bound is part of what gets replayed. Keeping it afterwards loses the line ending
                // that terminated a record, fusing that record with the next and producing a value
                // the file never contained.
                _quotedRaw.Append(c);

                // A line ends on a line feed, or on a carriage return that is not followed by one.
                // Counting only line feeds left the bound inoperative on files written with classic
                // Mac line endings — the very files where one stray quote consumes everything.
                bool endsLine = c == '\n' || (c == '\r' && PeekChar() != '\n');

                if (endsLine && ++quotedLines > _options.MaxQuotedFieldLines)
                {
                    // This quote was never syntax. Replay everything it swallowed, with the quote
                    // itself as an ordinary character, and read the records that were hiding inside
                    // it.
                    Diagnostics.RecoveredUnterminatedQuotes++;
                    PushBack(Dialect.Quote + _quotedRaw.ToString());
                    _quotedRaw.Clear();
                    _field.Clear();
                    inQuotes = false;
                    fieldWasQuoted = false;
                    atFieldStart = true;
                    suppressQuote = true;
                    quotedLines = 0;
                    continue;
                }

                if (c != Dialect.Quote)
                {
                    AppendToField(c);
                    continue;
                }

                // Looked at rather than taken: an earlier version read the character and pushed it
                // back, and pushing back spliced the whole pending buffer. Once per closing quote,
                // that made a file of quoted fields cost quadratic time — eighteen seconds for 768 KB
                // and hours for a few megabytes, inside a single call that cancellation cannot reach.
                int peek = PeekChar();

                if (peek == Dialect.Quote)
                {
                    ReadChar();
                    AppendToField(Dialect.Quote);
                    _quotedRaw.Append(Dialect.Quote);
                    continue;
                }

                // Anything else closes the field. What follows is appended as ordinary text, which is
                // what makes `"C" Road` read as `C Road` instead of failing the file.
                inQuotes = false;
                continue;
            }

            if (c == Dialect.Quote && atFieldStart && !suppressQuote)
            {
                inQuotes = true;
                fieldWasQuoted = true;
                atFieldStart = false;
                anythingSeen = true;
                quotedLines = 0;
                _quotedRaw.Clear();
                continue;
            }

            suppressQuote = false;

            if (c == Dialect.Delimiter)
            {
                CompleteField(fieldWasQuoted);
                fieldWasQuoted = false;
                atFieldStart = true;
                anythingSeen = true;
                continue;
            }

            // A carriage return ends the record unless a line feed follows it, in which case that
            // line feed does. Files written on classic Mac systems, and by some exporters still, end
            // their lines with a lone carriage return; a reader that breaks only on a line feed reads
            // such a file as one enormous row and raises nothing at all.
            if (c == '\r' && PeekChar() == '\n')
            {
                continue;
            }

            if (c is '\r' or '\n')
            {
                CompleteField(fieldWasQuoted);
                CurrentRowNumber++;
                return true;
            }

            // Everything else is content, a quote included. A quote that is not where a field begins
            // is an inch mark or a letter name, not syntax.
            AppendToField(c);
            atFieldStart = false;
            anythingSeen = true;
        }
    }

    /// <summary>
    /// Adds a character to the field being built, refusing one that grows past the ceiling.
    /// </summary>
    /// <remarks>
    /// A field has no length limit in the format, and a reader that honours that has none either: one
    /// unterminated value in a modest upload is enough to spend a host's memory, since the text is
    /// held twice while a quoted field is open — once as the value and once as the raw text kept for
    /// a possible replay.
    /// </remarks>
    private void AppendToField(char c)
    {
        if (_field.Length >= _options.MaxFieldChars)
        {
            throw new InvalidDataException(
                $"A field exceeds the {_options.MaxFieldChars} characters allowed.");
        }

        _field.Append(c);
    }

    private void CompleteField(bool wasQuoted)
    {
        if (_cellCount >= _options.MaxColumns)
        {
            throw new InvalidDataException(
                $"A row has more than the {_options.MaxColumns} columns allowed.");
        }

        if (_cellCount == _cells.Length)
        {
            Array.Resize(ref _cells, _cells.Length * 2);
        }

        // An empty field and a quoted empty field are the same absence. The distinction exists in the
        // syntax but not in the data, and a profile that counted them apart would be reporting on
        // punctuation rather than on content.
        _ = wasQuoted;

        _cells[_cellCount++] = RawCell.FromText(_field.ToString());
        _field.Clear();
    }

    /// <summary>
    /// Refuses to read on from a position the cursor cannot account for.
    /// </summary>
    /// <remarks>
    /// Anything thrown part-way through a row leaves the reader inside that row, with what it already
    /// consumed gone from the stream: a cancellation, a ceiling, a malformed part. Reading on would
    /// present the remainder as a complete record, correctly numbered and quietly missing its
    /// beginning — worse than any refusal, because nothing about it looks wrong. The message said
    /// "cancelled" for a while after the flag stopped being about cancellation.
    /// </remarks>
    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException(
                "A read failed part-way through a row, so this cursor cannot continue. "
                + "Open the file again to read it.");
        }
    }

    private void PushBack(string text)
    {
        // Replayed text belongs before anything already read ahead, since it was read earlier.
        if (_peeked >= 0)
        {
            text += (char)_peeked;
            _peeked = -1;
        }

        // Anything still unread keeps its place behind the replayed text.
        if (_pushbackPosition < _pushback.Count)
        {
            List<char> remaining = _pushback.GetRange(_pushbackPosition, _pushback.Count - _pushbackPosition);
            _pushback.Clear();
            _pushback.AddRange(text);
            _pushback.AddRange(remaining);
        }
        else
        {
            _pushback.Clear();
            _pushback.AddRange(text);
        }

        _pushbackPosition = 0;
    }

    /// <summary>Looks at the next character without consuming it, or -1 at the end.</summary>
    private int PeekChar()
    {
        if (_peeked < 0)
        {
            _peeked = ReadChar();
        }

        return _peeked;
    }

    private int ReadChar()
    {
        if (_peeked >= 0)
        {
            int peeked = _peeked;
            _peeked = -1;
            return peeked;
        }

        if (_pushbackPosition < _pushback.Count)
        {
            return _pushback[_pushbackPosition++];
        }

        if (_bufferPosition == _bufferLength)
        {
            if (_endOfStream)
            {
                return -1;
            }

            _bufferLength = _reader.Read(_buffer, 0, _buffer.Length);
            _bufferPosition = 0;

            if (_bufferLength == 0)
            {
                _endOfStream = true;
                return -1;
            }
        }

        return _buffer[_bufferPosition++];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();
    }
}
