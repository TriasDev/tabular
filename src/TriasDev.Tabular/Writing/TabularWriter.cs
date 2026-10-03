using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular;

/// <summary>
/// Writes a table into a stream, row by row: typed values in, a csv, xlsx or ods file out.
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
    private readonly bool _leaveOpen;
    private State _state = State.Open;
    private WriteColumn[] _columns = [];
    private readonly HashSet<string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);
    private string _sheetName = string.Empty;
    private int _sheets;
    private long _rowNumber;
    private int _column;

    private TabularWriter(Stream target, TabularFormat format, SpillBuffer buffer, ISheetWriter sheet, bool leaveOpen)
    {
        _target = target;
        Format = format;
        _buffer = buffer;
        _sheet = sheet;
        _leaveOpen = leaveOpen;
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

    /// <summary>Creates a writer for a format, into a stream.</summary>
    /// <param name="stream">Where the file goes: a file, a blob, a response body. It need not seek.</param>
    /// <param name="format">The format to write: csv or xlsx in this version.</param>
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

            switch (format)
            {
                case TabularFormat.Csv:
                    CsvFormat csv = effective.Csv.Resolve();
                    SpillBuffer buffer = new();
                    return new TabularWriter(stream, format, buffer, new CsvSheetWriter(buffer, csv), effective.LeaveOpen);
                case TabularFormat.Xlsx:
                    XlsxWriterOptions xlsx = effective.Xlsx.Checked();
                    SpillBuffer workbook = new();
                    return new TabularWriter(stream, format, workbook, new XlsxSheetWriter(workbook, xlsx), effective.LeaveOpen);
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
    public void BeginSheet(string name, ReadOnlySpan<WriteColumn> columns)
    {
        ExpectWritable();

        if (_state != State.Open && _state != State.InSheet)
        {
            throw Refuse("A sheet begins outside a row.");
        }

        if (_sheets > 0 && !_sheet.AllowsSeveralSheets)
        {
            throw Refuse($"A {Format} file holds one sheet.");
        }

        if (name is null)
        {
            throw Faulting(new ArgumentNullException(nameof(name)));
        }

        if (_sheet.NamesSheets && SheetNames.Problem(name, _sheetNames) is { } problem)
        {
            throw Faulting(new ArgumentException(problem, nameof(name)));
        }

        CheckColumns(columns);

        _columns = columns.ToArray();
        _sheetName = name;
        _sheetNames.Add(name);
        _sheets++;
        _rowNumber = 0;
        _sheet.BeginSheet(name, _columns);

        StartRow();

        foreach (string header in _columns.Select(column => column.Header))
        {
            if (_sheet.WriteHeader(header) is { } code)
            {
                throw Faulting(new ArgumentException($"The header \"{header}\" cannot be written: {code}.", nameof(columns)));
            }

            _column++;
        }

        FinishRow();
    }

    /// <summary>Begins a row; its values follow, one per column, then <see cref="EndRow"/>.</summary>
    /// <exception cref="TabularLimitException">The sheet already holds as many rows as its format allows.</exception>
    public void BeginRow()
    {
        ExpectWritable();

        if (_state != State.InSheet)
        {
            throw Refuse("A row begins inside a sheet, after the previous row ended.");
        }

        StartRow();
    }

    /// <summary>Writes the next cell as text; null writes an empty cell.</summary>
    /// <remarks>The import trims text and reads empty or whitespace-only text as no value.</remarks>
    public void Write(string? value)
    {
        int column = NextCell();

        if (value is null)
        {
            _sheet.WriteEmpty();
            return;
        }

        Check(TextRules.Check(value) ?? _sheet.WriteText(value, column), column);
    }

    /// <summary>Writes the next cell as an integer. Narrower integers arrive here by implicit conversion.</summary>
    /// <remarks>
    /// A <see cref="char"/> converts implicitly too, so it binds here and writes its code point:
    /// <c>Write('A')</c> writes 65 — write <c>Write("A")</c> for the letter. A <see cref="ulong"/> converts
    /// implicitly to both <see cref="double"/> and <see cref="decimal"/>, so the call is ambiguous and
    /// does not compile; convert it explicitly, to <see cref="long"/> or <see cref="decimal"/>.
    /// </remarks>
    public void Write(long value)
    {
        int column = NextCell();
        Check(_sheet.WriteLong(value), column);
    }

    /// <summary>Writes the next cell as a decimal number.</summary>
    public void Write(decimal value)
    {
        int column = NextCell();
        Check(_sheet.WriteDecimal(value), column);
    }

    /// <summary>
    /// Writes the next cell as a number. Refused when not finite, or when it has more than 15
    /// significant digits — which a workbook would not give back.
    /// </summary>
    public void Write(double value)
    {
        int column = NextCell();
        Check(ValueChecks.Double(value) ?? _sheet.WriteDouble(value), column);
    }

    /// <summary>
    /// Writes the next cell as a date, with its time of day when it has one. Anything finer than a
    /// millisecond is dropped, and the kind is not kept: the import returns the wall-clock value.
    /// </summary>
    public void Write(DateTime value)
    {
        int column = NextCell();
        DateTime truncated = ValueChecks.Truncated(value);
        Check(_sheet.WriteDate(truncated, truncated.TimeOfDay != TimeSpan.Zero), column);
    }

    /// <summary>Writes the next cell as a date. The import returns it as a <see cref="DateTime"/> at midnight.</summary>
    public void Write(DateOnly value)
    {
        int column = NextCell();
        Check(_sheet.WriteDate(value.ToDateTime(TimeOnly.MinValue), hasTime: false), column);
    }

    /// <summary>Writes the next cell as a boolean.</summary>
    public void Write(bool value)
    {
        NextCell();
        _sheet.WriteBoolean(value);
    }

    /// <summary>Writes the next cell empty.</summary>
    public void WriteEmpty()
    {
        NextCell();
        _sheet.WriteEmpty();
    }

    /// <summary>Ends the row; columns it did not reach are written empty.</summary>
    public void EndRow()
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A row ends after it began.");
        }

        FinishRow();
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
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_state == State.Disposed)
        {
            return;
        }

        _state = State.Disposed;

        try
        {
            await _buffer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!_leaveOpen)
            {
                await _target.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void CheckColumns(ReadOnlySpan<WriteColumn> columns)
    {
        if (columns.IsEmpty || columns.Length > MaxColumns)
        {
            throw Faulting(new ArgumentOutOfRangeException(nameof(columns), columns.Length, $"A sheet has 1 to {MaxColumns} columns."));
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (WriteColumn column in columns)
        {
            if (HeaderProblem(column.Header, seen) is { } problem)
            {
                throw Faulting(new ArgumentException(problem, nameof(columns)));
            }

            if (column.Width is { } width && (!double.IsFinite(width) || width <= 0 || width > MaxWidth))
            {
                throw Faulting(new ArgumentOutOfRangeException(nameof(columns), width, $"A column is more than 0 and at most {MaxWidth} characters wide."));
            }
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
        if (_rowNumber >= _sheet.MaxRows)
        {
            throw Faulting(new TabularLimitException("MaxRows", _sheet.MaxRows, $"A {Format} sheet holds at most {_sheet.MaxRows} rows, the header included."));
        }

        _rowNumber++;
        _column = 0;
        _sheet.BeginRow();
        _state = State.InRow;
    }

    private void FinishRow()
    {
        for (; _column < _columns.Length; _column++)
        {
            _sheet.WriteEmpty();
        }

        _sheet.EndRow();
        _state = State.InSheet;
    }

    private int NextCell()
    {
        ExpectWritable();

        if (_state != State.InRow)
        {
            throw Refuse("A value is written between BeginRow and EndRow.");
        }

        if (_column == _columns.Length)
        {
            throw Refuse($"The row already has a value for each of the sheet's {_columns.Length} columns.");
        }

        return _column++;
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

    private void MarkFaulted()
    {
        if (_state != State.Disposed)
        {
            _state = State.Faulted;
        }
    }
}
