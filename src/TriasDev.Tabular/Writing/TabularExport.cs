namespace TriasDev.Tabular;

/// <summary>Where an export of objects is declared: <c>TabularExport.For&lt;Portfolio&gt;().Column(…).Build()</c>.</summary>
public static class TabularExport
{
    /// <summary>Begins declaring an export of <typeparamref name="T"/>.</summary>
    public static TabularExportBuilder<T> For<T>() => new();
}

/// <summary>
/// An export of objects to a table: built once, kept in a static field, used by any number of
/// writes at once.
/// </summary>
/// <remarks>
/// Immutable and thread-safe: it holds the declared columns and nothing a write changes.
/// A source that is both <see cref="IEnumerable{T}"/> and <see cref="IAsyncEnumerable{T}"/> (an EF Core
/// <c>DbSet</c>, for one) is ambiguous: pass <c>.AsAsyncEnumerable()</c>. A literal <c>null</c> for the
/// options with a chunk source is ambiguous with the selector overload: write <c>options: null</c> or
/// omit it. Each chunk is flushed, so very small chunks mean one write to the target each.
/// </remarks>
public sealed class TabularExport<T>
{
    private readonly ExportColumn<T>[] _columns;
    private readonly WriteColumn[] _declared;
    private readonly IReadOnlyList<WriteColumn> _columnsView;

    internal TabularExport(ExportColumn<T>[] columns, WriteColumn[] declared, SheetOptions? sheet)
    {
        Sheet = sheet;
        _columns = columns;
        _declared = declared;
        _columnsView = Array.AsReadOnly(declared);
    }

    /// <summary>The layout every sheet the export writes gets; null when none was declared.</summary>
    public SheetOptions? Sheet { get; }

    /// <summary>The columns, in order, as a sheet's header row writes them.</summary>
    public IReadOnlyList<WriteColumn> Columns => _columnsView;

