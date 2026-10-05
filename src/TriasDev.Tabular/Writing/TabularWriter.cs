using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular;

/// <summary>
/// Writes a table into a stream, row by row: typed values in, a csv, xlsx or ods file (or a zip of csv sheets) out.
/// </summary>
/// <remarks>
/// <para>
/// Writes go synchronously into memory; <see cref="FlushAsync"/> moves that memory into the stream.
/// The stream itself is only ever written, flushed and closed asynchronously, so the writer serves an
/// ASP.NET Core response body, which refuses synchronous writes. Flush whenever
/// <see cref="FlushRecommended"/> is true, and memory stays flat however many rows the file has.
/// </para>
/// <para>
/// The file is valid only after <see cref="CompleteAsync"/>. A writer disposed without it, or after
/// an exception, leaves an incomplete file behind — for a workbook, plainly broken; for csv, a file
/// that looks whole. The caller discards it: aborts the response, deletes the file.
/// </para>
/// <para>Not thread-safe: one writer, one sequence of calls.</para>
/// </remarks>
public sealed class TabularWriter : IAsyncDisposable
{
    /// <summary>Past this many pending bytes, <see cref="FlushRecommended"/> turns true.</summary>
    internal const int FlushThreshold = 1024 * 1024;

    /// <summary>The most columns a sheet has: the workbook formats' own limit, and the csv reader's.</summary>
    internal const int MaxColumns = 16_384;

    private const double MaxWidth = 255;

    private const string FaultedMessage = "The writer failed earlier; the file is incomplete and nothing more can be written.";

