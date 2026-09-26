using System.IO.Compression;

using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// Reads a zip archive as one workbook: its sheets are the sheets of every file in it that can be
/// read as a table, in the order of their paths, each carrying its file's path as
/// <see cref="SheetInfo.Source"/>.
/// </summary>
/// <remarks>
/// <para>
/// The archive is flattened rather than nested so that nothing above the cursor learns what an
/// archive is: a sheet is profiled, mapped and imported the same way wherever it came from. Two
/// files may both hold a sheet called <c>Sheet1</c>; their <see cref="SheetInfo.Source"/> tells them
/// apart, and a mapping plan records both.
/// </para>
/// <para>
/// Every file is judged by its bytes, not its name, with the rules <see cref="TabularFile.Open"/>
/// uses: text is a csv sheet. What cannot be read as a table is reported in
/// <see cref="SkippedEntries"/> with the reason; directories, hidden files and <c>__MACOSX/</c> are
/// left out without a word, because nobody put them there on purpose.
/// </para>
/// <para>
/// A csv file is read as a stream straight out of the archive, never unpacked. Its dialect is
/// decided when the archive is opened, from the file's head, and the file is opened afresh to be
/// read — so moving back to a csv sheet reads it again from its first row, which a plain
/// <see cref="CsvCursor"/> cannot do.
/// </para>
/// </remarks>
public sealed class ArchiveCursor : ITabularCursor
{
    /// <summary>A file of the archive that holds sheets, and how it is read.</summary>
    private sealed record Source(ZipArchiveEntry Entry, TabularFormat Format, CsvDialect? Dialect);

    private readonly Stream _stream;
    private readonly TabularOpenOptions _options;
    private readonly ZipArchive _zip;
    private readonly List<Source> _sources = [];
    private readonly List<SheetInfo> _sheets = [];
    private readonly List<(int Source, int Local)> _origins = [];
    private readonly List<SkippedEntry> _skipped = [];

    /// <summary>The repairs counted by cursors already closed; the current one's are added on reading.</summary>
    private readonly CursorDiagnostics _closed = new();

    private readonly CursorDiagnostics _diagnostics = new();

    private long _declaredTotal;
    private ITabularCursor? _inner;
    private CountingStream? _counter;
    private int _innerSource = -1;
    private bool _disposed;

    /// <summary>Opens a zip archive and lists the sheets of the files in it.</summary>
    /// <param name="stream">
    /// The archive. Must be seekable. Closed with the cursor, or when opening fails, unless
    /// <see cref="TabularOpenOptions.LeaveOpen"/> says otherwise.
    /// </param>
    /// <param name="options">
    /// The archive's bounds, and the csv, xlsx and ods options its files are read with; null for the
    /// defaults.
    /// </param>
    /// <param name="cancellationToken">Stops the opening, which reads every file's head.</param>
    public ArchiveCursor(Stream stream, TabularOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _stream = stream;
        _options = options ?? TabularOpenOptions.Default;
        ZipArchive? zip = null;

        try
        {
            _options.Archive.Checked();
            _options.Csv.Checked();
            _options.Xlsx.Checked();
            _options.Ods.Checked();
            cancellationToken.ThrowIfCancellationRequested();

            zip = OpenZip(stream);
            _zip = zip;
            ListSources(cancellationToken);

            if (_sheets.Count == 0)
            {
                throw new TabularFormatException(TabularFormatException.Unsupported,
                    "The archive holds no file that can be read as a table.");
            }

            MoveToSheet(0);
        }
        catch
        {
            _inner?.Dispose();
            zip?.Dispose();

            if (!_options.LeaveOpen)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc />
    public TabularFormat Format => TabularFormat.Zip;

    /// <inheritdoc />
    public IReadOnlyList<SheetInfo> Sheets => _sheets;

    /// <inheritdoc />
    public IReadOnlyList<SkippedEntry> SkippedEntries => _skipped;

    /// <inheritdoc />
    public int CurrentSheetIndex { get; private set; }

    /// <inheritdoc />
    public ReadOnlySpan<RawCell> CurrentRow => _inner is null ? [] : _inner.CurrentRow;

    /// <inheritdoc />
    public int CurrentRowNumber => _inner?.CurrentRowNumber ?? 0;

    /// <inheritdoc />
    /// <remarks>The current sheet's file's dialect, or null for a workbook sheet.</remarks>
    public CsvDialect? Dialect => _inner?.Dialect;

    /// <inheritdoc />
    /// <remarks>
    /// Every file's repairs together: the files already read, and the one being read. The same
    /// instance throughout, as every cursor's is.
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
    /// Bytes read against the bytes the sheets' files declare, uncompressed: the compressed position
    /// inside an entry is not observable. A file is counted as read once the cursor has moved past it.
    /// </remarks>
    public double? ReadFraction
    {
        get
        {
            if (_declaredTotal == 0 || _innerSource < 0)
            {
                return null;
            }

            long before = 0;

            for (int i = 0; i < _innerSource; i++)
            {
                before += _sources[i].Entry.Length;
            }

            long current = _counter?.BytesRead ?? 0;
            return Math.Min(1d, (before + current) / (double)_declaredTotal);
        }
    }

    /// <inheritdoc />
    public bool MoveToSheet(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (index < 0 || index >= _sheets.Count)
        {
            return false;
        }

        (int source, int local) = _origins[index];

        // A csv file is always opened afresh, so a sheet moved back to starts from its first row.
        if (source != _innerSource || _sources[source].Format == TabularFormat.Csv)
        {
            OpenSource(source);
        }

        if (!_inner!.MoveToSheet(local))
        {
            return false;
        }

        CurrentSheetIndex = index;
        return true;
    }

    /// <inheritdoc />
    public bool ReadRow(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return _inner!.ReadRow(cancellationToken);
        }
        catch (InvalidDataException e)
        {
            // Raised by the decompressor: the entry's data is damaged, whatever the file inside is.
            throw Corrupt(e);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner?.Dispose();
        _zip.Dispose();

        if (!_options.LeaveOpen)
        {
            _stream.Dispose();
        }
    }

    private static ZipArchive OpenZip(Stream stream)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException e)
        {
            throw Corrupt(e);
        }
    }

