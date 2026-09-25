using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

using TriasDev.Tabular.Abstractions;

namespace TriasDev.Tabular.Xlsx;

/// <summary>
/// Reads an OOXML workbook sheet by sheet, row by row.
/// </summary>
/// <remarks>
/// <para>
/// The parts a workbook needs before its first cell means anything — which sheets exist, which epoch
/// its dates count from, which styles render as dates — are read once at construction. They are
/// small. The sheet itself, which is not, is streamed.
/// </para>
/// <para>
/// The shared string table is read on first use rather than on open, so a workbook whose columns are
/// all numeric never pays for it.
/// </para>
/// </remarks>
public sealed class XlsxCursor : ITabularCursor
{
    /// <summary>
    /// The last column the format has, XFD.
    /// </summary>
    /// <remarks>
    /// A limit of the format, not a policy of ours, which is why exceeding it is malformed rather
    /// than merely large. It matters because a cell reference sizes an array: <c>r="ZZZZZZ1"</c> is
    /// column 321,272,406, and honouring it allocates gigabytes from a file of a few hundred bytes.
    /// </remarks>
    private const int MaxColumns = 16_384;

    private const string WorkbookPart = "xl/workbook.xml";
    private const string WorkbookRelationshipsPart = "xl/_rels/workbook.xml.rels";
    private const string StylesPart = "xl/styles.xml";
    private const string SharedStringsPart = "xl/sharedStrings.xml";

    /// <summary>
    /// The relationship namespace the format defines. A fixed identifier from the specification, not
    /// an address: nothing is ever fetched from it.
    /// </summary>
    private const string RelationshipNamespace =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly ZipArchive _package;

    /// <summary>
    /// Every part in the package, found without regard to case.
    /// </summary>
    /// <remarks>
    /// Part names in a package are compared case-insensitively. Excel writes them lower-case, so a
    /// reader that matches exactly works on every file Excel produced and fails on files from
    /// producers that chose otherwise — which is the worst kind of failure, because it looks like a
    /// corrupt file rather than a naming difference.
    /// </remarks>
    private readonly Dictionary<string, ZipArchiveEntry> _parts;
    private readonly bool _date1904;
    private readonly bool[] _styleIsDate;
    private readonly string[] _sheetPaths;

    private readonly StringBuilder _inlineText = new();
    private readonly XlsxCursorOptions _options;

    private string[]? _sharedStrings;
    private SheetScanner? _sheetScanner;
    private Stream? _sheetStream;
    private RawCell[] _cells = new RawCell[16];
    private int _cellCount;
    private bool _disposed;
    private bool _faulted;

    /// <summary>Opens a cursor over a workbook and positions it on the first sheet.</summary>
    /// <param name="stream">The package.</param>
    /// <param name="options">Reading options, or null for the defaults.</param>
    /// <param name="leaveOpen">Whether disposing the cursor leaves the stream open.</param>
    /// <exception cref="InvalidDataException">
    /// The stream is not a workbook, holds no sheet, or expands beyond the configured budget.
    /// </exception>
    public XlsxCursor(
        Stream stream,
        XlsxCursorOptions? options = null,
        bool leaveOpen = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        XlsxCursorOptions effective = options ?? XlsxCursorOptions.Default;
        _options = effective;

        _package = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);