    private readonly Stream _target;
    private readonly SpillBuffer _buffer;
    private readonly ISheetWriter _sheet;
    private readonly StyleTable _styles;
    private static int s_stamps;
    private readonly int _stamp = NextStamp();
    private readonly bool _leaveOpen;
    private State _state = State.Open;
    private WriteColumn[] _columns = [];
    private readonly HashSet<string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);
    private string _sheetName = string.Empty;
    private int _sheets;
    private int _rowNumber;
    private int _column;

    // Merges: per column, the rows a range covers there (first..last, 0 when none). Allocated on a
    // sheet's first merge, so a sheet without merges pays one null check per cell.
    private long[]? _coveredFrom;
    private long[]? _coveredThrough;
    private long _lastCoveredRow;
    private int _pendingRows;
    private int _pendingColumns;
    private int _merges;

    private TabularWriter(Stream target, TabularFormat format, SpillBuffer buffer, ISheetWriter sheet, StyleTable styles, bool leaveOpen)
    {
        _target = target;
        Format = format;
        _buffer = buffer;
        _sheet = sheet;
        _styles = styles;
        _leaveOpen = leaveOpen;
        MaxRows = sheet.MaxRows;
    }

    private enum State
    {
        Open,
        InSheet,
        InRow,
        Completed,
        Faulted,
        Disposed,
    }

    /// <summary>The format being written.</summary>
    public TabularFormat Format { get; }

    /// <summary>
    /// Whether enough is pending in memory that the caller should <see cref="FlushAsync"/> now.
    /// </summary>
    public bool FlushRecommended => _buffer.Pending >= FlushThreshold;

    /// <summary>
    /// The most rows a sheet holds, the header included: the format's limit, which for csv is the
    /// largest row number the reader gives. Settable only so a test can reach it without writing 2^31 rows.
    /// </summary>
    internal int MaxRows { get; set; }

    /// <summary>Bytes held in memory and not yet handed to the stream; a test hook for the memory bound.</summary>
    internal int PendingBytes => (int)Math.Min(int.MaxValue, _buffer.Pending);

    /// <summary>Creates a writer for a format, into a stream.</summary>
    /// <param name="stream">Where the file goes: a file, a blob, a response body. It need not seek.</param>
    /// <param name="format">The format to write: csv, a zip of csv sheets, xlsx or ods.</param>
    /// <param name="options">The format's knobs; checked here, before anything is written.</param>
    /// <exception cref="ArgumentException">The stream cannot be written, or an option cannot work.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A format this version does not write.</exception>
    /// <remarks>
    /// On failure the stream is closed — with <c>Dispose()</c>, since nothing was written to it —
    /// unless <see cref="TabularWriterOptions.LeaveOpen"/> is set.
    /// </remarks>
    public static TabularWriter Create(Stream stream, TabularFormat format, TabularWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        TabularWriterOptions effective = options ?? TabularWriterOptions.Default;

        try
        {
            if (!stream.CanWrite)
            {
                throw new ArgumentException("The stream cannot be written to.", nameof(stream));
            }

            if (effective.Csv is null)
            {
#pragma warning disable S3928 // Justification: the parameter name identifies the option being validated, not a method parameter
                throw new ArgumentNullException(nameof(TabularWriterOptions.Csv), $"{nameof(TabularWriterOptions)}.{nameof(TabularWriterOptions.Csv)} is null.");
#pragma warning restore S3928
            }

            if (effective.Xlsx is null)
            {
#pragma warning disable S3928 // Justification: the parameter name identifies the option being validated, not a method parameter
                throw new ArgumentNullException(nameof(TabularWriterOptions.Xlsx), $"{nameof(TabularWriterOptions)}.{nameof(TabularWriterOptions.Xlsx)} is null.");
#pragma warning restore S3928
            }

            if (effective.Ods is null)
            {
#pragma warning disable S3928 // Justification: the parameter name identifies the option being validated, not a method parameter
                throw new ArgumentNullException(nameof(TabularWriterOptions.Ods), $"{nameof(TabularWriterOptions)}.{nameof(TabularWriterOptions.Ods)} is null.");
#pragma warning restore S3928
            }

            if (effective.Zip is null)
            {
#pragma warning disable S3928 // Justification: the parameter name identifies the option being validated, not a method parameter
                throw new ArgumentNullException(nameof(TabularWriterOptions.Zip), $"{nameof(TabularWriterOptions)}.{nameof(TabularWriterOptions.Zip)} is null.");
#pragma warning restore S3928
            }

            StyleTable styles = new();

            switch (format)
            {
                case TabularFormat.Csv:
                    CsvFormat csv = effective.Csv.Resolve();
                    SpillBuffer buffer = new();
                    return new TabularWriter(stream, format, buffer, new CsvSheetWriter(buffer, csv), styles, effective.LeaveOpen);
                case TabularFormat.Zip:
                    ZipWriterOptions zip = effective.Zip.Checked();
                    SpillBuffer archive = new();
                    return new TabularWriter(stream, format, archive, new ZipCsvSheetWriter(archive, zip, effective.Csv.Resolve()), styles, effective.LeaveOpen);
                case TabularFormat.Xlsx:
                    XlsxWriterOptions xlsx = effective.Xlsx.Checked();
                    SpillBuffer workbook = new();
                    return new TabularWriter(stream, format, workbook, new XlsxSheetWriter(workbook, xlsx, styles), styles, effective.LeaveOpen);
                case TabularFormat.Ods:
                    OdsWriterOptions ods = effective.Ods.Checked();
                    SpillBuffer spreadsheet = new();
                    return new TabularWriter(stream, format, spreadsheet, new OdsSheetWriter(spreadsheet, ods, styles), styles, effective.LeaveOpen);
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, $"Writing {format} is not supported.");
            }
        }
        catch when (!effective.LeaveOpen)
        {
            CloseAfterFailedCreate(stream);
            throw;
        }
    }

    /// <summary>
    /// Closes the stream of a <see cref="Create"/> that failed, so that the exception which made it
    /// fail is the one the caller sees — not one the stream throws while closing.
    /// </summary>
    private static void CloseAfterFailedCreate(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception closing) when (closing is not OutOfMemoryException)
        {
            // Ignored: the caller is told why Create failed; a stream that also fails to close adds
            // nothing they could act on, and reporting it would hide the reason.
        }
    }

    /// <summary>Begins a sheet and writes its header row.</summary>
    /// <param name="name">The sheet's name. A workbook's names are 1 to 31 characters, none of [ ] : * ? / \, no apostrophe at either end, not "History", unique ignoring case. A csv file has one sheet, whose name is not written.</param>
    /// <param name="columns">The columns: 1 to 16,384, each with a header that is not empty, neither starts nor ends with whitespace, and is unique in the sheet, ignoring case.</param>
    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns) => BeginSheet(name, columns, null);

    /// <summary>Begins a sheet laid out by <paramref name="options"/> and writes its header row.</summary>
    /// <param name="name">The sheet's name. A workbook's names are 1 to 31 characters, none of [ ] : * ? / \, no apostrophe at either end, not "History", unique ignoring case. A csv file has one sheet, whose name is not written.</param>
    /// <param name="columns">The columns: 1 to 16,384, each with a header that is not empty, neither starts nor ends with whitespace, and is unique in the sheet, ignoring case.</param>
    /// <param name="options">The header style, frozen rows and columns, and filter; null for none.</param>
    /// <exception cref="ArgumentOutOfRangeException">A freeze outside the sheet: rows from 0 to the format's row limit less one, columns from 0 to the column count.</exception>
    /// <exception cref="ArgumentException">The <see cref="SheetOptions.HeaderStyle"/> has an alignment that is not a defined value, as <see cref="RegisterStyle"/> refuses.</exception>
    /// <exception cref="InvalidOperationException">The <see cref="SheetOptions.HeaderStyle"/> would be the file's 4097th distinct style, as <see cref="RegisterStyle"/> refuses; or a merged range of the previous sheet still covers rows the sheet did not write.</exception>
    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns, SheetOptions? options)
    {
        ExpectWritable();

        if (_state != State.Open && _state != State.InSheet)
        {
            throw Refuse("A sheet begins outside a row.");
        }

        ExpectRangesClosed();

        if (_sheets > 0 && !_sheet.AllowsSeveralSheets)
        {
            throw Refuse($"A {Format} file holds one sheet.");
        }

        if (name is null)
        {
            throw Faulting(new ArgumentNullException(nameof(name)));
        }

        if (NameProblem(name) is { } problem)
        {
            throw Faulting(new ArgumentException(problem, nameof(name)));
        }

        CheckColumns(columns);

        SheetOptions layout = options ?? SheetOptions.Default;

        if (layout.FreezeProblem(nameof(options), MaxRows, columns.Length) is { } freezeProblem)
        {
            throw Faulting(freezeProblem);
        }

        int headerStyle = layout.HeaderStyle is { } header ? AddStyle(header) : 0;

        _columns = columns.ToArray();
        _sheetName = name;
        _sheetNames.Add(name);
        _sheets++;
        _rowNumber = 0;
        _coveredFrom = null;
        _coveredThrough = null;
        _lastCoveredRow = 0;
        _lastStyles = null;
        _lastStyleIds = null;
        _merges = 0;
        _pendingRows = 0;
        _pendingColumns = 0;
        _sheet.BeginSheet(name, _columns, layout);

        StartRow();

        foreach (string headerText in _columns.Select(column => column.Header))
        {
            if (_sheet.WriteHeader(headerText, headerStyle) is { } code)
            {
                throw Faulting(new ArgumentException($"The header \"{headerText}\" cannot be written: {code}.", nameof(columns)));
            }

            _column++;
        }

        FinishRow();
    }

    /// <summary>Why the format refuses a sheet's name: the workbook rules first, then its own; null when it is fine.</summary>
    private string? NameProblem(string name) =>
        _sheet.NamesSheets ? SheetNames.Problem(name, _sheetNames) ?? _sheet.NameProblem(name) : null;

    /// <summary>Begins a row; its values follow, one per column, then <see cref="EndRow"/>.</summary>
    /// <exception cref="TabularWriteException">The sheet already holds as many rows as its format allows (<c>write.too-many-rows</c>, located at the last row it holds): 1,048,576 for xlsx and ods, <see cref="int.MaxValue"/> — the largest row number the reader gives — for csv and a zip of csv sheets. The writer is faulted.</exception>
    public void BeginRow()
    {
        ExpectWritable();

        if (_state != State.InSheet)
        {
            throw Refuse("A row begins inside a sheet, after the previous row ended.");
        }

        StartRow();
    }

    /// <summary>
    /// Registers a style for this writer's cells and returns its id, to pass to the <c>Write</c>
    /// overloads. The same style, by value, returns the same id. Call it before or during any sheet;
    /// csv files ignore styles.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="style"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The file already holds 4096 distinct styles: registering that many by hand is a defect in the calling code. The writer is faulted. (A style rule that reaches the limit is the data's doing instead: <see cref="TabularWriteException"/> <c>write.too-many-styles</c>, located at its cell.)</exception>
    /// <exception cref="ArgumentOutOfRangeException">The style's alignment is not a defined value. The writer is faulted.</exception>
    public StyleId RegisterStyle(CellStyle style)
    {
        ExpectWritable();
        return new StyleId(_stamp, AddStyle(style));
    }

    /// <summary>The most rule-returned styles remembered by reference before the cache starts over.</summary>
    internal const int StyleCacheLimit = 16_384;

    private readonly Dictionary<CellStyle, StyleId> _byReference = new(ReferenceEqualityComparer.Instance);

    // The last hit per column: a rule across thousands of columns changes colour from one cell to the next,
    // but a column's own previous cell is usually the same style. Allocated on the sheet's first StyleFor.
    private CellStyle?[]? _lastStyles;
    private StyleId[]? _lastStyleIds;

    internal int StyleCacheCount => _byReference.Count;

    /// <summary>
    /// The id of a style a rule returned: a reference compare when it is the previous one, a lookup by
    /// reference otherwise, registering it (by value) the first time. Null is the unstyled cell.
    /// </summary>
    /// <remarks>
    /// A rule that builds a new style per cell still writes correctly — registration dedupes by value —
    /// but the reference cache would grow with every cell, so it starts over at <see cref="StyleCacheLimit"/>.
    /// </remarks>
    internal StyleId StyleFor(CellStyle? style)
    {
        if (style is null)
        {
            return default;
        }

        // StyleFor runs before the cell's Write, so _column is the cell about to be written. Outside a row,
        // or on a merged sheet's covered positions, it is only a slot: the cache is checked by reference.
        int slot = _column;

        if (_lastStyles is null && _columns.Length != 0)
        {
            _lastStyles = new CellStyle?[_columns.Length];
            _lastStyleIds = new StyleId[_columns.Length];
        }

        bool cached = _lastStyles is not null && (uint)slot < (uint)_lastStyles.Length;

        if (cached && ReferenceEquals(style, _lastStyles![slot]))
        {
            return _lastStyleIds![slot];
        }

        if (!_byReference.TryGetValue(style, out StyleId id))
        {
            id = RegisterRuleStyle(style);

            if (_byReference.Count == StyleCacheLimit)
            {
                _byReference.Clear();
            }

            _byReference.Add(style, id);
        }

        if (cached)
        {
            _lastStyles![slot] = style;
            _lastStyleIds![slot] = id;
        }

        return id;
    }

    private int AddStyle(CellStyle style)
    {
        try
        {
            return _styles.Add(style);
        }
        catch (Exception refused) when (refused is ArgumentException or InvalidOperationException)
        {
            MarkFaulted();
            throw;
        }
    }

    /// <summary>
    /// Registers a style a rule returned for the cell about to be written. A rule turns data into
    /// styles, so the one past the limit is the data's doing — a write error located at its cell —
    /// where the same limit reached by <see cref="RegisterStyle"/> is a defect in the calling code.
    /// </summary>
    private StyleId RegisterRuleStyle(CellStyle style)
    {
        ExpectWritable();

        try
        {
            if (_styles.TryAdd(style, out int index))
            {
                return new StyleId(_stamp, index);
            }
        }
        catch (ArgumentException)
        {
            MarkFaulted();
            throw;
        }

        int column = NextUncoveredColumn();
        string header = column < _columns.Length ? _columns[column].Header : string.Empty;

        throw Faulting(new TabularWriteException(
            ErrorCodes.Write.TooManyStyles,
            _sheetName,
            _rowNumber,
            column,
            header,
            $"Row {_rowNumber}, column {column + 1} (\"{header}\") of sheet \"{_sheetName}\" cannot be written: its style rule returned a {StyleTable.MaxStyles + 1}th distinct style, and a file holds at most {StyleTable.MaxStyles}."));
    }

    /// <summary>The cell the next write lands in: the row's position, past any positions a merged range covers.</summary>
    private int NextUncoveredColumn()
    {
        int column = _column;

        while (_coveredFrom is not null && column < _columns.Length && IsCovered(column))
        {
            column++;
        }

        return column;
    }

    /// <summary>Writes the next cell as text; null writes an empty cell.</summary>
    /// <remarks>The import trims text and reads empty or whitespace-only text as no value.</remarks>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns. This applies to every <c>Write</c> overload and <see cref="WriteEmpty()"/>.</exception>
    public void Write(string? value) => Write(value, default);

    /// <inheritdoc cref="Write(string?)"/>
    /// <param name="value">The text; null writes an empty cell.</param>
    /// <param name="style">A style this writer handed out, or the default for none.</param>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(string? value, StyleId style)
    {
        int column = NextCell();
        int index = Index(style);

        if (value is null)
        {
            _sheet.WriteEmpty(index);
            return;
        }

        Check(TextRules.Check(value) ?? _sheet.WriteText(value, column, index), column);
    }

    /// <summary>Writes the next cell as an integer. Narrower integers arrive here by implicit conversion.</summary>
    /// <remarks>
    /// A <see cref="char"/> converts implicitly too, so it binds here and writes its code point:
    /// <c>Write('A')</c> writes 65 — write <c>Write("A")</c> for the letter. A <see cref="ulong"/> converts
    /// implicitly to both <see cref="double"/> and <see cref="decimal"/>, so the call is ambiguous and
    /// does not compile; convert it explicitly, to <see cref="long"/> or <see cref="decimal"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(long value) => Write(value, default);

    /// <inheritdoc cref="Write(long)"/>
    /// <param name="value">The integer.</param>
    /// <param name="style">A style this writer handed out, or the default for none.</param>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(long value, StyleId style)
    {
        int column = NextCell();
        Check(_sheet.WriteLong(value, Index(style)), column);
    }

    /// <summary>Writes the next cell as a decimal number.</summary>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(decimal value) => Write(value, default);

    /// <inheritdoc cref="Write(decimal)"/>
    /// <param name="value">The number.</param>
    /// <param name="style">A style this writer handed out, or the default for none.</param>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(decimal value, StyleId style)
    {
        int column = NextCell();
        Check(_sheet.WriteDecimal(value, Index(style)), column);
    }

    /// <summary>
    /// Writes the next cell as a number. Refused when not finite, or when it has more than 15
    /// significant digits — which a workbook would not give back.
    /// </summary>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(double value) => Write(value, default);

    /// <inheritdoc cref="Write(double)"/>
    /// <param name="value">The number.</param>
    /// <param name="style">A style this writer handed out, or the default for none.</param>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(double value, StyleId style)
    {
        int column = NextCell();
        Check(ValueChecks.Double(value) ?? _sheet.WriteDouble(value, Index(style)), column);
    }

    /// <summary>
    /// Writes the next cell as a date, with its time of day when it has one. Anything finer than a
    /// millisecond is dropped, and the kind is not kept: the import returns the wall-clock value.
    /// </summary>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(DateTime value) => Write(value, default);

    /// <inheritdoc cref="Write(DateTime)"/>
    /// <param name="value">The date and time.</param>
    /// <param name="style">A style this writer handed out, or the default for none.</param>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(DateTime value, StyleId style)
    {
        int column = NextCell();
        DateTime truncated = ValueChecks.Truncated(value);
        Check(_sheet.WriteDate(truncated, truncated.TimeOfDay != TimeSpan.Zero, Index(style)), column);
    }

    /// <summary>Writes the next cell as a date. The import returns it as a <see cref="DateTime"/> at midnight.</summary>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(DateOnly value) => Write(value, default);

    /// <inheritdoc cref="Write(DateOnly)"/>
    /// <param name="value">The date.</param>
    /// <param name="style">A style this writer handed out, or the default for none.</param>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(DateOnly value, StyleId style)
    {
        int column = NextCell();
        Check(_sheet.WriteDate(value.ToDateTime(TimeOnly.MinValue), hasTime: false, Index(style)), column);
    }

    /// <summary>Writes the next cell as a boolean.</summary>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(bool value) => Write(value, default);

    /// <inheritdoc cref="Write(bool)"/>
    /// <param name="value">The boolean.</param>
    /// <param name="style">A style this writer handed out, or the default for none.</param>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void Write(bool value, StyleId style)
    {
        NextCell();
        _sheet.WriteBoolean(value, Index(style));
    }

    /// <summary>Writes the next cell empty.</summary>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void WriteEmpty() => WriteEmpty(default);

    /// <inheritdoc cref="WriteEmpty()"/>
    /// <param name="style">A style this writer handed out, or the default for none.</param>
    /// <exception cref="ArgumentException"><paramref name="style"/> was not handed out by this writer.</exception>
    /// <exception cref="InvalidOperationException">A <see cref="Merge"/> waits and this cell would overlap a range declared earlier, or the range reaches past the sheet's columns.</exception>
    public void WriteEmpty(StyleId style)
    {
        NextCell();
        _sheet.WriteEmpty(Index(style));
    }

    /// <summary>
    /// Merges the next cell written with the cells right of and below it: it becomes the top-left of
    /// a range <paramref name="rows"/> high and <paramref name="columns"/> wide, which shows its value.
    /// The writer skips the covered positions — the row's next write lands after the range, and later
    /// rows skip it too — and writes them itself. Csv writes the value in the top-left cell and empty
    /// fields elsewhere.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Rows or columns below 1, or a single cell.</exception>
    /// <exception cref="InvalidOperationException">Outside a row, or a merge already waits for its cell.</exception>
    /// <exception cref="TabularWriteException">The sheet already holds as many merges as its format allows — 65,536 for xlsx (<c>write.too-many-merges</c>, located where the range would begin). The writer is faulted.</exception>
    public void Merge(int rows, int columns)
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A merge is declared inside a row, right before its top-left cell.");
        }

        if (_pendingColumns != 0)
        {
            throw Refuse("A merge already waits for its top-left cell.");
        }

        if (rows < 1 || columns < 1 || (rows == 1 && columns == 1))
        {
            throw Faulting(new ArgumentOutOfRangeException(rows < 1 ? nameof(rows) : nameof(columns), $"A merged range is at least 1 × 1 and more than one cell; {rows} × {columns} is not."));
        }

        if (_merges == _sheet.MaxMerges)
        {
            int column = Math.Min(NextUncoveredColumn(), _columns.Length - 1);
            string header = _columns[column].Header;

            throw Faulting(new TabularWriteException(
                ErrorCodes.Write.TooManyMerges,
                _sheetName,
                _rowNumber,
                column,
                header,
                $"Row {_rowNumber}, column {column + 1} (\"{header}\") of sheet \"{_sheetName}\" cannot begin a merged range: a {Format} sheet holds at most {_sheet.MaxMerges}."));
        }

        // Allocated here, on the sheet's first merge, so that a merge waiting implies the arrays exist
        // and a cell of a sheet without merges pays a single null check.
        _coveredFrom ??= new long[_columns.Length];
        _coveredThrough ??= new long[_columns.Length];
        _pendingRows = rows;
        _pendingColumns = columns;
    }

    /// <summary>Ends the row; columns it did not reach are written empty.</summary>
    public void EndRow()
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A row ends after it began.");
        }

        if (_pendingColumns != 0)
        {
            throw Refuse("A merge was declared but no cell followed it in the row.");
        }

        FinishRow();
    }

    /// <summary>
    /// Writes a batch given by column as its rows, at the sheet's next row: each column's value for the
    /// row in turn, typed, through the same checks as <c>Write</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The batch's columns are not the sheet's.</exception>
    /// <exception cref="InvalidOperationException">Outside a sheet, or inside a row.</exception>
    /// <exception cref="TabularWriteException">A value the format cannot hold, or a row past the sheet's limit, located by sheet, row, column and header.</exception>
    public void WriteBatch(ColumnBatch batch)
    {
        ExpectBatch(batch);

        try
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                WriteBatchRow(batch, row);
            }
        }
        catch
        {
            // The caller's selectors, rules and lists run inside: whatever they throw leaves a half-written row.
            MarkFaulted();
            throw;
        }
    }

    /// <summary>
    /// Writes a batch as <see cref="WriteBatch"/> does, flushing to the stream whenever
    /// <see cref="FlushRecommended"/> after a row — memory stays flat however large the batch.
    /// </summary>
    /// <exception cref="ArgumentException">The batch's columns are not the sheet's.</exception>
    /// <exception cref="InvalidOperationException">Outside a sheet, or inside a row.</exception>
    /// <exception cref="TabularWriteException">A value the format cannot hold, or a row past the sheet's limit, located by sheet, row, column and header.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the writer is faulted and the file incomplete.</exception>
    public async ValueTask WriteBatchAsync(ColumnBatch batch, CancellationToken cancellationToken = default)
    {
        ExpectBatch(batch);

        try
        {
            for (int row = 0; row < batch.RowCount; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteBatchRow(batch, row);

                if (FlushRecommended)
                {
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            MarkFaulted();
            throw;
        }
    }

    private void ExpectBatch(ColumnBatch batch)
    {
        ExpectWritable();

        if (batch is null)
        {
            throw Faulting(new ArgumentNullException(nameof(batch)));
        }

        if (_state != State.InSheet)
        {
            throw Refuse("A batch is written inside a sheet, between rows.");
        }

        if (batch.ColumnCount != _columns.Length)
        {
            throw Faulting(new ArgumentException($"The batch has {batch.ColumnCount} columns; sheet \"{_sheetName}\" has {_columns.Length}.", nameof(batch)));
        }

        if (_lastCoveredRow > _rowNumber)
        {
            throw Refuse("A merged range still covers rows below; a batch writes plain rows — end the range first.");
        }
    }

    private void WriteBatchRow(ColumnBatch batch, int row)
    {
        BeginRow();

        for (int column = 0; column < _columns.Length; column++)
        {
            batch.Column(column).Write(this, row);
        }

        EndRow();
    }

    /// <summary>Moves everything pending in memory into the stream.</summary>
    /// <remarks>Cancelling, or a stream that fails, leaves the writer faulted and the file incomplete.</remarks>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ExpectWritable();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _buffer.DrainToAsync(_target, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            MarkFaulted();
            throw;
        }
    }

    /// <summary>Ends the file, writes what is pending and flushes the stream. Only now is the file valid.</summary>
    /// <exception cref="InvalidOperationException">A row has not ended, no sheet was begun, or a merged range still covers rows the sheet did not write.</exception>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ExpectWritable();

        if (_state == State.InRow)
        {
            throw Refuse("The last row has not ended.");
        }

        if (_sheets == 0)
        {
            throw Refuse("A file has at least one sheet.");
        }

        ExpectRangesClosed();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sheet.Complete();
            await _buffer.DrainToAsync(_target, cancellationToken).ConfigureAwait(false);
            await _target.FlushAsync(cancellationToken).ConfigureAwait(false);
            _state = State.Completed;
        }
        catch
        {
            MarkFaulted();
            throw;
        }
    }

    /// <summary>
    /// Releases the writer and closes the stream, unless told to leave it open. Without
    /// <see cref="CompleteAsync"/> first, what is pending is dropped and the file stays incomplete.
    /// A stream that fails to close is reported only for a complete file; otherwise the failure is
    /// dropped, so it never hides the exception that is already on its way.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_state == State.Disposed)
        {
            return;
        }

        bool complete = _state == State.Completed;
        _state = State.Disposed;

        try
        {
            try
            {
                _sheet.Dispose();
            }
            finally
            {
                await _buffer.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (!_leaveOpen)
            {
                await CloseTargetAsync(complete).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Closes the target. When the file is not complete — the writer failed, or was abandoned — a
    /// failure to close is dropped: the caller is already handling (or has already been told about)
    /// what went wrong, and an exception from closing would replace it. A complete file's close
    /// failure is reported: it may mean the bytes did not all arrive.
    /// </summary>
    private async ValueTask CloseTargetAsync(bool complete)
    {
        if (complete)
        {
            await _target.DisposeAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            await _target.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception closing) when (closing is not OutOfMemoryException)
        {
            // Dropped on purpose; see the summary.
        }
    }

    /// <summary>
    /// What is wrong with a sheet's columns, as the exception to throw, or null: 1 to 16,384 columns,
    /// headers not empty, not padded, unique ignoring case and writable, widths more than 0 and at
    /// most 255 characters.
    /// </summary>
    /// <remarks>Shared with <see cref="TabularExportBuilder{T}.Build"/>, so an export's columns fail where they are declared.</remarks>
    internal static Exception? ColumnsProblem(ReadOnlySpan<WriteColumn> columns)
    {
        if (columns.IsEmpty || columns.Length > MaxColumns)
        {
            return new ArgumentOutOfRangeException(nameof(columns), columns.Length, $"A sheet has 1 to {MaxColumns} columns.");
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (WriteColumn column in columns)
        {
            if (HeaderProblem(column.Header, seen) is { } problem)
            {
                return new ArgumentException(problem, nameof(columns));
            }

            if (column.Width is { } width && (!double.IsFinite(width) || width <= 0 || width > MaxWidth))
            {
                return new ArgumentOutOfRangeException(nameof(columns), width, $"A column is more than 0 and at most {MaxWidth} characters wide.");
            }
        }

        return null;
    }

    private void CheckColumns(ReadOnlySpan<WriteColumn> columns)
    {
        if (ColumnsProblem(columns) is { } problem)
        {
            throw Faulting(problem);
        }
    }

    /// <summary>Why a header could not be read back as written, or null; adds it to <paramref name="seen"/>.</summary>
    private static string? HeaderProblem(string? header, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return "Every column has a header that is not empty.";
        }

        if (char.IsWhiteSpace(header[0]) || char.IsWhiteSpace(header[^1]))
        {
            return $"The header \"{header}\" starts or ends with whitespace, which the import trims; it would not read back as written.";
        }

        if (!seen.Add(header))
        {
            return $"The header \"{header}\" appears twice; the import could not tell the columns apart.";
        }

        return TextRules.Check(header) is { } code ? $"The header \"{header}\" cannot be written: {code}." : null;
    }

    private void StartRow()
    {
        if (_rowNumber >= MaxRows)
        {
            throw Faulting(TooManyRows());
        }

        _rowNumber++;
        _column = 0;
        _sheet.BeginRow();
        _state = State.InRow;
    }

    /// <summary>
    /// The refusal of the row past the limit, located at the last row the sheet holds: the refused
    /// row's own number may not fit in an int — for csv the limit is <see cref="int.MaxValue"/> itself.
    /// </summary>
    private TabularWriteException TooManyRows()
    {
        string header = _columns.Length > 0 ? _columns[0].Header : string.Empty;

        return new TabularWriteException(
            ErrorCodes.Write.TooManyRows,
            _sheetName,
            _rowNumber,
            0,
            header,
            $"Sheet \"{_sheetName}\" holds {_rowNumber} rows, the header included, the most a {Format} sheet holds; the row after row {_rowNumber} cannot be written.");
    }

    private void FinishRow()
    {
        for (; _column < _columns.Length; _column++)
        {
            if (_coveredFrom is not null && IsCovered(_column))
            {
                _sheet.WriteCovered();
            }
            else
            {
                _sheet.WriteEmpty(0);
            }
        }

        _sheet.EndRow();
        _state = State.InSheet;
    }

    /// <summary>A number no other writer in the process holds, never 0.</summary>
    private static int NextStamp()
    {
        int stamp = Interlocked.Increment(ref s_stamps);
        return stamp != 0 ? stamp : Interlocked.Increment(ref s_stamps);
    }

    /// <summary>The style's index, refused unless this writer handed it out.</summary>
    private int Index(StyleId style)
    {
        int index = style.Value;

        if ((uint)index >= (uint)_styles.Count || (index != 0 && style.Writer != _stamp))
        {
            throw Faulting(new ArgumentException($"{style} was not handed out by this writer's RegisterStyle method.", nameof(style)));
        }

        return index;
    }

    private int NextCell()
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A value is written between BeginRow and EndRow.");
        }

        if (_coveredFrom is not null)
        {
            return NextCellOfMergedSheet();
        }

        if (_column == _columns.Length)
        {
            throw Refuse($"The row already has a value for each of the sheet's {_columns.Length} columns.");
        }

        return _column++;
    }

    /// <summary>The next cell on a sheet that has a merge: covered positions are written first, and a waiting merge begins here.</summary>
    private int NextCellOfMergedSheet()
    {
        SkipCovered();

        if (_column == _columns.Length)
        {
            throw Refuse($"The row already has a value for each of the sheet's {_columns.Length} columns.");
        }

        if (_pendingColumns != 0)
        {
            BeginMerge(_column);
        }

        return _column++;
    }

    /// <summary>Writes the covered positions the row has reached.</summary>
    private void SkipCovered()
    {
        while (_column < _columns.Length && IsCovered(_column))
        {
            _sheet.WriteCovered();
            _column++;
        }
    }

    private bool IsCovered(int column) =>
        _coveredFrom![column] <= _rowNumber && _rowNumber <= _coveredThrough![column];

    /// <summary>Checks the waiting merge against the sheet and earlier ranges, records it, and tells the format.</summary>
    private void BeginMerge(int column)
    {
        int rows = _pendingRows;
        int columns = _pendingColumns;
        _pendingRows = 0;
        _pendingColumns = 0;

        if (columns > _columns.Length - column)
        {
            throw Refuse($"A merge of {columns} columns from column {column + 1} reaches past the sheet's {_columns.Length} columns.");
        }

        long[] coveredFrom = _coveredFrom!;
        long[] coveredThrough = _coveredThrough!;
        long last = (long)_rowNumber + rows - 1;

        for (int c = column; c < column + columns; c++)
        {
            if (coveredThrough[c] >= _rowNumber && coveredFrom[c] <= last)
            {
                throw Refuse($"The merge from row {_rowNumber}, column {column + 1} overlaps a range declared earlier.");
            }
        }

        for (int c = column; c < column + columns; c++)
        {
            // The top-left cell holds the value; every other position in the range is covered.
            coveredFrom[c] = c == column ? _rowNumber + 1L : _rowNumber;
            coveredThrough[c] = c == column && rows == 1 ? 0 : last;
        }

        _lastCoveredRow = Math.Max(_lastCoveredRow, last);
        _merges++;
        _sheet.Merge(rows, columns);
    }

    private void ExpectRangesClosed()
    {
        if (_lastCoveredRow > _rowNumber)
        {
            throw Refuse($"A merged range reaches row {_lastCoveredRow}, but sheet \"{_sheetName}\" ends at row {_rowNumber}.");
        }
    }

    private void Check(string? code, int column)
    {
        if (code is null)
        {
            return;
        }

        string header = _columns[column].Header;

        throw Faulting(new TabularWriteException(
            code,
            _sheetName,
            _rowNumber,
            column,
            header,
            $"Row {_rowNumber}, column {column + 1} (\"{header}\") of sheet \"{_sheetName}\" cannot be written: {code}."));
    }

    private void ExpectWritable()
    {
        ObjectDisposedException.ThrowIf(_state == State.Disposed, this);

        if (_state == State.Faulted)
        {
            throw new InvalidOperationException(FaultedMessage);
        }

        if (_state == State.Completed)
        {
            // Not faulted: the file is whole, and a later call says so again rather than that it failed.
            throw new InvalidOperationException("The file is complete; nothing more can be written.");
        }
    }

    private InvalidOperationException Refuse(string message) => Faulting(new InvalidOperationException(message));

    private T Faulting<T>(T exception)
        where T : Exception
    {
        MarkFaulted();
        return exception;
    }

    /// <summary>Marks the writer unusable after a failure it was driven through, so its file is not completed.</summary>
    internal void Fault()
    {
        // A completed file is whole: a failure after it must not turn it into a failed one.
        if (_state != State.Completed)
        {
            MarkFaulted();
        }
    }

    private void MarkFaulted()
    {
        if (_state != State.Disposed)
        {
            _state = State.Faulted;
        }
    }
}
