using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
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

        Span<byte> head = stackalloc byte[128];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = origin;

        if (read < 4 || !head[..4].SequenceEqual(ZipSignature))
        {
            return TabularFormat.Csv;
        }

        return IsOpenDocumentSpreadsheet(head[..read]) ? TabularFormat.Ods : TabularFormat.Xlsx;
    }

    /// <summary>
    /// Whether a zip's first bytes are an OpenDocument spreadsheet's: ODF requires the first entry to
    /// be <c>mimetype</c>, stored uncompressed, so its name and content stand in the local header.
    /// </summary>
    /// <remarks>
    /// Read from the header rather than by opening the archive: the name's length is at offset 26, the
    /// extra field's at 28, the name itself at 30 and the content right after both. A spreadsheet
    /// that breaks the rule goes on to the workbook path, which refuses it by name.
    /// </remarks>
    private static bool IsOpenDocumentSpreadsheet(ReadOnlySpan<byte> head)
    {
        if (head.Length < 30)
        {
            return false;
        }

        int nameLength = head[26] | (head[27] << 8);
        int extraLength = head[28] | (head[29] << 8);
        int content = 30 + nameLength + extraLength;
        ReadOnlySpan<byte> mimetype = "application/vnd.oasis.opendocument.spreadsheet"u8;

        return nameLength == 8
            && head.Slice(30, 8).SequenceEqual("mimetype"u8)
            && head.Length >= content + mimetype.Length
            && head.Slice(content, mimetype.Length).SequenceEqual(mimetype)
            && (head.Length == content + mimetype.Length || head[content + mimetype.Length] is not (byte)'-');
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
            return Detect(stream) switch
            {
                TabularFormat.Xlsx => new XlsxCursor(stream, effective.Xlsx, effective.LeaveOpen, cancellationToken),
                TabularFormat.Ods => new OdsCursor(stream, effective.Ods, effective.LeaveOpen, cancellationToken),
                _ => new CsvCursor(stream, name, effective.Csv, effective.LeaveOpen, cancellationToken),
            };
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
