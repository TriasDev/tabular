using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular.Ods;

/// <summary>
/// Reads an OpenDocument spreadsheet (<c>.ods</c>) forward, one row at a time.
/// </summary>
/// <remarks>
/// <para>
/// Simpler than a workbook in the ways that matter: every cell says what it holds —
/// <c>office:value-type</c> with the value beside it — so there is no number format to guess from,
/// no style table, no epoch and no shared strings. The text is inline.
/// </para>
/// <para>
/// Harder in two. All sheets live in one part, <c>content.xml</c>, so the sheet names are gathered in
/// one pass when the cursor opens and a sheet is reached by reading past the ones before it. And rows
/// and cells carry repeat counts: an empty repeat only moves the position along, while a repeat that
/// holds a value is expanded — within <see cref="OdsCursorOptions.MaxColumns"/> and
/// <see cref="OdsCursorOptions.MaxRows"/>, because twenty characters of markup can ask for a billion
/// cells.
/// </para>
/// </remarks>
public sealed class OdsCursor : ITabularCursor
{
    private const string SpreadsheetMimetype = "application/vnd.oasis.opendocument.spreadsheet";

    /// <summary>The attributes the reader asks for, by local name.</summary>
    /// <remarks>
    /// <c>calcext:value-type</c> is the one taken whole. ODF has no error type, so LibreOffice writes a
    /// failed formula as an empty string with the error in its own extension attribute; read by its
    /// local name, it would be <c>office:value-type</c>'s twin and the error would be lost. The
    /// prefix is LibreOffice's fixed one rather than a resolved namespace — this is its extension
    /// and its spelling.
    /// </remarks>
    private static readonly string[] KeptAttributes =
    [
        "name", "value-type", "value", "date-value", "time-value", "boolean-value", "string-value",
        "number-columns-repeated", "number-rows-repeated", "c", ExtensionValueType,
    ];

    private const string ExtensionValueType = "calcext:value-type";

    private readonly OdsCursorOptions _options;
    private readonly ZipArchive _package;
    private readonly ZipArchiveEntry _content;
    private readonly StringBuilder _text = new();

    private RawCell[] _cells = new RawCell[16];
    private int _cellCount;

    private SheetScanner? _scanner;
    private CountingStream? _counter;

    /// <summary>How many tables the scanner has entered; the current one is this minus one.</summary>
    private int _tablesEntered;

    /// <summary>How many tables and DDE links are open around the scanner, as the name pass counts them.</summary>
    private int _nesting;

    /// <summary>The cells repeats have handed out beyond the ones written, across the file.</summary>
    private long _repeatedCells;

    private bool _sheetEnded;
    private int _repeatsLeft;
    private bool _disposed;
    private bool _faulted;