    /// <summary>Writes a file of one sheet from chunks as they arrive — the fast source for millions of rows.</summary>
    /// <param name="stream">The stream the file is written to; closed at the end, unless <see cref="TabularWriterOptions.LeaveOpen"/>.</param>
    /// <param name="format">The file's format.</param>
    /// <param name="sheetName">The sheet's name; a csv file's single sheet takes no name in the file, but the name must still be valid.</param>
    /// <param name="chunks">The rows, a batch at a time.</param>
    /// <param name="options">Options of the writer, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Stops the write; also flows into the enumeration of <paramref name="chunks"/>.</param>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// The writer is flushed after every chunk and inside a chunk whenever it recommends it, so memory
    /// holds a chunk and about a megabyte, however many rows the file has. On any failure the stream is
    /// closed (unless <see cref="TabularWriterOptions.LeaveOpen"/>) and the file is incomplete.
    /// An xlsx or ods sheet holds at most 1,048,576 rows, the header included; the row past it throws
    /// <see cref="TabularWriteException"/> (<c>write.too-many-rows</c>) after the rows before it were written — choose csv when the
    /// count may exceed it.
    /// </remarks>
    /// <exception cref="TabularWriteException">A value the format cannot hold, a style rule's 4,097th distinct style, or a row past the sheet's limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A chunk is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The writer is not in a state to begin a sheet.</exception>
    public ValueTask<int> WriteAsync(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<IReadOnlyList<T>> chunks, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, chunks, cancellationToken), cancellationToken);

    /// <summary>
    /// Writes a file of one sheet from a stream of messages that each carry a chunk — a gRPC server
    /// stream whose messages hold a repeated field, passed as it comes.
    /// </summary>
    /// <typeparam name="TChunk">The message type.</typeparam>
    /// <param name="stream">The stream the file is written to; closed at the end, unless <see cref="TabularWriterOptions.LeaveOpen"/>.</param>
    /// <param name="format">The file's format.</param>
    /// <param name="sheetName">The sheet's name; a csv file's single sheet takes no name in the file, but the name must still be valid.</param>
    /// <param name="chunks">The messages.</param>
    /// <param name="itemsOf">Returns the items a message carries, as an <see cref="IReadOnlyList{T}"/> — a protobuf <c>RepeatedField</c> is one. It must not return <see langword="null"/>.</param>
    /// <param name="options">Options of the writer, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Stops the write; also flows into the enumeration of <paramref name="chunks"/>.</param>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// Flushed like the chunk overload, so memory holds a message and about a megabyte. On any failure
    /// the stream is closed (unless <see cref="TabularWriterOptions.LeaveOpen"/>) and the file is incomplete.
    /// An xlsx or ods sheet holds at most 1,048,576 rows, the header included; the row past it throws
    /// <see cref="TabularWriteException"/> (<c>write.too-many-rows</c>) after the rows before it were written — choose csv when the
    /// count may exceed it.
    /// </remarks>
    /// <exception cref="TabularWriteException">A value the format cannot hold, a style rule's 4,097th distinct style, or a row past the sheet's limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="itemsOf"/> returned <see langword="null"/> for a message.</exception>
    /// <exception cref="InvalidOperationException">The writer is not in a state to begin a sheet.</exception>
    public ValueTask<int> WriteAsync<TChunk>(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<TChunk> chunks, Func<TChunk, IReadOnlyList<T>> itemsOf, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, chunks, itemsOf, cancellationToken), cancellationToken);

    /// <summary>Writes a file of one sheet from items arriving one at a time; awaits per item, so prefer chunks for millions.</summary>
    /// <param name="stream">The stream the file is written to; closed at the end, unless <see cref="TabularWriterOptions.LeaveOpen"/>.</param>
    /// <param name="format">The file's format.</param>
    /// <param name="sheetName">The sheet's name; a csv file's single sheet takes no name in the file, but the name must still be valid.</param>
    /// <param name="items">The rows.</param>
    /// <param name="options">Options of the writer, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Stops the write; also flows into the enumeration of <paramref name="items"/>.</param>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// On any failure the stream is closed (unless <see cref="TabularWriterOptions.LeaveOpen"/>) and the
    /// file is incomplete. An xlsx or ods sheet holds at most 1,048,576 rows, the header included; the
    /// row past it throws <see cref="TabularWriteException"/> (<c>write.too-many-rows</c>) after the rows before it were written —
    /// choose csv when the count may exceed it.
    /// </remarks>
    /// <exception cref="TabularWriteException">A value the format cannot hold, a style rule's 4,097th distinct style, or a row past the sheet's limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The writer is not in a state to begin a sheet.</exception>
    public ValueTask<int> WriteAsync(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<T> items, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, items, cancellationToken), cancellationToken);

    /// <summary>Writes a file of one sheet from items in memory or produced synchronously.</summary>
    /// <param name="stream">The stream the file is written to; closed at the end, unless <see cref="TabularWriterOptions.LeaveOpen"/>.</param>
    /// <param name="format">The file's format.</param>
    /// <param name="sheetName">The sheet's name; a csv file's single sheet takes no name in the file, but the name must still be valid.</param>
    /// <param name="items">The rows.</param>
    /// <param name="options">Options of the writer, or <see langword="null"/> for the defaults.</param>
    /// <param name="cancellationToken">Stops the write, observed at each flush, about every megabyte written.</param>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// On any failure the stream is closed (unless <see cref="TabularWriterOptions.LeaveOpen"/>) and the
    /// file is incomplete. An xlsx or ods sheet holds at most 1,048,576 rows, the header included; the
    /// row past it throws <see cref="TabularWriteException"/> (<c>write.too-many-rows</c>) after the rows before it were written —
    /// choose csv when the count may exceed it.
    /// </remarks>
    /// <exception cref="TabularWriteException">A value the format cannot hold, a style rule's 4,097th distinct style, or a row past the sheet's limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The writer is not in a state to begin a sheet.</exception>
    public ValueTask<int> WriteAsync(Stream stream, TabularFormat format, string sheetName, IEnumerable<T> items, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, items, cancellationToken), cancellationToken);

    /// <summary>Writes one sheet into a writer the caller owns, from chunks as they arrive.</summary>
    /// <param name="writer">The caller's writer; the caller completes it.</param>
    /// <param name="sheetName">The sheet's name; a csv file's single sheet takes no name in the file, but the name must still be valid.</param>
    /// <param name="chunks">The rows, a batch at a time.</param>
    /// <param name="cancellationToken">Stops the write; also flows into the enumeration of <paramref name="chunks"/>.</param>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// The caller completes the writer; several exports may write their sheets into one workbook. On any
    /// failure the writer is faulted. An xlsx or ods sheet holds at most 1,048,576 rows, the header
    /// included; the row past it throws <see cref="TabularWriteException"/> (<c>write.too-many-rows</c>) after the rows before it were
    /// written — choose csv when the count may exceed it.
    /// </remarks>
    /// <exception cref="TabularWriteException">A value the format cannot hold, a style rule's 4,097th distinct style, or a row past the sheet's limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A chunk is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The writer is not in a state to begin a sheet.</exception>
    public async ValueTask<int> WriteSheetAsync(TabularWriter writer, string sheetName, IAsyncEnumerable<IReadOnlyList<T>> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(chunks);

        try
        {
            writer.BeginSheet(sheetName, _declared, Sheet);
            int rows = 0;

            await foreach (IReadOnlyList<T> chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                rows += await WriteChunkAsync(writer, chunk ?? throw new ArgumentException("A chunk is null.", nameof(chunks)), cancellationToken).ConfigureAwait(false);
            }

            return rows;
        }
        catch
        {
            writer.Fault();
            throw;
        }
    }

    /// <summary>Writes one sheet into a writer the caller owns, from messages that each carry a chunk.</summary>
    /// <typeparam name="TChunk">The message type.</typeparam>
    /// <param name="writer">The caller's writer; the caller completes it.</param>
    /// <param name="sheetName">The sheet's name; a csv file's single sheet takes no name in the file, but the name must still be valid.</param>
    /// <param name="chunks">The messages.</param>
    /// <param name="itemsOf">Returns the items a message carries, as an <see cref="IReadOnlyList{T}"/> — a protobuf <c>RepeatedField</c> is one. It must not return <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the write; also flows into the enumeration of <paramref name="chunks"/>.</param>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// The caller completes the writer. On any failure the writer is faulted. An xlsx or ods sheet holds
    /// at most 1,048,576 rows, the header included; the row past it throws
    /// <see cref="TabularWriteException"/> (<c>write.too-many-rows</c>) after the rows before it were written — choose csv when the
    /// count may exceed it.
    /// </remarks>
    /// <exception cref="TabularWriteException">A value the format cannot hold, a style rule's 4,097th distinct style, or a row past the sheet's limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="itemsOf"/> returned <see langword="null"/> for a message.</exception>
    /// <exception cref="InvalidOperationException">The writer is not in a state to begin a sheet.</exception>
    public async ValueTask<int> WriteSheetAsync<TChunk>(TabularWriter writer, string sheetName, IAsyncEnumerable<TChunk> chunks, Func<TChunk, IReadOnlyList<T>> itemsOf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(itemsOf);

        try
        {
            writer.BeginSheet(sheetName, _declared, Sheet);
            int written = 0;

            await foreach (TChunk message in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyList<T> chunk = itemsOf(message) ?? throw new ArgumentException("The selector returned null for a message.", nameof(itemsOf));
                written += await WriteChunkAsync(writer, chunk, cancellationToken).ConfigureAwait(false);
            }

            return written;
        }
        catch
        {
            writer.Fault();
            throw;
        }
    }

    /// <summary>Writes one sheet into a writer the caller owns, from items arriving one at a time.</summary>
    /// <param name="writer">The caller's writer; the caller completes it.</param>
    /// <param name="sheetName">The sheet's name; a csv file's single sheet takes no name in the file, but the name must still be valid.</param>
    /// <param name="items">The rows.</param>
    /// <param name="cancellationToken">Stops the write; also flows into the enumeration of <paramref name="items"/>.</param>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// The caller completes the writer. On any failure the writer is faulted. An xlsx or ods sheet holds
    /// at most 1,048,576 rows, the header included; the row past it throws
    /// <see cref="TabularWriteException"/> (<c>write.too-many-rows</c>) after the rows before it were written — choose csv when the
    /// count may exceed it.
    /// </remarks>
    /// <exception cref="TabularWriteException">A value the format cannot hold, a style rule's 4,097th distinct style, or a row past the sheet's limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The writer is not in a state to begin a sheet.</exception>
    public async ValueTask<int> WriteSheetAsync(TabularWriter writer, string sheetName, IAsyncEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);

        try
        {
            writer.BeginSheet(sheetName, _declared, Sheet);
            int rows = 0;

            await foreach (T item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                WriteRow(writer, item);
                rows++;

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return rows;
        }
        catch
        {
            writer.Fault();
            throw;
        }
    }

    /// <summary>Writes one sheet into a writer the caller owns, from items in memory or produced synchronously.</summary>
    /// <param name="writer">The caller's writer; the caller completes it.</param>
    /// <param name="sheetName">The sheet's name; a csv file's single sheet takes no name in the file, but the name must still be valid.</param>
    /// <param name="items">The rows.</param>
    /// <param name="cancellationToken">Stops the write, observed at each flush, about every megabyte written.</param>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// The caller completes the writer. On any failure the writer is faulted. An xlsx or ods sheet holds
    /// at most 1,048,576 rows, the header included; the row past it throws
    /// <see cref="TabularWriteException"/> (<c>write.too-many-rows</c>) after the rows before it were written — choose csv when the
    /// count may exceed it.
    /// </remarks>
    /// <exception cref="TabularWriteException">A value the format cannot hold, a style rule's 4,097th distinct style, or a row past the sheet's limit.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The writer is not in a state to begin a sheet.</exception>
    public async ValueTask<int> WriteSheetAsync(TabularWriter writer, string sheetName, IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);

        try
        {
            writer.BeginSheet(sheetName, _declared, Sheet);
            int rows = 0;

            foreach (T item in items)
            {
                WriteRow(writer, item);
                rows++;

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return rows;
        }
        catch
        {
            writer.Fault();
            throw;
        }
    }

    /// <summary>
    /// Creates the writer, lets the sheet be written, completes the file. Arguments are checked inside
    /// the writer's lifetime, so a refused one still closes the stream.
    /// </summary>
    private static async ValueTask<int> WriteFileAsync(Stream stream, TabularFormat format, TabularWriterOptions? options, Func<TabularWriter, ValueTask<int>> write, CancellationToken cancellationToken)
    {
        TabularWriter writer = TabularWriter.Create(stream, format, options);

        await using (writer.ConfigureAwait(false))
        {
            int rows = await write(writer).ConfigureAwait(false);
            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return rows;
        }
    }

    /// <summary>Writes a chunk's rows, flushing inside it when recommended and once after it.</summary>
    private async ValueTask<int> WriteChunkAsync(TabularWriter writer, IReadOnlyList<T> chunk, CancellationToken cancellationToken)
    {
        for (int i = 0; i < chunk.Count; i++)
        {
            WriteRow(writer, chunk[i]);

            if (writer.FlushRecommended)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return chunk.Count;
    }

    private void WriteRow(TabularWriter writer, T item)
    {
        writer.BeginRow();

        foreach (ExportColumn<T> column in _columns)
        {
            column.Write(writer, item);
        }

        writer.EndRow();
    }
}