    /// <summary>Counts the archive against its bounds, then sniffs each file in path order.</summary>
    private void ListSources(CancellationToken cancellationToken)
    {
        ArchiveCursorOptions bounds = _options.Archive;
        int entries = 0;
        long declared = 0;

        foreach (ZipArchiveEntry entry in _zip.Entries)
        {
            if (++entries > bounds.MaxEntries)
            {
                throw new TabularLimitException(nameof(ArchiveCursorOptions.MaxEntries), bounds.MaxEntries,
                    $"The archive holds more than the {bounds.MaxEntries} files allowed.");
            }

            declared += entry.Length;

            if (declared > bounds.MaxUncompressedBytes)
            {
                throw new TabularLimitException(nameof(ArchiveCursorOptions.MaxUncompressedBytes), bounds.MaxUncompressedBytes,
                    $"The archive expands to more than the {bounds.MaxUncompressedBytes} bytes allowed.");
            }
        }

        // Ordered by path so the sheets come in the same order whatever order the archiver wrote.
        foreach (ZipArchiveEntry entry in _zip.Entries.Where(e => !IsLeftOut(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sniff(entry);
        }
    }

    /// <summary>A directory, a hidden file or folder, or a Mac resource fork: nobody zipped it on purpose.</summary>
    private static bool IsLeftOut(string path) =>
        path.EndsWith('/') || path.EndsWith('\\')
        || path.StartsWith("__MACOSX/", StringComparison.Ordinal)
        || path.Split(PathSeparators).Any(segment => segment.StartsWith('.'));

    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>Decides what a file is from its head, and lists its sheets or records why not.</summary>
    private void Sniff(ZipArchiveEntry entry)
    {
        if (entry.IsEncrypted)
        {
            // Opening it would hand the ciphertext over as though it were the file.
            Skip(entry, SkippedEntryReason.Encrypted);
            return;
        }

        byte[] head;

        try
        {
            head = ReadHead(entry, _options.Csv.DialectProbeBytes);
        }
        catch (InvalidDataException)
        {
            Skip(entry, SkippedEntryReason.Unreadable);
            return;
        }

        if (head.AsSpan().StartsWith("PK\u0003\u0004"u8))
        {
            Skip(entry, SkippedEntryReason.NestedArchive);
            return;
        }

        if (head.AsSpan().StartsWith(CsvDialectDetector.CompoundFileSignature))
        {
            Skip(entry, SkippedEntryReason.LegacyWorkbook);
            return;
        }

        if (CsvDialectDetector.IsXmlDocument(head))
        {
            Skip(entry, SkippedEntryReason.XmlDocument);
            return;
        }

        CsvDialect dialect;

        try
        {
            dialect = CsvDialectDetector.Detect(head);
        }
        catch (TabularFormatException)
        {
            Skip(entry, SkippedEntryReason.Binary);
            return;
        }

        string name = Path.GetFileNameWithoutExtension(entry.Name);
        AddSource(new Source(entry, TabularFormat.Csv, _options.Csv.Dialect ?? dialect), [name.Length > 0 ? name : entry.Name]);
    }

    private static byte[] ReadHead(ZipArchiveEntry entry, int probeBytes)
    {
        using Stream content = entry.Open();
        byte[] head = new byte[(int)Math.Min(probeBytes, Math.Max(entry.Length, 0))];
        int read = content.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return read == head.Length ? head : head[..read];
    }

    private void AddSource(Source source, IReadOnlyList<string> sheetNames)
    {
        int maxSheets = _options.Xlsx.MaxSheets;

        if (_sheets.Count + sheetNames.Count > maxSheets)
        {
            throw new TabularLimitException("MaxSheets", maxSheets,
                $"The archive's files hold more than the {maxSheets} sheets allowed.");
        }

        int index = _sources.Count;
        _sources.Add(source);
        _declaredTotal += source.Entry.Length;

        for (int local = 0; local < sheetNames.Count; local++)
        {
            _origins.Add((index, local));
            _sheets.Add(new SheetInfo
            {
                Index = _sheets.Count,
                Name = sheetNames[local],
                Format = source.Format,
                Source = source.Entry.FullName,
            });
        }
    }

    private void Skip(ZipArchiveEntry entry, SkippedEntryReason reason) =>
        _skipped.Add(new SkippedEntry { Path = entry.FullName, Reason = reason });

    /// <summary>Closes the file being read, keeping its repairs, and opens another.</summary>
    private void OpenSource(int index)
    {
        CloseInner();

        Source source = _sources[index];
        Stream content;

        try
        {
            content = source.Entry.Open();
        }
        catch (InvalidDataException e)
        {
            throw Corrupt(e);
        }

        _counter = new CountingStream(content);

        // The dialect is stated, so the cursor reads forward and never needs the stream to seek.
        _inner = new CsvCursor(_counter, _sheets[_origins.FindIndex(o => o.Source == index)].Name,
            _options.Csv with { Dialect = source.Dialect });
        _innerSource = index;
    }

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
        _counter = null;
        _innerSource = -1;
    }

    private static TabularFormatException Corrupt(Exception inner) =>
        new(TabularFormatException.Corrupt, $"The archive is not readable: {inner.Message}", inner);
}
