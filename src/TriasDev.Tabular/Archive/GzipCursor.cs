using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// Reads a gzip-compressed file as the file inside it: a csv file's one sheet, or a workbook's
/// sheets, each carrying the inner file's name as <see cref="SheetInfo.Source"/> when the gzip
/// header stores it.
/// </summary>
/// <remarks>
/// <para>
/// gzip packs one file, so there is nothing to list: the inner file is judged by its first
/// decompressed bytes with the rules <see cref="TabularFile.Open"/> uses, and read by its own cursor.
/// </para>
/// <para>
/// A csv file is decompressed as it is read, never unpacked. Its dialect is decided from its head,
/// and moving back to its sheet decompresses it again from its start. A workbook needs random access,
/// so it is decompressed into memory once, up to
/// <see cref="ArchiveCursorOptions.MaxEmbeddedWorkbookBytes"/>.
/// </para>
/// <para>
/// A file cut off or damaged anywhere is refused — <see cref="TabularFormatException.Truncated"/> or
/// <see cref="TabularFormatException.Corrupt"/> — as soon as the reading reaches the damage; it is
/// never read as a shorter file.
/// </para>
/// <para>
/// <see cref="SheetInfo.Source"/> is the header's stored name only, never the name passed in: a
/// mapping plan records it, and the name a caller passes may differ between the analysis and the
/// import of the same file.
/// </para>
/// </remarks>
internal sealed class GzipCursor : ITabularCursor
{
    private readonly Stream _stream;
    private readonly TabularOpenOptions _options;
    private readonly long _origin;
    private readonly List<SheetInfo> _sheets = [];

    /// <summary>The csv file's dialect; null when the inner file is a workbook.</summary>
    private readonly CsvDialect? _dialect;

    /// <summary>The repairs counted by csv cursors already closed; the current one's are added on reading.</summary>
    private readonly CursorDiagnostics _closed = new();

    private readonly CursorDiagnostics _diagnostics = new();

    private ITabularCursor? _inner;
    private GzipStreamReader? _reader;
    private bool _disposed;

