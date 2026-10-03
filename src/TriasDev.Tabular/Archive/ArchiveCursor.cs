using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// Reads an archive — a zip, a tar, or a tar compressed with gzip — as one workbook: its sheets are the
/// sheets of every file in it that can be read as a table, in the order of their paths, each carrying
/// its file's path as <see cref="SheetInfo.Source"/>.
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
/// <see cref="SkippedEntries"/> with the reason; directories, links, hidden files and <c>__MACOSX/</c>
/// are left out without a word, because nobody put them there on purpose.
/// </para>
/// <para>
/// A csv file is read as a stream straight out of the archive, never unpacked. Its dialect is
/// decided when the archive is opened, from the file's head, and the file is opened afresh to be
/// read — so moving back to a csv sheet reads it again from its first row, which a plain
/// <see cref="CsvCursor"/> cannot do. In a compressed tar, opening a file afresh means decompressing
/// the archive again up to it, unless the cursor is moving forward through the archive anyway.
/// </para>
/// </remarks>
public sealed class ArchiveCursor : ITabularCursor
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    private readonly Stream _stream;
    private readonly TabularOpenOptions _options;
    private readonly ArchiveContainer _container;
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

    /// <summary>Opens an archive and lists the sheets of the files in it.</summary>
    /// <param name="stream">
    /// The archive: a zip, a tar, or a tar compressed with gzip, told apart by its bytes. Must be
    /// seekable. Closed with the cursor, or when opening fails, unless
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
        ArchiveContainer? container = null;

        try
        {
            _options.Archive.Checked();
            _options.Csv.Checked();
            _options.Xlsx.Checked();
            _options.Ods.Checked();
            cancellationToken.ThrowIfCancellationRequested();

            container = ArchiveContainer.Choose(stream);
            _container = container;
            ListSources(cancellationToken);

            if (_sheets.Count == 0)
            {
                throw new TabularFormatException(TabularFormatException.Unsupported,
                    "The archive holds no file that can be read as a table.");
            }

            MoveToSheet(0, cancellationToken);
        }
        catch
        {
            _inner?.Dispose();
            container?.Dispose();

            if (!_options.LeaveOpen)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc />
    /// <remarks><see cref="TabularFormat.Zip"/> or <see cref="TabularFormat.Tar"/>, compressed or not.</remarks>
    public TabularFormat Format => _container.Format;

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

            long current = _counter?.BytesRead
                ?? (long)((_inner?.ReadFraction ?? 0) * _sources[_innerSource].Entry.Length);
            return Math.Min(1d, (before + current) / (double)_declaredTotal);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Moving to a workbook's sheet copies the workbook out of a compressed archive, which the token
    /// can stop. A move stopped there has closed the file it left, so the cursor refuses to read until
    /// a move succeeds.
    /// </remarks>
    public bool MoveToSheet(int index, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (index < 0 || index >= _sheets.Count)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        (int source, int local) = _origins[index];

        // A csv file is always opened afresh, so a sheet moved back to starts from its first row.
        if (source != _innerSource || _sources[source].Format == TabularFormat.Csv)
        {
            OpenSource(source, cancellationToken);
        }

        try
        {
            if (!_inner!.MoveToSheet(local, cancellationToken))
            {
                return false;
            }
        }
        catch
        {
            // The workbook is open but not at the sheet asked for; reading on would hand out its
            // other sheet under the index of the one the cursor left.
            CloseInner();
            throw;
        }

        CurrentSheetIndex = index;
        return true;
    }

    /// <inheritdoc />
    public bool ReadRow(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_inner is null)
        {
            throw new InvalidOperationException(
                "A move to another sheet was stopped part-way, so there is no sheet to read. Move to a sheet first.");
        }

        try
        {
            return _inner.ReadRow(cancellationToken);
        }
        catch (InvalidDataException e)
        {
            // Raised by a zip's decompressor: the entry's data is damaged, whatever the file inside is.
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
        _container.Dispose();

        if (!_options.LeaveOpen)
        {
            _stream.Dispose();
        }
    }

    /// <summary>A directory, a link, a hidden file or folder, or a Mac resource fork: nobody archived it on purpose.</summary>
    private static bool IsLeftOut(ArchiveEntry entry) =>
        entry.Kind == ArchiveEntryKind.LeftOut
        || entry.Path.EndsWith('/') || entry.Path.EndsWith('\\')
        || entry.Path.StartsWith("__MACOSX/", StringComparison.Ordinal)
        || entry.Path.Split(PathSeparators).Any(segment => segment is not ("." or "..") && segment.StartsWith('.'));

    private static byte[] ReadHead(Stream content, ArchiveEntry entry, int probeBytes)
    {
        byte[] head = new byte[(int)Math.Min(probeBytes, Math.Max(entry.Length, 0))];
        int read = content.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return read == head.Length ? head : head[..read];
    }

    private static Finding Skipped(ArchiveEntry entry, SkippedEntryReason reason) => new(entry, null, [], reason);

    private static TabularFormatException Corrupt(Exception inner) =>
        new(TabularFormatException.Corrupt, $"The archive is not readable: {inner.Message}", inner);

    /// <summary>
    /// Counts the archive against its bounds and judges each file, then lists the findings in path
    /// order.
    /// </summary>
    /// <remarks>
    /// An archive with a directory is counted before a file is opened, and judged in path order. One
    /// that can only be read as a stream is counted and judged as it is read, in its own order — each
    /// file while it is the current one, which is the only time that is cheap — and the findings are
    /// put in path order afterwards, so the sheets come in the same order whatever order the archiver
    /// wrote.
    /// </remarks>
    private void ListSources(CancellationToken cancellationToken)
    {
        IEnumerable<ArchiveEntry> order;

        if (_container.Directory is { } directory)
        {
            CountAgainstBounds(directory);
            order = directory.OrderBy(e => e.Path, StringComparer.Ordinal);
        }
        else
        {
            order = Counted(_container.Entries(cancellationToken));
        }

        List<Finding> findings = [];

        foreach (ArchiveEntry entry in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsLeftOut(entry))
            {
                findings.Add(Judge(entry, cancellationToken));
            }
        }

        foreach (Finding finding in findings.OrderBy(f => f.Entry.Path, StringComparer.Ordinal))
        {
            if (finding.Skip is { } reason)
            {
                _skipped.Add(new SkippedEntry { Path = finding.Entry.Path, Reason = reason });
            }
            else
            {
                AddSource(finding.Source!, finding.SheetNames);
            }
        }
    }

    private void CountAgainstBounds(IEnumerable<ArchiveEntry> entries)
    {
        foreach (ArchiveEntry _ in Counted(entries))
        {
            // Counting is the point: Counted throws on the first entry past a bound.
        }
    }

    /// <summary>Passes the entries through, failing as soon as one passes the archive's bounds.</summary>
    private IEnumerable<ArchiveEntry> Counted(IEnumerable<ArchiveEntry> entries)
    {
        ArchiveCursorOptions bounds = _options.Archive;
        int count = 0;
        long declared = 0;

        foreach (ArchiveEntry entry in entries)
        {
            if (++count > bounds.MaxEntries)
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

            yield return entry;
        }
    }

    /// <summary>Decides what a file is from its head, and finds its sheets or why not.</summary>
    private Finding Judge(ArchiveEntry entry, CancellationToken cancellationToken)
    {
        switch (entry.Kind)
        {
            case ArchiveEntryKind.Encrypted:
                // Opening it would hand the ciphertext over as though it were the file.
                return Skipped(entry, SkippedEntryReason.Encrypted);
            case ArchiveEntryKind.Unsupported:
                return Skipped(entry, SkippedEntryReason.Unsupported);
        }

        using Stream content = _container.Open(entry, cancellationToken);
        byte[] head;

        try
        {
            head = ReadHead(content, entry, _options.Csv.DialectProbeBytes);
        }
        catch (InvalidDataException)
        {
            return Skipped(entry, SkippedEntryReason.Unreadable);
        }

        if (head.AsSpan().StartsWith("PK\u0003\u0004"u8))
        {
            return JudgeWorkbook(entry, head, content, cancellationToken);
        }

        if (GzipHeader.HasSignature(head))
        {
            // Read on its own a gzip file is opened; inside an archive it would be a second layer of
            // packing, which nothing here unpacks.
            return Skipped(entry, SkippedEntryReason.Compressed);
        }

        if (head.AsSpan().StartsWith(CsvDialectDetector.CompoundFileSignature))
        {
            return Skipped(entry, SkippedEntryReason.LegacyWorkbook);
        }

        if (CsvDialectDetector.IsXmlDocument(head))
        {
            return Skipped(entry, SkippedEntryReason.XmlDocument);
        }

        CsvDialect dialect;

        try
        {
            dialect = CsvDialectDetector.Detect(head);
        }
        catch (TabularFormatException)
        {
            return Skipped(entry, SkippedEntryReason.Binary);
        }

        string name = Path.GetFileNameWithoutExtension(entry.Name);
        return new Finding(entry, new Source(entry, TabularFormat.Csv, _options.Csv.Dialect ?? dialect),
            [(name.Length > 0 ? name : entry.Name, SheetVisibility.Visible)], null);
    }

    /// <summary>
    /// Lists the sheets of a zip inside the archive, if it is a workbook: read in place when the
    /// archive allows it, else copied into memory, opened for its sheet names and released.
    /// </summary>
    /// <remarks>
    /// A workbook that cannot be read is skipped with the reason, as any other file that is not a
    /// table; a bound it exceeds fails the archive, because bounds are the library's defence and are
    /// never downgraded to a skip.
    /// </remarks>
    private Finding JudgeWorkbook(ArchiveEntry entry, byte[] head, Stream content, CancellationToken cancellationToken)
    {
        CheckWorkbookSize(entry);

        if (_container.OpenSeekable(entry) is { } inPlace)
        {
            using (inPlace)
            {
                return JudgeOpenedWorkbook(entry, inPlace, cancellationToken);
            }
        }

        ChunkedBuffer? buffer;

        try
        {
            buffer = ChunkedBuffer.CopyOf(new HeadedStream(head, content), entry.Length, _options.Archive.MaxEmbeddedWorkbookBytes, cancellationToken);
        }
        catch (InvalidDataException)
        {
            // Damage met past the head, while copying: the file is unreadable, the archive is not.
            return Skipped(entry, SkippedEntryReason.Unreadable);
        }

        using (buffer)
        {
            return JudgeOpenedWorkbook(entry, buffer, cancellationToken);
        }
    }

    private Finding JudgeOpenedWorkbook(ArchiveEntry entry, Stream workbook, CancellationToken cancellationToken)
    {
        (TabularFormat format, SkippedEntryReason? skip) = TabularFile.ClassifyZip(workbook) switch
        {
            TabularFile.ZipContent.Xlsx => (TabularFormat.Xlsx, (SkippedEntryReason?)null),
            TabularFile.ZipContent.Ods => (TabularFormat.Ods, null),
            TabularFile.ZipContent.OtherDocument => (TabularFormat.Zip, SkippedEntryReason.OtherDocument),
            _ => (TabularFormat.Zip, SkippedEntryReason.NestedArchive),
        };

        if (skip is { } reason)
        {
            return Skipped(entry, reason);
        }

        List<(string Name, SheetVisibility Visibility)> names;

        try
        {
            using ITabularCursor opened = EmbeddedWorkbook.Open(format, workbook, _options, leaveOpen: true, cancellationToken);
            names = [.. opened.Sheets.Select(sheet => (sheet.Name, sheet.Visibility))];
        }
        catch (TabularFormatException e)
        {
            return Skipped(entry, e.Code == TabularFormatException.Unsupported ? SkippedEntryReason.Unsupported : SkippedEntryReason.Unreadable);
        }

        return new Finding(entry, new Source(entry, format, null), names, null);
    }

    /// <summary>The declared size refuses the plain case before a byte is read; a copy refuses a file whose declared size lied.</summary>
    private void CheckWorkbookSize(ArchiveEntry entry)
    {
        long limit = _options.Archive.MaxEmbeddedWorkbookBytes;

        if (entry.Length > limit)
        {
            throw new TabularLimitException(nameof(ArchiveCursorOptions.MaxEmbeddedWorkbookBytes), limit,
                $"A workbook inside the archive or compressed file is larger than the {limit} bytes allowed.");
        }
    }

    private void AddSource(Source source, IReadOnlyList<(string Name, SheetVisibility Visibility)> sheetNames)
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
                Name = sheetNames[local].Name,
                Visibility = sheetNames[local].Visibility,
                Format = source.Format,
                Source = source.Entry.Path,
            });
        }
    }

    /// <summary>Closes the file being read, keeping its repairs, and opens another.</summary>
    private void OpenSource(int index, CancellationToken cancellationToken)
    {
        CloseInner();

        Source source = _sources[index];

        if (source.Format != TabularFormat.Csv)
        {
            // Held while its sheets are read, released when the cursor moves to another file.
            _inner = EmbeddedWorkbook.Open(source.Format, OpenWorkbook(source.Entry, cancellationToken), _options, leaveOpen: false, cancellationToken);
            _innerSource = index;
            return;
        }

        Stream content;

        try
        {
            content = _container.Open(source.Entry, cancellationToken);
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

    /// <summary>A workbook to read: in place when the archive allows it, else a copy in memory.</summary>
    private Stream OpenWorkbook(ArchiveEntry entry, CancellationToken cancellationToken)
    {
        CheckWorkbookSize(entry);

        if (_container.OpenSeekable(entry) is { } inPlace)
        {
            return inPlace;
        }

        try
        {
            using Stream content = _container.Open(entry, cancellationToken);
            return ChunkedBuffer.CopyOf(content, entry.Length, _options.Archive.MaxEmbeddedWorkbookBytes, cancellationToken);
        }
        catch (InvalidDataException e)
        {
            throw Corrupt(e);
        }
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

    /// <summary>A file of the archive that holds sheets, and how it is read.</summary>
    private sealed record Source(ArchiveEntry Entry, TabularFormat Format, CsvDialect? Dialect);

    /// <summary>What judging one entry found: a source with its sheets, or a skip.</summary>
    private sealed record Finding(ArchiveEntry Entry, Source? Source, IReadOnlyList<(string Name, SheetVisibility Visibility)> SheetNames, SkippedEntryReason? Skip);
}