    /// <summary>Opens a cursor over an OpenDocument spreadsheet.</summary>
    /// <param name="stream">The package. Must be seekable.</param>
    /// <param name="options">Bounds, or null for the defaults.</param>
    /// <param name="leaveOpen">
    /// Whether the stream stays open once the cursor is disposed, or once opening it fails.
    /// </param>
    /// <param name="cancellationToken">Stops the opening, which reads the whole content part for the sheet names.</param>
    /// <exception cref="TabularFormatException">
    /// The stream is not a readable OpenDocument spreadsheet, or is another kind of OpenDocument file.
    /// </exception>
    /// <exception cref="TabularLimitException">The package exceeds one of the configured bounds.</exception>
    public OdsCursor(
        Stream stream,
        OdsCursorOptions? options = null,
        bool leaveOpen = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _options = options ?? OdsCursorOptions.Default;

        try
        {
            _options.Checked();
            _package = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
        }
        catch (Exception failed)
        {
            // Nothing owns the stream yet — the archive that would have closed it was never made.
            if (!leaveOpen)
            {
                stream.Dispose();
            }

            if (failed is InvalidDataException broken)
            {
                throw Corrupt(broken);
            }

            throw;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _content = FindContent();
            Sheets = ReadSheetNames(cancellationToken);

            if (Sheets.Count == 0)
            {
                throw new TabularFormatException(TabularFormatException.Unsupported, "The spreadsheet holds no sheet.");
            }

            MoveTo(0, cancellationToken);
        }
        catch (Exception malformed) when (malformed is XmlException or InvalidDataException or FormatException)
        {
            _package.Dispose();
            throw Corrupt(malformed);
        }
        catch
        {
            _package.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public TabularFormat Format => TabularFormat.Ods;

    /// <inheritdoc />
    public IReadOnlyList<SheetInfo> Sheets { get; }

    /// <inheritdoc />
    public int CurrentSheetIndex { get; private set; } = -1;

    /// <inheritdoc />
    public ReadOnlySpan<RawCell> CurrentRow => _cells.AsSpan(0, _cellCount);

    /// <inheritdoc />
    public int CurrentRowNumber { get; private set; }

    /// <inheritdoc />
    /// <remarks>OpenDocument states what it holds, so nothing is ever repaired.</remarks>
    public CursorDiagnostics Diagnostics { get; } = new();

    /// <inheritdoc />
    /// <remarks>
    /// Across the whole content part rather than per sheet, since every sheet lives in it: bytes the
    /// reader has taken from the part against the part's declared size.
    /// </remarks>
    public double? ReadFraction =>
        _counter is null || _content.Length == 0 ? null : Math.Min(1d, (double)_counter.BytesRead / _content.Length);

    /// <inheritdoc />
    /// <remarks>
    /// Reads forward through the content part to the sheet, from the top when it lies behind. The
    /// interface gives this no token; the constructor's first move is cancellable, later ones are not.
    /// </remarks>
    public bool MoveToSheet(int index) => MoveTo(index, CancellationToken.None);

    private bool MoveTo(int index, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (index < 0 || index >= Sheets.Count)
        {
            return false;
        }

        // Forward from where the scanner stands if it can get there; from the top of the part if the
        // sheet lies behind it.
        if (_scanner is null || index < _tablesEntered)
        {
            OpenContent();
        }

        int sinceCheck = 0;

        while (_tablesEntered <= index)
        {
            if (!_scanner!.Read())
            {
                throw Truncated();
            }

            Checkpoint(ref sinceCheck, cancellationToken);
            EnterOrLeaveScope(_scanner, index);
        }

        CurrentSheetIndex = index;
        CurrentRowNumber = 0;
        _cellCount = 0;
        _repeatsLeft = 0;
        _sheetEnded = false;
        _faulted = false;
        return true;
    }

    /// <summary>
    /// Keeps count of the tables and DDE links around the scanner, as the name pass does, and checks
    /// a sheet it enters against the name listed for it.
    /// </summary>
    private void EnterOrLeaveScope(SheetScanner scanner, int index)
    {
        if (scanner.Kind == XmlNodeKind.EndElement && IsScope(scanner.Name))
        {
            _nesting = Math.Max(0, _nesting - 1);
            return;
        }

        if (scanner.Kind != XmlNodeKind.Element || scanner.IsEmptyElement || !IsScope(scanner.Name))
        {
            return;
        }

        if (_nesting == 0 && scanner.Name.SequenceEqual(TableElement) && ++_tablesEntered > index)
        {
            string name = scanner.TryGetAttribute("name", out ReadOnlySpan<char> value)
                ? SheetScanner.DecodeAttribute(value)
                : string.Empty;

            // The two passes over the part disagree only on markup no writer produces; reading on
            // would hand out one sheet's rows under another's name.
            if (name != Sheets[index].Name)
            {
                throw new TabularFormatException(TabularFormatException.Corrupt,
                    "The spreadsheet's markup is malformed: its tables cannot be told apart reliably.");
            }
        }

        _nesting++;
    }

    private static bool IsScope(ReadOnlySpan<char> name) => name.SequenceEqual(TableElement) || name.SequenceEqual("dde-link");

    private const string TableElement = "table";

    /// <inheritdoc />
    public bool ReadRow(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_faulted)
        {
            throw new InvalidOperationException(
                "A read failed part-way through a row, so this cursor cannot continue. Open the file again to read it.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return ReadRowCore(cancellationToken);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private bool ReadRowCore(CancellationToken cancellationToken)
    {
        // A row repeated with a value in it is handed out again: the cells from last time stand.
        if (_repeatsLeft > 0)
        {
            _repeatsLeft--;
            Advance(1);
            return true;
        }

        _cellCount = 0;

        if (_sheetEnded)
        {
            return false;
        }

        SheetScanner scanner = _scanner!;
        int sinceCheck = 0;

        while (scanner.Read())
        {
            Checkpoint(ref sinceCheck, cancellationToken);

            switch (Classify(scanner))
            {
                case RowPlace.SheetEnd:
                    _sheetEnded = true;
                    return false;

                case RowPlace.Row when ReadRowElement(scanner, cancellationToken):
                    return true;
            }
        }

        throw Truncated();
    }

    private enum RowPlace
    {
        Other,
        Row,
        SheetEnd,
    }

    /// <summary>
    /// What the node means to the sheet: its end, one of its rows, or neither. A table inside the
    /// sheet is kept count of, so its end is not the sheet's and its rows are not the sheet's rows.
    /// </summary>
    private RowPlace Classify(SheetScanner scanner)
    {
        if (scanner.Kind == XmlNodeKind.EndElement && scanner.Name.SequenceEqual(TableElement))
        {
            if (--_nesting > 0)
            {
                return RowPlace.Other;
            }

            _nesting = 0;
            return RowPlace.SheetEnd;
        }

        if (scanner.Kind != XmlNodeKind.Element)
        {
            return RowPlace.Other;
        }

        if (!scanner.IsEmptyElement && scanner.Name.SequenceEqual(TableElement))
        {
            _nesting++;
            return RowPlace.Other;
        }

        // Header rows, row groups and the like are wrappers; the rows inside them are rows.
        return _nesting == 1 && scanner.Name.SequenceEqual("table-row") ? RowPlace.Row : RowPlace.Other;
    }

    /// <summary>Reads a row element; true when it holds a value and is handed out.</summary>
    private bool ReadRowElement(SheetScanner scanner, CancellationToken cancellationToken)
    {
        long repeat = Repeat(scanner, "number-rows-repeated");

        if (!scanner.IsEmptyElement)
        {
            ReadCells(scanner, cancellationToken);
        }

        if (_cellCount == 0)
        {
            // Empty, however many times: the row number moves on and nothing is handed out. It
            // stops one past the ceiling, so a value after it is refused and nothing wraps.
            long ceiling = Math.Min(int.MaxValue, _options.MaxRows + 1L);
            CurrentRowNumber = (int)(repeat >= ceiling - CurrentRowNumber ? ceiling : CurrentRowNumber + repeat);
            return false;
        }

        Advance(1);

        // Refused at once rather than after a billion rows have been handed out one at a time.
        if (repeat - 1 > _options.MaxRows - (long)CurrentRowNumber)
        {
            throw RowLimit();
        }

        _repeatsLeft = (int)(repeat - 1);
        CountRepeated(_repeatsLeft, _cellCount);
        return true;
    }

    /// <summary>Checks the token every few thousand nodes: often enough to stop a hostile file, rarely enough to cost nothing.</summary>
    private static void Checkpoint(ref int sinceCheck, CancellationToken cancellationToken)
    {
        if (++sinceCheck >= 4_096)
        {
            sinceCheck = 0;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void Advance(int rows)
    {
        long next = CurrentRowNumber + (long)rows;

        if (next > _options.MaxRows)
        {
            throw RowLimit();
        }

        CurrentRowNumber = (int)next;
    }

    /// <summary>Counts cells a repeat adds against the file's budget for them.</summary>
    private void CountRepeated(long copies, long cellsPerCopy)
    {
        long budget = _options.MaxRepeatedCells;

        if (copies > 0 && copies > (budget - _repeatedCells) / Math.Max(1, cellsPerCopy))
        {
            throw new TabularLimitException(nameof(OdsCursorOptions.MaxRepeatedCells), budget,
                $"The spreadsheet's repeats hand out more than the {budget} cells allowed.");
        }

        _repeatedCells += copies * cellsPerCopy;
    }

    /// <summary>Reads a row's cells up to its end, placing each at its column.</summary>
    private void ReadCells(SheetScanner scanner, CancellationToken cancellationToken)
    {
        long column = 0;
        int sinceCheck = 0;

        while (scanner.Read())
        {
            Checkpoint(ref sinceCheck, cancellationToken);

            if (scanner.Kind == XmlNodeKind.EndElement && scanner.Name.SequenceEqual("table-row"))
            {
                return;
            }

            bool covered = scanner.Kind == XmlNodeKind.Element && scanner.Name.SequenceEqual("covered-table-cell");

            if (!covered && (scanner.Kind != XmlNodeKind.Element || !scanner.Name.SequenceEqual("table-cell")))
            {
                continue;
            }

            long repeat = Repeat(scanner, "number-columns-repeated");

            // The covered part of a merge is an empty cell in its position, whatever it holds.
            RawCell cell = covered ? SkipCell(scanner, cancellationToken) : ReadCell(scanner, cancellationToken);

            if (!cell.IsEmpty)
            {
                PlaceRepeated(column, repeat, cell);
            }

            // Stops one past the ceiling, so a value after it is refused and nothing wraps.
            column = repeat > _options.MaxColumns - column ? _options.MaxColumns + 1L : column + repeat;
        }

        throw Truncated();
    }

    /// <summary>Reads one cell, its value from its attributes and its text from its paragraphs.</summary>
    private RawCell ReadCell(SheetScanner scanner, CancellationToken cancellationToken)
    {
        // Taken before reading on: an attribute points into the scanner's buffer, which the next
        // node may overwrite.
        bool isError = scanner.TryGetAttribute(ExtensionValueType, out ReadOnlySpan<char> extension)
            && extension.SequenceEqual("error");
        RawCell typed = isError || !scanner.TryGetAttribute("value-type", out ReadOnlySpan<char> type)
            ? RawCell.Empty
            : TypedValue(scanner, type);

        if (!typed.IsEmpty)
        {
            // The value is in the attributes; the paragraphs only show it formatted, so they are
            // passed over rather than assembled into a string nobody reads.
            SkipCell(scanner, cancellationToken);
            return typed;
        }

        string? stated = !isError && scanner.TryGetAttribute("string-value", out ReadOnlySpan<char> s)
            ? SheetScanner.DecodeAttribute(s)
            : null;

        if (stated is not null)
        {
            if (stated.Length > _options.MaxValueChars)
            {
                throw ValueLimit();
            }

            // The stated string is the value; the paragraphs only show it.
            SkipCell(scanner, cancellationToken);
            return RawCell.FromText(stated);
        }

        if (scanner.IsEmptyElement)
        {
            return RawCell.Empty;
        }

        ReadText(scanner, cancellationToken);

        if (isError)
        {
            // The error as the cell shows it — #N/A, #REF!, or LibreOffice's own Err:502.
            return RawCell.FromError(_text.ToString());
        }

        return RawCell.FromText(_text.ToString());
    }

    /// <summary>The value a cell declares in its attributes, or empty for text and for no type.</summary>
    private static RawCell TypedValue(SheetScanner scanner, ReadOnlySpan<char> type)
    {
        switch (type)
        {
            case "float" or "percentage" or "currency":
                return scanner.TryGetAttribute("value", out ReadOnlySpan<char> number)
                    && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                        ? RawCell.FromNumber(value)
                        : RawCell.Empty;

            case "date":
                // An ISO date, year first: a value with no date is no date, not today's.
                return scanner.TryGetAttribute("date-value", out ReadOnlySpan<char> date)
                    && date.IndexOf('-') >= 4
                    && DateReading.TryParseAsWritten(date, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out DateTime written)
                        ? RawCell.FromDate(written)
                        : RawCell.Empty;

            case "time":
                return scanner.TryGetAttribute("time-value", out ReadOnlySpan<char> time)
                    ? FromDuration(time.ToString())
                    : RawCell.Empty;

            case "boolean":
                return scanner.TryGetAttribute("boolean-value", out ReadOnlySpan<char> flag)
                    ? FromBooleanValue(flag)
                    : RawCell.Empty;

            default:
                return RawCell.Empty;
        }
    }

    /// <summary>xsd:boolean's four spellings; anything else is no boolean, and the cell reads as its text.</summary>
    private static RawCell FromBooleanValue(ReadOnlySpan<char> flag) => flag switch
    {
        "true" or "1" => RawCell.FromBoolean(true),
        "false" or "0" => RawCell.FromBoolean(false),
        _ => RawCell.Empty,
    };

    /// <summary>
    /// A time cell, an ISO 8601 duration, read as the workbook serial of as many days: a time of day
    /// on 31 December 1899, as an xlsx time-only cell, and a longer one — LibreOffice's form for a
    /// date-time under a time-only format — on the day the serial names.
    /// </summary>
    private static RawCell FromDuration(string duration)
    {
        TimeSpan span;

        try
        {
            span = XmlConvert.ToTimeSpan(duration);
        }
        catch (FormatException)
        {
            return RawCell.Empty;
        }
        catch (OverflowException)
        {
            return RawCell.Empty;
        }

        return span >= TimeSpan.Zero && XlsxCursor.TryFromSerial(span.TotalDays, date1904: false, out DateTime date)
            ? RawCell.FromDate(date)
            : RawCell.Empty;
    }

    // The state of one cell's text assembly: how deep the reader is inside the cell, and the depth
    // of the paragraph or annotation it is in, or -1.
    private int _textDepth;
    private int _paragraphDepth;
    private int _annotationDepth;
    private bool _anyParagraph;

    /// <summary>
    /// Assembles a cell's text into the reused builder: paragraphs joined by a line feed, spaces,
    /// tabs and line breaks as written, annotations left out.
    /// </summary>
    private void ReadText(SheetScanner scanner, CancellationToken cancellationToken)
    {
        _text.Clear();
        _textDepth = 0;
        _paragraphDepth = -1;
        _annotationDepth = -1;
        _anyParagraph = false;
        int sinceCheck = 0;

        while (scanner.Read())
        {
            Checkpoint(ref sinceCheck, cancellationToken);

            switch (scanner.Kind)
            {
                case XmlNodeKind.Element:
                    EnterElement(scanner);
                    break;

                case XmlNodeKind.EndElement when _textDepth == 0:
                    return;

                case XmlNodeKind.EndElement:
                    _textDepth--;
                    _annotationDepth = _textDepth == _annotationDepth ? -1 : _annotationDepth;
                    _paragraphDepth = _textDepth == _paragraphDepth ? -1 : _paragraphDepth;
                    break;

                case XmlNodeKind.Text when _paragraphDepth >= 0 && _annotationDepth < 0:
                    Append(scanner.Value);
                    break;
            }
        }

        throw Truncated();
    }

    private void EnterElement(SheetScanner scanner)
    {
        int depth = _textDepth;
        _textDepth += scanner.IsEmptyElement ? 0 : 1;

        if (_annotationDepth >= 0)
        {
            return;
        }

        ReadOnlySpan<char> name = scanner.Name;

        // A comment, and a sub-table inside the cell, are not the cell's text.
        if (name.SequenceEqual("annotation") || name.SequenceEqual(TableElement))
        {
            _annotationDepth = scanner.IsEmptyElement ? -1 : depth;
        }
        else if (name.SequenceEqual("p") || name.SequenceEqual("h"))
        {
            if (_anyParagraph)
            {
                Append("\n");
            }

            _anyParagraph = true;
            _paragraphDepth = scanner.IsEmptyElement ? -1 : depth;
        }
        else if (_paragraphDepth >= 0)
        {
            AppendInline(scanner, name);
        }
    }

    /// <summary>The elements inside a paragraph that stand for characters.</summary>
    private void AppendInline(SheetScanner scanner, ReadOnlySpan<char> name)
    {
        if (name.SequenceEqual("s"))
        {
            int count = scanner.TryGetAttribute("c", out ReadOnlySpan<char> c)
                && int.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                    ? n
                    : 1;

            if (_text.Length + (long)count > _options.MaxValueChars)
            {
                throw ValueLimit();
            }

            _text.Append(' ', count);
        }
        else if (name.SequenceEqual("tab"))
        {
            Append("\t");
        }
        else if (name.SequenceEqual("line-break"))
        {
            Append("\n");
        }
    }

    private void Append(ReadOnlySpan<char> text)
    {
        if (_text.Length + text.Length > _options.MaxValueChars)
        {
            throw ValueLimit();
        }

        _text.Append(text);
    }

    /// <summary>Reads past a cell without taking anything from it.</summary>
    private static RawCell SkipCell(SheetScanner scanner, CancellationToken cancellationToken)
    {
        if (scanner.IsEmptyElement)
        {
            return RawCell.Empty;
        }

        int depth = 0;
        int sinceCheck = 0;

        while (scanner.Read())
        {
            Checkpoint(ref sinceCheck, cancellationToken);

            if (scanner.Kind == XmlNodeKind.Element && !scanner.IsEmptyElement)
            {
                depth++;
            }
            else if (scanner.Kind == XmlNodeKind.EndElement && depth-- == 0)
            {
                return RawCell.Empty;
            }
        }

        throw Truncated();
    }

    /// <summary>A repeat count, one when absent; a malformed one is refused rather than guessed.</summary>
    private static long Repeat(SheetScanner scanner, string attribute)
    {
        if (!scanner.TryGetAttribute(attribute, out ReadOnlySpan<char> value))
        {
            return 1;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long repeat) && repeat >= 1
            ? repeat
            : throw new TabularFormatException(TabularFormatException.Corrupt,
                $"A repeat count of '{value.ToString()}' is not a positive whole number.");
    }

    /// <summary>Places a cell holding a value at every column its repeat covers, within the ceiling.</summary>
    private void PlaceRepeated(long column, long repeat, RawCell cell)
    {
        if (repeat > _options.MaxColumns - column)
        {
            throw new TabularLimitException(nameof(OdsCursorOptions.MaxColumns), _options.MaxColumns,
                $"A row fills more than the {_options.MaxColumns} columns allowed.");
        }

        CountRepeated(repeat - 1, 1);

        for (long i = 0; i < repeat; i++)
        {
            Place((int)(column + i), cell);
        }
    }

    private void Place(int index, RawCell cell)
    {
        if (index >= _cells.Length)
        {
            Array.Resize(ref _cells, Math.Max(index + 1, _cells.Length * 2));
        }

        for (int i = _cellCount; i < index; i++)
        {
            _cells[i] = RawCell.Empty;
        }

        _cells[index] = cell;
        _cellCount = Math.Max(_cellCount, index + 1);
    }

    /// <summary>
    /// Finds <c>content.xml</c>, counting the package against its bounds and refusing an OpenDocument
    /// file that is not a spreadsheet.
    /// </summary>
    private ZipArchiveEntry FindContent()
    {
        int entries = 0;
        long declared = 0;
        ZipArchiveEntry? content = null;
        ZipArchiveEntry? mimetype = null;

        foreach (ZipArchiveEntry entry in _package.Entries)
        {
            CountAgainstBounds(ref entries, ref declared, entry);
            content ??= entry.FullName == "content.xml" ? entry : null;
            mimetype ??= entry.FullName == "mimetype" ? entry : null;
        }

        if (mimetype is not null)
        {
            RefuseAnotherKind(mimetype);
        }

        return content ?? throw new TabularFormatException(TabularFormatException.Unsupported,
            "The package holds no content.xml, so it is not an OpenDocument spreadsheet.");
    }

    private void CountAgainstBounds(ref int entries, ref long declared, ZipArchiveEntry entry)
    {
        if (++entries > _options.MaxPackageEntries)
        {
            throw new TabularLimitException(nameof(OdsCursorOptions.MaxPackageEntries), _options.MaxPackageEntries,
                $"The package holds more than the {_options.MaxPackageEntries} parts allowed.");
        }

        declared += entry.Length;

        if (declared > _options.MaxUncompressedBytes)
        {
            throw new TabularLimitException(nameof(OdsCursorOptions.MaxUncompressedBytes), _options.MaxUncompressedBytes,
                $"The package expands to more than the {_options.MaxUncompressedBytes} bytes allowed.");
        }
    }

    /// <summary>Refuses an OpenDocument file that says it is something other than a spreadsheet.</summary>
    private static void RefuseAnotherKind(ZipArchiveEntry mimetype)
    {
        using StreamReader reader = new(mimetype.Open());
        char[] head = new char[128];
        int read = reader.ReadBlock(head, 0, head.Length);

        if (!head.AsSpan(0, read).Trim().SequenceEqual(SpreadsheetMimetype))
        {
            throw new TabularFormatException(TabularFormatException.Unsupported,
                "This OpenDocument file is not a spreadsheet.");
        }
    }

    /// <summary>One pass over the content part for the tables' names.</summary>
    private List<SheetInfo> ReadSheetNames(CancellationToken cancellationToken)
    {
        using (Stream bytes = _content.Open())
        {
            List<string>? names = TableNameScan.TryRead(bytes, _options.MaxSheets, cancellationToken);

            if (names is not null)
            {
                return [.. names.Select((name, index) => new SheetInfo { Index = index, Name = name, Format = TabularFormat.Ods })];
            }
        }

        // A part in UTF-16, which the byte pass does not read: tokenized, as the reading will.
        List<SheetInfo> sheets = [];

        using SheetScanner scanner = new(
            new StreamReader(_content.Open(), Encoding.UTF8, true, 64 * 1024), keptLocalNames: KeptAttributes);
        int sinceCheck = 0;
        int depth = 0;

        while (scanner.Read())
        {
            Checkpoint(ref sinceCheck, cancellationToken);

            // The same sheets the byte pass lists: tables standing in no table and no DDE link.
            if (scanner.Kind == XmlNodeKind.EndElement && IsScope(scanner.Name))
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (scanner.Kind != XmlNodeKind.Element || scanner.IsEmptyElement || !IsScope(scanner.Name)
                || depth++ > 0 || !scanner.Name.SequenceEqual(TableElement))
            {
                continue;
            }

            if (sheets.Count >= _options.MaxSheets)
            {
                throw new TabularLimitException(nameof(OdsCursorOptions.MaxSheets), _options.MaxSheets,
                    $"The spreadsheet declares more than the {_options.MaxSheets} sheets allowed.");
            }

            string name = scanner.TryGetAttribute("name", out ReadOnlySpan<char> value)
                ? SheetScanner.DecodeAttribute(value)
                : string.Empty;
            sheets.Add(new SheetInfo { Index = sheets.Count, Name = name, Format = TabularFormat.Ods });
        }

        return sheets;
    }

    private void OpenContent()
    {
        _scanner?.Dispose();
        _counter = new CountingStream(_content.Open());
        _scanner = new SheetScanner(new StreamReader(_counter, Encoding.UTF8, true, 64 * 1024), keptLocalNames: KeptAttributes);
        _tablesEntered = 0;
        _nesting = 0;
        _sheetEnded = false;
    }

    private TabularLimitException RowLimit() =>
        new(nameof(OdsCursorOptions.MaxRows), _options.MaxRows,
            $"A sheet has rows holding values beyond row {_options.MaxRows}.");

    private TabularLimitException ValueLimit() =>
        new(nameof(OdsCursorOptions.MaxValueChars), _options.MaxValueChars,
            $"A cell value exceeds the {_options.MaxValueChars} characters allowed.");

    private static TabularFormatException Corrupt(Exception inner) =>
        new(TabularFormatException.Corrupt, $"The spreadsheet is not readable: {inner.Message}", inner);

    private static TabularFormatException Truncated() =>
        new(TabularFormatException.Truncated, "The spreadsheet's content ends part-way through a sheet.");

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scanner?.Dispose();
        _scanner = null;
        _package.Dispose();
    }
}