    /// <summary>Opens a gzip file and judges the file inside it.</summary>
    /// <param name="stream">
    /// The gzip file. Must be seekable. Closed with the cursor, or when opening fails, unless
    /// <see cref="TabularOpenOptions.LeaveOpen"/> says otherwise.
    /// </param>
    /// <param name="name">What to call it; a csv sheet takes this name without its <c>.gz</c>, unless the header stores one.</param>
    /// <param name="options">The bounds and the csv, xlsx and ods options the inner file is read with; null for the defaults.</param>
    /// <param name="cancellationToken">Stops the opening, which decompresses the file's head — or the whole of a workbook.</param>
    public GzipCursor(Stream stream, string name, TabularOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _options = options ?? TabularOpenOptions.Default;

        try
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            if (!stream.CanSeek)
            {
                throw new ArgumentException("A gzip file is read again from its start to move back to its sheet, so the stream must be seekable.", nameof(stream));
            }

            _options.Archive.Checked();
            _options.Csv.Checked();
            _options.Xlsx.Checked();
            _options.Ods.Checked();
            cancellationToken.ThrowIfCancellationRequested();

            _origin = stream.Position;
            (string? stored, byte[] head) = ReadHead();

            if (head.AsSpan().StartsWith("PK\u0003\u0004"u8))
            {
                _inner = OpenWorkbook(cancellationToken);
                _sheets.AddRange(_inner.Sheets.Select(sheet => sheet with { Source = stored }));
                return;
            }

            if (GzipHeader.HasSignature(head))
            {
                throw new TabularFormatException(TabularFormatException.Unsupported,
                    "The gzip file holds another gzip file; a file compressed twice is not read. Decompress it once.");
            }

            if (TarHeader.IsHeader(head))
            {
                throw new TabularFormatException(TabularFormatException.Unsupported,
                    "The gzip file holds a tar archive; open it with TabularFile.Open, which reads it as an archive.");
            }

            // A legacy workbook, an XML document or a binary file is refused here in the words used
            // for one on its own.
            _dialect = _options.Csv.Dialect ?? CsvDialectDetector.Detect(head);
            _sheets.Add(new SheetInfo { Index = 0, Name = stored ?? WithoutGzExtension(name), Format = TabularFormat.Csv, Source = stored });
            MoveToSheet(0, cancellationToken);
        }
        catch
        {
            _inner?.Dispose();

            if (!_options.LeaveOpen)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc />
    public TabularFormat Format => TabularFormat.Gzip;

    /// <inheritdoc />
    public IReadOnlyList<SheetInfo> Sheets => _sheets;

    /// <inheritdoc />
    public int CurrentSheetIndex => _dialect is null ? _inner?.CurrentSheetIndex ?? 0 : 0;

    /// <inheritdoc />
    public ReadOnlySpan<RawCell> CurrentRow => _inner is null ? [] : _inner.CurrentRow;

    /// <inheritdoc />
    public int CurrentRowNumber => _inner?.CurrentRowNumber ?? 0;

    /// <inheritdoc />
    /// <remarks>The csv file's dialect, or null for a workbook.</remarks>
    public CsvDialect? Dialect => _dialect;

    /// <inheritdoc />
    /// <remarks>
    /// A csv sheet read again is counted again, as its repairs were made again — as for a csv file in
    /// a zip archive. The same instance throughout.
    /// </remarks>
    public CursorDiagnostics Diagnostics
    {
        get
        {
            CursorDiagnostics? current = _inner?.Diagnostics;
            _diagnostics.RecoveredUnterminatedQuotes = _closed.RecoveredUnterminatedQuotes + (current?.RecoveredUnterminatedQuotes ?? 0);
            _diagnostics.RecoveredStrayQuotes = _closed.RecoveredStrayQuotes + (current?.RecoveredStrayQuotes ?? 0);
            return _diagnostics;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// For a csv file, the compressed bytes read against the compressed file — observable, since the
    /// stream is seekable. For a workbook, the workbook cursor's own.
    /// </remarks>
    public double? ReadFraction
    {
        get
        {
            if (_reader is null)
            {
                return _inner?.ReadFraction;
            }

            long length = _stream.Length - _origin;
            return length <= 0 ? null : Math.Min(1d, (_reader.FilePosition - _origin) / (double)length);
        }
    }

    /// <inheritdoc />
    public bool MoveToSheet(int index, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (index < 0 || index >= _sheets.Count)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_dialect is null)
        {
            return _inner!.MoveToSheet(index, cancellationToken);
        }

        // A csv file is decompressed afresh, so the sheet moved back to starts from its first row.
        CloseInner();
        _stream.Position = _origin;
        _reader = new GzipStreamReader(_stream, _options.Archive.MaxUncompressedBytes);

        // The dialect is stated, so the cursor reads forward and never needs the stream to seek.
        _inner = new CsvCursor(_reader, _sheets[0].Name, _options.Csv with { Dialect = _dialect });
        return true;
    }

    /// <inheritdoc />
    public bool ReadRow(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_inner is null)
        {
            throw new InvalidOperationException(
                "A move to the sheet failed part-way, so there is no sheet to read. Move to a sheet first.");
        }

        return _inner.ReadRow(cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner?.Dispose();

        if (!_options.LeaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static string WithoutGzExtension(string name) =>
        name.Length > 3 && name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;

    /// <summary>The stored name and the first decompressed bytes — as many as the csv dialect probe takes.</summary>
    private (string? Name, byte[] Head) ReadHead()
    {
        _stream.Position = _origin;
        using GzipStreamReader reader = new(_stream, _options.Archive.MaxUncompressedBytes);
        byte[] head = new byte[_options.Csv.DialectProbeBytes];
        int read = reader.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return (reader.Name, read == head.Length ? head : head[..read]);
    }

    /// <summary>Decompresses a zip into memory — checked to its last byte — and opens it if it is a workbook.</summary>
    private ITabularCursor OpenWorkbook(CancellationToken cancellationToken)
    {
        _stream.Position = _origin;
        ChunkedBuffer buffer;

        using (GzipStreamReader reader = new(_stream, _options.Archive.MaxUncompressedBytes))
        {
            buffer = ChunkedBuffer.CopyOf(reader, 0, _options.Archive.MaxEmbeddedWorkbookBytes, cancellationToken);
        }

        try
        {
            TabularFormat format = TabularFile.ClassifyZip(buffer, TabularFile.ZipEntryBound.Of(_options, archive: false)) switch
            {
                TabularFile.ZipContent.Xlsx => TabularFormat.Xlsx,
                TabularFile.ZipContent.Ods => TabularFormat.Ods,
                _ => throw new TabularFormatException(TabularFormatException.Unsupported,
                    "The gzip file holds a zip archive or another document, not a workbook; a compressed archive is not read. Decompress it and upload the archive."),
            };

            return EmbeddedWorkbook.Open(format, buffer, _options, leaveOpen: false, cancellationToken);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Closes the csv cursor being read, keeping its repairs.</summary>
    private void CloseInner()
    {
        if (_inner is null)
        {
            return;
        }

        CursorDiagnostics done = _inner.Diagnostics;
        _closed.RecoveredUnterminatedQuotes += done.RecoveredUnterminatedQuotes;
        _closed.RecoveredStrayQuotes += done.RecoveredStrayQuotes;
        _inner.Dispose();
        _inner = null;
        _reader = null;
    }
}
