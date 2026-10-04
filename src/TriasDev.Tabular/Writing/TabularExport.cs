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

    internal TabularExport(ExportColumn<T>[] columns, WriteColumn[] declared)
    {
        _columns = columns;
        _declared = declared;
        _columnsView = Array.AsReadOnly(declared);
    }

    /// <summary>The columns, in order, as a sheet's header row writes them.</summary>
    public IReadOnlyList<WriteColumn> Columns => _columnsView;

    /// <summary>Writes a file of one sheet from chunks as they arrive — the fast source for millions of rows.</summary>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>
    /// The writer is flushed after every chunk and inside a chunk whenever it recommends it, so memory
    /// holds a chunk and about a megabyte, however many rows the file has. On any failure the stream is
    /// closed (unless <see cref="TabularWriterOptions.LeaveOpen"/>) and the file is incomplete.
    /// </remarks>
    public ValueTask<long> WriteAsync(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<IReadOnlyList<T>> chunks, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, chunks, cancellationToken), cancellationToken);

    /// <summary>
    /// Writes a file of one sheet from a stream of messages that each carry a chunk — a gRPC server
    /// stream whose messages hold a repeated field, passed as it comes.
    /// </summary>
    /// <returns>The number of data rows written.</returns>
    public ValueTask<long> WriteAsync<TChunk>(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<TChunk> chunks, Func<TChunk, IReadOnlyList<T>> rows, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, chunks, rows, cancellationToken), cancellationToken);

    /// <summary>Writes a file of one sheet from items arriving one at a time; awaits per item, so prefer chunks for millions.</summary>
    /// <returns>The number of data rows written.</returns>
    public ValueTask<long> WriteAsync(Stream stream, TabularFormat format, string sheetName, IAsyncEnumerable<T> items, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, items, cancellationToken), cancellationToken);

    /// <summary>Writes a file of one sheet from items in memory or produced synchronously.</summary>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>Cancellation is observed at each flush, about every megabyte written.</remarks>
    public ValueTask<long> WriteAsync(Stream stream, TabularFormat format, string sheetName, IEnumerable<T> items, TabularWriterOptions? options = null, CancellationToken cancellationToken = default) =>
        WriteFileAsync(stream, format, options, writer => WriteSheetAsync(writer, sheetName, items, cancellationToken), cancellationToken);

    /// <summary>Writes one sheet into a writer the caller owns, from chunks as they arrive.</summary>
    /// <returns>The number of data rows written.</returns>
    /// <remarks>The caller completes the writer; several exports may write their sheets into one workbook.</remarks>
    public async ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IAsyncEnumerable<IReadOnlyList<T>> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(chunks);

        try
        {
            writer.BeginSheet(sheetName, _declared);
            long rows = 0;

            await foreach (IReadOnlyList<T> chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                rows += await WriteChunkAsync(writer, chunk, cancellationToken).ConfigureAwait(false);
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
    /// <returns>The number of data rows written.</returns>
    public async ValueTask<long> WriteSheetAsync<TChunk>(TabularWriter writer, string sheetName, IAsyncEnumerable<TChunk> chunks, Func<TChunk, IReadOnlyList<T>> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(rows);

        try
        {
            writer.BeginSheet(sheetName, _declared);
            long written = 0;

            await foreach (TChunk message in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                written += await WriteChunkAsync(writer, rows(message), cancellationToken).ConfigureAwait(false);
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
    /// <returns>The number of data rows written.</returns>
    public async ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IAsyncEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);

        try
        {
            writer.BeginSheet(sheetName, _declared);
            long rows = 0;

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
    /// <returns>The number of data rows written.</returns>
    public async ValueTask<long> WriteSheetAsync(TabularWriter writer, string sheetName, IEnumerable<T> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(items);

        try
        {
            writer.BeginSheet(sheetName, _declared);
            long rows = 0;

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
    private static async ValueTask<long> WriteFileAsync(Stream stream, TabularFormat format, TabularWriterOptions? options, Func<TabularWriter, ValueTask<long>> write, CancellationToken cancellationToken)
    {
        TabularWriter writer = TabularWriter.Create(stream, format, options);

        await using (writer.ConfigureAwait(false))
        {
            long rows = await write(writer).ConfigureAwait(false);
            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return rows;
        }
    }

    /// <summary>Writes a chunk's rows, flushing inside it when recommended and once after it.</summary>
    private async ValueTask<long> WriteChunkAsync(TabularWriter writer, IReadOnlyList<T> chunk, CancellationToken cancellationToken)
    {
        if (chunk is null)
        {
            throw new ArgumentException("A chunk is null.", nameof(chunk));
        }

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
