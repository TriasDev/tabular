using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular;

/// <summary>Opens a file without the caller having to know which kind it is.</summary>
public static class TabularFile
{
    /// <summary>The four bytes every zip archive, and so every xlsx package, begins with.</summary>
    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// Decides what kind of file this is by looking at it.
    /// </summary>
    /// <remarks>
    /// By its bytes, not by its name. A csv saved as <c>.xlsx</c> is commoner than it ought to be —
    /// somebody renames an export, or a browser guesses a content type — and a reader that trusts the
    /// extension fails on it with a message about a corrupt archive, which sends the reader looking
    /// in the wrong place entirely.
    /// </remarks>
    public static TabularFormat Detect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            throw new ArgumentException("Detection rewinds the stream, so it must be seekable.", nameof(stream));
        }

        long origin = stream.Position;

        Span<byte> head = stackalloc byte[4];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = origin;

        return read == head.Length && head.SequenceEqual(ZipSignature)
            ? TabularFormat.Xlsx
            : TabularFormat.Csv;
    }

    /// <summary>Opens a cursor over a file of whichever kind it turns out to be.</summary>
    /// <param name="stream">
    /// The file. Must be seekable. Closed with the cursor, or when opening fails, unless
    /// <see cref="TabularOpenOptions.LeaveOpen"/> says otherwise.
    /// </param>
    /// <param name="name">What to call it; a csv file's single sheet takes this name.</param>
    /// <param name="options">The cursor options for either kind, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the opening, which reads the file's head or directory.</param>
    public static ITabularCursor Open(
        Stream stream,
        string name,
        TabularOpenOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        TabularOpenOptions effective = options ?? TabularOpenOptions.Default;

        try
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            cancellationToken.ThrowIfCancellationRequested();

            // Opening a workbook is not free — the package's parts are enumerated and its style
            // table is read before a single row is available — so the token belongs here as much as
            // on a read.
            return Detect(stream) == TabularFormat.Xlsx
                ? new XlsxCursor(stream, effective.Xlsx, effective.LeaveOpen, cancellationToken)
                : new CsvCursor(stream, name, effective.Csv, effective.LeaveOpen, cancellationToken);
        }
        catch when (!effective.LeaveOpen)
        {
            // The cursors close the stream on their own failures; this covers the ones before a
            // cursor exists. Disposing a stream twice is harmless.
            stream.Dispose();
            throw;
        }
    }
}
