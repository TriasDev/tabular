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
    /// <param name="stream">The file. Must be seekable.</param>
    /// <param name="name">What to call it; a csv file's single sheet takes this name.</param>
    /// <param name="leaveOpen">Whether disposing the cursor leaves the stream open.</param>
    public static ITabularCursor Open(
        Stream stream,
        string name,
        bool leaveOpen = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Opening a workbook is not free — the package's parts are enumerated and its style table is
        // read before a single row is available — so the token belongs here as much as on a read.
        return Detect(stream) == TabularFormat.Xlsx
            ? new XlsxCursor(stream, leaveOpen: leaveOpen, cancellationToken: cancellationToken)
            : new CsvCursor(stream, name, leaveOpen: leaveOpen);
    }
}