        try
        {
            _parts = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

            int entries = 0;

            foreach (ZipArchiveEntry entry in _package.Entries)
            {
                // Counting what the archive holds, not what the dictionary keeps: zip allows two
                // entries to share a name, and a package of six hundred thousand entries all called
                // the same thing never advanced a count of distinct parts — so the ceiling was
                // bypassed outright and the cursor opened, holding 264 MB.
                if (++entries > effective.MaxPackageEntries)
                {
                    throw new InvalidDataException(
                        $"The package holds more than the {effective.MaxPackageEntries} parts allowed.");
                }

                _parts.TryAdd(entry.FullName, entry);
            }

            GuardExpansion(_package, effective.MaxUncompressedBytes);

            Dictionary<string, string> relationships = ReadRelationships(cancellationToken);
            (List<SheetInfo> sheets, List<string> paths, bool date1904) =
                ReadWorkbook(relationships, cancellationToken);

            if (sheets.Count == 0)
            {
                throw new InvalidDataException("The package holds no worksheet.");
            }

            Sheets = sheets;
            _sheetPaths = [.. paths];
            _date1904 = date1904;
            _styleIsDate = ReadDateStyles(cancellationToken);

            MoveToSheet(0);
        }
        catch
        {
            _package.Dispose();
            throw;
        }
    }

    /// <summary>Finds a part by name, without regard to case.</summary>
    private ZipArchiveEntry? Part(string name) =>
        _parts.TryGetValue(name, out ZipArchiveEntry? entry) ? entry : null;

    /// <inheritdoc />
    public TabularFormat Format => TabularFormat.Xlsx;

    /// <inheritdoc />
    public IReadOnlyList<SheetInfo> Sheets { get; }

    /// <inheritdoc />
    public int CurrentSheetIndex { get; private set; } = -1;

    /// <inheritdoc />
    public int CurrentRowNumber { get; private set; }

    /// <inheritdoc />
    public CursorDiagnostics Diagnostics { get; } = new();

    /// <inheritdoc />
    public ReadOnlySpan<RawCell> CurrentRow => _cells.AsSpan(0, _cellCount);

    /// <inheritdoc />
    public bool MoveToSheet(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (index < 0 || index >= _sheetPaths.Length)
        {
            return false;
        }

        CloseSheet();

        ZipArchiveEntry? entry = Part(_sheetPaths[index]);

        if (entry is null)
        {
            // Poisoned before it is thrown. CloseSheet has already run, so a caller that catches this
            // and reads on gets a cursor with no scanner — which reports a clean end of file. A
            // truncated file read as a shorter but entirely plausible table is the worst outcome this
            // reader has, and it was reachable from the public API by a malformed package.
            _faulted = true;

            throw new InvalidDataException($"The worksheet part {_sheetPaths[index]} is missing.");
        }

        _sheetStream = entry.Open();

        // The sheet is read by a scanner rather than by XmlReader, and only the sheet: the other
        // parts are small and read once, while this one carries every cell. XmlReader has no
        // span-returning attribute API, so it would allocate a string per cell for `r`, `t` and `s`
        // whether or not anything reads them - which measured as most of the cursor's cost.
        _sheetScanner = new SheetScanner(new StreamReader(_sheetStream, Encoding.UTF8, true, 64 * 1024));
        CurrentSheetIndex = index;
        CurrentRowNumber = 0;
        _cellCount = 0;

        return true;
    }

    /// <inheritdoc />
    public bool ReadRow(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        cancellationToken.ThrowIfCancellationRequested();

        if (_sheetScanner is null)
        {
            return false;
        }

        try
        {
            return ReadRowCore(_sheetScanner, cancellationToken);
        }
        catch
        {
            // Whatever it was, the scanner is somewhere inside a row whose cells are partly consumed,
            // so what remains of it is not a row. Poisoning here rather than at each throwing site,
            // because marking sites was tried and the flag reached one of four.
            _faulted = true;
            throw;
        }
    }

    private bool ReadRowCore(SheetScanner scanner, CancellationToken cancellationToken)
    {
        int sinceCheck = 0;

        while (scanner.Read())
        {
            // A sheet can hold a long stretch of markup this reader wants none of, and a cell can
            // send it off to load a shared string table of a million entries. Neither is bounded by
            // the row, so a check only at the top of the call would be a check that never runs
            // during the part that takes the time.
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (scanner.Kind != XmlNodeKind.Element || !scanner.Name.SequenceEqual("row"))
            {
                continue;
            }

            // The row's own number, not a count of how many were read: rows may be absent, and a
            // message pointing a user at the wrong line is worse than no message.
            CurrentRowNumber = scanner.TryGetAttribute("r", out ReadOnlySpan<char> reference)
                && int.TryParse(reference, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    ? number
                    : CurrentRowNumber + 1;

            _cellCount = 0;

            if (!scanner.IsEmptyElement)
            {
                ReadCells(scanner, cancellationToken);
            }

            return true;
        }

        return false;
    }

    /// <summary>Reads every cell up to the end of the row the scanner is inside.</summary>
    private void ReadCells(SheetScanner scanner, CancellationToken cancellationToken)
    {
        // Its own counter. The outer stride stops advancing the moment a row is entered, so a row
        // holding four hundred million elements this reader wants none of ran for five seconds with
        // nothing able to stop it.
        int sinceCheck = 0;

        while (scanner.Read())
        {
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (scanner.Kind == XmlNodeKind.EndElement && scanner.Name.SequenceEqual("row"))
            {
                return;
            }

            if (scanner.Kind == XmlNodeKind.Element && scanner.Name.SequenceEqual("c"))
            {
                ReadCell(scanner, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Reads one cell, taking everything it needs from the attribute spans before advancing.
    /// </summary>
    /// <remarks>
    /// The spans point into the scanner's buffer and are void after the next read, so the column
    /// index, the type and the style are converted here and now. That conversion is the point: an
    /// index and two small enumerations instead of three strings, on a path that runs to seventeen
    /// million cells.
    /// </remarks>
    private void ReadCell(SheetScanner scanner, CancellationToken cancellationToken)
    {
        int index = scanner.TryGetAttribute("r", out ReadOnlySpan<char> reference)
            ? ColumnIndex(reference)
            : _cellCount;

        if (index >= MaxColumns)
        {
            throw new InvalidDataException(
                index == TooManyLetters
                    ? $"A cell names a column beyond the {MaxColumns} the format has."
                    : $"A cell names column {index + 1}, beyond the {MaxColumns} columns the format has.");
        }

        CellValueType type = scanner.TryGetAttribute("t", out ReadOnlySpan<char> declared)
            ? Classify(declared)
            : CellValueType.Number;

        bool dateStyle = scanner.TryGetAttribute("s", out ReadOnlySpan<char> style)
            && int.TryParse(style, NumberStyles.Integer, CultureInfo.InvariantCulture, out int styleIndex)
            && styleIndex >= 0
            && styleIndex < _styleIsDate.Length
            && _styleIsDate[styleIndex];

        if (index < 0)
        {
            index = _cellCount;
        }

        Place(index, scanner.IsEmptyElement ? RawCell.Empty : ReadCellContent(scanner, type, dateStyle, cancellationToken));
    }

    /// <summary>Reads the children of a <c>&lt;c&gt;</c> element up to its end, and the value they hold.</summary>
    private RawCell ReadCellContent(
        SheetScanner scanner,
        CellValueType type,
        bool dateStyle,
        CancellationToken cancellationToken)
    {
        RawCell cell = RawCell.Empty;
        int sinceCheck = 0;

        while (scanner.Read())
        {
            // Its own counter: the row loop's stops advancing the moment a cell is entered, and one
            // cell can hold four hundred million elements this reader wants nothing from.
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (scanner.Kind == XmlNodeKind.EndElement && scanner.Name.SequenceEqual("c"))
            {
                break;
            }

            if (scanner.Kind != XmlNodeKind.Element)
            {
                continue;
            }

            if (scanner.Name.SequenceEqual("v"))
            {
                cell = ReadValue(scanner, type, dateStyle, cancellationToken);
            }
            else if (scanner.Name.SequenceEqual("is"))
            {
                cell = RawCell.FromText(ReadInlineString(scanner, cancellationToken));
            }
        }

        return cell;
    }

    private RawCell ReadValue(SheetScanner scanner, CellValueType type, bool dateStyle, CancellationToken cancellationToken)
    {
        if (scanner.IsEmptyElement || !scanner.Read() || scanner.Kind != XmlNodeKind.Text)
        {
            return RawCell.Empty;
        }

        ReadOnlySpan<char> text = scanner.Value;

        switch (type)
        {
            case CellValueType.SharedString:
                // Both halves of the range matter: NumberStyles.Integer accepts a leading sign, so
                // without the lower bound a value of -1 passes "is it below the table's length" and
                // indexes the array.
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    && index >= 0
                    && index < SharedStrings(cancellationToken).Length
                        ? RawCell.FromText(SharedStrings(cancellationToken)[index])
                        : RawCell.Empty;

            case CellValueType.Boolean:
                return RawCell.FromBoolean(text.SequenceEqual("1"));

            case CellValueType.Error:
                return RawCell.FromError(new string(text));

            case CellValueType.Text:
                return RawCell.FromText(new string(text));

            case CellValueType.IsoDate:
                return DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AllowWhiteSpaces,
                    out DateTime written)
                        ? RawCell.FromDate(written)
                        : RawCell.FromText(new string(text));
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return RawCell.FromText(new string(text));
        }

        return dateStyle && TryFromSerial(value, _date1904, out DateTime date)
            ? RawCell.FromDate(date)
            : RawCell.FromNumber(value);
    }

    /// <summary>
    /// Reads an inline string, concatenating every run it is built from.
    /// </summary>
    /// <remarks>
    /// A formatted string is split into runs, one per formatting change. Reading only the first — or
    /// only a direct text child, which a run-built string does not have — returns an empty string for
    /// a cell that plainly holds text, which is the defect this replaces.
    /// </remarks>
    private string ReadInlineString(SheetScanner scanner, CancellationToken cancellationToken)
    {
        if (scanner.IsEmptyElement)
        {
            return string.Empty;
        }

        _inlineText.Clear();
        bool inText = false;
        int sinceCheck = 0;

        while (scanner.Read())
        {
            // The value ceiling below bounds text, and this loop can run without producing any: an
            // element carrying nothing costs nothing to write and is not measured by any budget.
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (scanner.Kind)
            {
                case XmlNodeKind.EndElement when scanner.Name.SequenceEqual("is"):
                    return _inlineText.ToString();

                case XmlNodeKind.EndElement:
                    inText &= !scanner.Name.SequenceEqual("t");
                    break;

                case XmlNodeKind.Element:
                    inText = scanner.Name.SequenceEqual("t") && !scanner.IsEmptyElement;
                    break;

                case XmlNodeKind.Text when inText:
                    AppendInlineText(scanner.Value);
                    break;
            }
        }

        return _inlineText.ToString();
    }

    private void AppendInlineText(ReadOnlySpan<char> text)
    {
        _inlineText.Append(text);

        // The scanner's ceiling bounds one token; this bounds the value the tokens build. Measured, a
        // 307 KB workbook assembled eighty million characters into one cell without a single token
        // approaching the scanner's limit.
        if (_inlineText.Length > _options.MaxValueChars)
        {
            throw new InvalidDataException(
                $"A cell value exceeds the {_options.MaxValueChars} characters allowed.");
        }
    }

    /// <summary>What a cell's <c>t</c> attribute says it holds.</summary>
    private enum CellValueType
    {
        Number,
        SharedString,
        Boolean,
        Error,
        Text,

        /// <summary>
        /// A date written as an ISO-8601 string rather than as a serial number.
        /// </summary>
        /// <remarks>
        /// Excel does not emit this; the strict OOXML profile does, and so do several exporters. A
        /// reader that does not know the type falls back to text, which turns a date column into a
        /// text column without saying anything.
        /// </remarks>
        IsoDate,
    }

    private static CellValueType Classify(ReadOnlySpan<char> declared)
    {
        if (declared.SequenceEqual("s"))
        {
            return CellValueType.SharedString;
        }

        if (declared.SequenceEqual("b"))
        {
            return CellValueType.Boolean;
        }

        if (declared.SequenceEqual("e"))
        {
            return CellValueType.Error;
        }

        if (declared.SequenceEqual("d"))
        {
            return CellValueType.IsoDate;
        }

        if (declared.SequenceEqual("str") || declared.SequenceEqual("inlineStr"))
        {
            return CellValueType.Text;
        }

        return CellValueType.Number;
    }

    /// <summary>
    /// Puts a cell where its reference says it belongs, filling any gap before it.
    /// </summary>
    /// <remarks>
    /// By position, not by arrival. Cells normally come in order, but a reference is what states
    /// where a cell is, and a file that writes them out of order is stating something a reader that
    /// merely appends would get wrong without noticing.
    /// </remarks>
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

    /// <summary>The shared string table, read the first time a cell actually refers to it.</summary>
    /// <remarks>
    /// Loaded inside a row read, so the cancellation token of the call that triggered it is the one
    /// that has to reach here — this is the single longest thing the cursor ever does.
    /// </remarks>
    private string[] SharedStrings(CancellationToken cancellationToken) =>
        _sharedStrings ??= ReadSharedStrings(cancellationToken);

    private string[] ReadSharedStrings(CancellationToken cancellationToken)
    {
        ZipArchiveEntry? part = Part(SharedStringsPart);

        if (part is null)
        {
            return [];          // a workbook that writes every string inline has no table at all
        }

        List<string> values = [];

        using Stream stream = part.Open();
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true });

        int sinceCheck = 0;
        long characters = 0;

        while (reader.Read())
        {
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
            {
                continue;
            }

            if (values.Count >= _options.MaxSharedStrings)
            {
                throw new InvalidDataException(
                    $"The shared string table holds more than the {_options.MaxSharedStrings} entries allowed.");
            }

            string item = ReadSharedStringItem(reader, _options.MaxValueChars, cancellationToken);
            characters += item.Length;

            if (characters > _options.MaxSharedStringChars)
            {
                throw new InvalidDataException(
                    $"The shared string table holds more than the {_options.MaxSharedStringChars} "
                    + "characters allowed.");
            }

            values.Add(item);
        }

        // Both the list and the array it is copied into are alive for the length of this statement,
        // so the table costs twice its size at the moment it is finished. The entry ceiling above is
        // what bounds that; the count is not known in advance, so the copy cannot be avoided without
        // reading the part twice.
        return [.. values];
    }

    private static string ReadSharedStringItem(XmlReader reader, int maxChars, CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement)
        {
            return string.Empty;
        }

        StringBuilder text = new();
        bool advance = true;
        int sinceCheck = 0;

        while (true)
        {
            // The character ceiling below counts what <t> produces, and this loop can run without
            // producing any.
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (advance && !reader.Read())
            {
                break;
            }

            advance = true;

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                AppendText(reader, text, maxChars, cancellationToken);
                advance = false;
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Appends one element's text, in pieces, refusing it before the whole of it exists.
    /// </summary>
    /// <remarks>
    /// <c>ReadElementContentAsString</c> materialises the element first and hands back a string, so a
    /// ceiling tested against its length is a report rather than a bound: measured, a single
    /// two-hundred-million-character run in a 0.19 MB upload allocated 1,528 MB and stayed deaf to
    /// cancellation for the whole of it, before being told it was too long. Reading in chunks is the
    /// difference between refusing a value and surviving it.
    /// </remarks>
    private static void AppendText(
        XmlReader reader,
        StringBuilder text,
        int maxChars,
        CancellationToken cancellationToken)
    {
        if (reader.IsEmptyElement)
        {
            return;
        }

        char[] buffer = new char[8 * 1024];

        while (reader.Read())
        {
            if (reader.NodeType is XmlNodeType.EndElement or XmlNodeType.Element)
            {
                return;
            }

            if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA
                or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace))
            {
                continue;
            }

            int read;

            while ((read = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (text.Length + read > maxChars)
                {
                    throw new InvalidDataException(
                        $"A cell value exceeds the {maxChars} characters allowed.");
                }

                text.Append(buffer, 0, read);
            }
        }
    }

    private Dictionary<string, string> ReadRelationships(CancellationToken cancellationToken)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        int sinceCheck = 0;
        ZipArchiveEntry? part = Part(WorkbookRelationshipsPart);

        if (part is null)
        {
            return map;
        }

        using Stream stream = part.Open();
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true });

        while (reader.Read())
        {
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship")
            {
                continue;
            }

            string? id = reader.GetAttribute("Id");
            string? target = reader.GetAttribute("Target");

            if (id is not null && target is not null)
            {
                if (map.Count >= _options.MaxRelationships)
                {
                    throw new InvalidDataException(
                        $"The workbook declares more than the {_options.MaxRelationships} relationships allowed.");
                }

                map[id] = Bounded(target, "relationship target");
            }
        }

        return map;
    }

    private (List<SheetInfo> Sheets, List<string> Paths, bool Date1904) ReadWorkbook(
        Dictionary<string, string> relationships,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry part = Part(WorkbookPart)
            ?? throw new InvalidDataException("The package is not a workbook: xl/workbook.xml is missing.");

        List<SheetInfo> sheets = [];
        int sinceCheck = 0;
        List<string> paths = [];
        bool date1904 = false;

        using Stream stream = part.Open();
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true });

        while (reader.Read())
        {
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.LocalName == "workbookPr")
            {
                string? value = reader.GetAttribute("date1904");
                date1904 = value is "1" or "true";
            }
            else if (reader.LocalName == "sheet")
            {
                AddSheet(reader, relationships, sheets, paths);
            }
        }

        return (sheets, paths, date1904);
    }

    /// <summary>Records one <c>&lt;sheet&gt;</c> element: its name, and the part that holds it.</summary>
    private void AddSheet(
        XmlReader reader,
        Dictionary<string, string> relationships,
        List<SheetInfo> sheets,
        List<string> paths)
    {
        string name = reader.GetAttribute("name") ?? $"Sheet{sheets.Count + 1}";
        string? id = reader.GetAttribute("id", RelationshipNamespace);

        string target = id is not null && relationships.TryGetValue(id, out string? mapped)
            ? mapped
            : $"worksheets/sheet{sheets.Count + 1}.xml";

        if (sheets.Count >= _options.MaxSheets)
        {
            throw new InvalidDataException(
                $"The workbook declares more than the {_options.MaxSheets} sheets allowed.");
        }

        sheets.Add(new SheetInfo { Index = sheets.Count, Name = Bounded(name, "sheet name") });
        paths.Add(NormalisePart(Bounded(target, "sheet target")));
    }

    /// <summary>
    /// Refuses a string from the package's bookkeeping that is longer than one can honestly be.
    /// </summary>
    /// <remarks>
    /// Applied where the value is read, before it is stored anywhere, because the cost is the string
    /// itself and a ceiling that arrives after it has been built is a report rather than a bound.
    /// </remarks>
    private string Bounded(string value, string what)
    {
        if (value.Length > _options.MaxMetadataChars)
        {
            throw new InvalidDataException(
                $"A {what} is longer than the {_options.MaxMetadataChars} characters allowed.");
        }

        return value;
    }

    /// <summary>Turns a relationship target into a path inside the package.</summary>
    private static string NormalisePart(string target) =>
        target.StartsWith('/') ? target.TrimStart('/') : $"xl/{target}";

    /// <summary>
    /// Builds, per style index, whether that style renders its number as a date.
    /// </summary>
    /// <remarks>
    /// Flattened into an array once, because the alternative is what the prior art does: walk the
    /// style table for every styled cell. The formats are a linked list in the document object model,
    /// so that walk is linear, and doing it per cell makes the cost the product of cells and styles.
    /// </remarks>
    private bool[] ReadDateStyles(CancellationToken cancellationToken)
    {
        ZipArchiveEntry? part = Part(StylesPart);

        if (part is null)
        {
            return [];
        }

        Dictionary<int, string> customFormats = [];
        List<int> cellFormats = [];
        bool inCellXfs = false;

        using Stream stream = part.Open();
        using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true });

        int sinceCheck = 0;

        while (reader.Read())
        {
            // This runs in the constructor, before a caller has anything to cancel with, which is
            // exactly why it needs the token most: a style table nobody can interrupt spends its time
            // before the first row is read.
            if (++sinceCheck >= 4_096)
            {
                sinceCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Both lists, because the first version of this counted one of the two that grow in this
            // loop — and the uncounted one carries a string, so it costs more per entry.
            if (cellFormats.Count + customFormats.Count > _options.MaxCellFormats)
            {
                throw new InvalidDataException(
                    $"The style table holds more than the {_options.MaxCellFormats} cell formats allowed.");
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
            {
                inCellXfs = false;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                inCellXfs = ReadStyleElement(reader, inCellXfs, customFormats, cellFormats);
            }
        }

        return FlattenDateStyles(cellFormats, customFormats);
    }

    /// <summary>
    /// Takes what one element of the style table contributes, and says whether the reader is now
    /// inside <c>&lt;cellXfs&gt;</c>.
    /// </summary>
    private bool ReadStyleElement(
        XmlReader reader,
        bool inCellXfs,
        Dictionary<int, string> customFormats,
        List<int> cellFormats)
    {
        switch (reader.LocalName)
        {
            case "numFmt":
                string? code = reader.GetAttribute("formatCode");

                if (code is not null && TryParseFormatId(reader, out int numFmtId))
                {
                    customFormats[numFmtId] = Bounded(code, "number format code");
                }

                return inCellXfs;

            case "cellXfs":
                return true;

            case "xf" when inCellXfs:
                cellFormats.Add(TryParseFormatId(reader, out int formatId) ? formatId : 0);
                return true;

            default:
                return inCellXfs;
        }
    }

    private static bool TryParseFormatId(XmlReader reader, out int numFmtId)
    {
        string? id = reader.GetAttribute("numFmtId");

        numFmtId = 0;
        return id is not null && int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out numFmtId);
    }

    /// <summary>Resolves, per cell format, whether its number format is a date.</summary>
    private static bool[] FlattenDateStyles(List<int> cellFormats, Dictionary<int, string> customFormats)
    {
        bool[] isDate = new bool[cellFormats.Count];

        for (int i = 0; i < cellFormats.Count; i++)
        {
            int id = cellFormats[i];
            isDate[i] = IsBuiltInDateFormat(id)
                || (customFormats.TryGetValue(id, out string? code) && LooksLikeDateFormat(code));
        }

        return isDate;
    }

    /// <summary>The number format identifiers the specification reserves for dates and times.</summary>
    private static bool IsBuiltInDateFormat(int numFmtId) =>
        numFmtId is (>= 14 and <= 22) or (>= 45 and <= 47);

    /// <summary>
    /// Decides whether a custom format code renders a date, by looking for date tokens outside the
    /// parts of the code that are literal text.
    /// </summary>
    /// <remarks>
    /// The exclusions are what keep it honest. Quoted runs, bracketed sections and escaped characters
    /// are literal, so a currency format such as <c>#,##0 "Dm"</c> must not read as a date on account
    /// of its letter d.
    /// </remarks>
    private static bool LooksLikeDateFormat(string code)
    {
        bool inQuotes = false;
        bool inBrackets = false;
        int i = 0;

        while (i < code.Length)
        {
            char c = code[i];
            i++;

            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    continue;
                case '[':
                    inBrackets = true;
                    continue;
                case ']':
                    inBrackets = false;
                    continue;
                case '\\':
                    i++;                        // the escaped character is literal, so skip it
                    continue;
            }

            if (inQuotes || inBrackets)
            {
                continue;
            }

            if (c is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 's' or 'S' or 'm' or 'M')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a serial number to a date, or reports that it is not one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under the 1900 system the epoch is 1 January 1900, and the format treats the non-existent
    /// 29 February 1900 as a real day. Serial 60 <em>is</em> that non-day; serials above it are one
    /// further from the epoch than arithmetic suggests, and serials below it are not. So the two
    /// cases need different bases — 30 December 1899 above, 31 December 1899 below — and an earlier
    /// version of this method wrote two branches that were arithmetically identical, which is to say
    /// it wrote a comment rather than a correction. Serials under 60 came out a day early, and
    /// time-only cells, whose serial is less than one, came out a day early with them.
    /// </para>
    /// <para>
    /// The value comes from the file, so it can be anything a double can hold, including infinity and
    /// not-a-number. A serial outside the calendar is not a date; the cell keeps its number rather
    /// than failing the file, which is close to what a spreadsheet program shows for it.
    /// </para>
    /// </remarks>
    private static bool TryFromSerial(double serial, bool date1904, out DateTime date)
    {
        date = default;

        if (!double.IsFinite(serial))
        {
            return false;
        }

        DateTime epoch;
        double days;

        if (date1904)
        {
            epoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            days = serial;
        }
        else if (serial >= 60)
        {
            epoch = new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);
            days = serial;
        }
        else
        {
            epoch = new DateTime(1899, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);
            days = serial;
        }

        double ticks = days * TimeSpan.TicksPerDay;

        if (ticks < (DateTime.MinValue - epoch).Ticks || ticks > (DateTime.MaxValue - epoch).Ticks)
        {
            return false;
        }

        date = epoch.AddTicks((long)ticks);
        return true;
    }

    /// <summary>Turns a cell reference such as <c>AB12</c> into a zero-based column index.</summary>
    /// <remarks>
    /// Bounded as it is built, in both letter cases. Accumulating unchecked let a long reference wrap
    /// to a negative number, which passed the caller's width guard and was then normalised into the
    /// next column — a malformed reference silently accepted as data, in a reader that counts
    /// everything else it repairs. Anything past the format's width is returned as past it, so the
    /// caller refuses it rather than placing it.
    /// </remarks>
    /// <summary>What <see cref="ColumnIndex"/> returns for a reference too long to be a column.</summary>
    private const int TooManyLetters = int.MaxValue;

    private static int ColumnIndex(ReadOnlySpan<char> reference)
    {
        int index = 0;

        foreach (char c in reference)
        {
            int letter;

            if (c is >= 'A' and <= 'Z')
            {
                letter = c - 'A' + 1;
            }
            else if (c is >= 'a' and <= 'z')
            {
                letter = c - 'a' + 1;
            }
            else
            {
                break;
            }

            if (index > MaxColumns)
            {
                // Not a count: the accumulation was abandoned, so the real column is unknown and
                // reporting the ceiling as if it were the answer made the diagnostic useless for the
                // reference it was written for.
                return TooManyLetters;
            }

            index = (index * 26) + letter;
        }

        return index - 1;
    }

    private static void GuardExpansion(ZipArchive package, long budget)
    {
        long declared = 0;

        foreach (ZipArchiveEntry entry in package.Entries)
        {
            declared += entry.Length;

            if (declared > budget)
            {
                throw new InvalidDataException(
                    $"The package expands to more than the {budget} bytes allowed.");
            }
        }
    }

    private void CloseSheet()
    {
        _sheetScanner?.Dispose();
        _sheetScanner = null;
        _sheetStream?.Dispose();
        _sheetStream = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseSheet();
        _package.Dispose();
    }
}
