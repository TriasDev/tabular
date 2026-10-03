namespace TriasDev.Tabular.Archive;

/// <summary>
/// Where an archive cursor gets its entries from: a zip, a tar, a compressed tar.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Directory"/> is the full list of entries when the container can read it before any
/// entry is opened (zip, tar), and null for an archive that can only be read as a stream (tar.gz).
/// <see cref="Entries"/> yields every entry, of every kind, in the order the archive stores them;
/// while an entry is the one most recently yielded, <see cref="Open"/> on it is cheap for every
/// container. <see cref="Open"/> may be called again later for any entry — for a stream-only archive
/// that means reading it again up to the entry.
/// </para>
/// <para>
/// Streams from <see cref="Open"/> are forward-only and the caller's to dispose; at most one is in use
/// at a time. A zip entry's damage surfaces as <see cref="InvalidDataException"/>, which the cursor
/// turns into a skip or a refusal as before; every other container raises only
/// <see cref="TabularException"/>s, and they fail the archive.
/// </para>
/// </remarks>
internal abstract class ArchiveContainer : IDisposable
{
    public abstract TabularFormat Format { get; }

    public abstract IReadOnlyList<ArchiveEntry>? Directory { get; }

    /// <summary>Chooses the container by the stream's bytes. Anything not recognised is tried as a zip, which refuses it as before.</summary>
    public static ArchiveContainer Choose(Stream stream, TabularOpenOptions options, CancellationToken cancellationToken)
    {
        long origin = stream.Position;
        byte[] head = new byte[TarHeader.BlockSize];
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        stream.Position = origin;

        return TarHeader.IsHeader(head.AsSpan(0, read))
            ? new TarContainer(stream, options.Archive.MaxEntries, cancellationToken)
            : new ZipContainer(stream);
    }

    public abstract IEnumerable<ArchiveEntry> Entries(CancellationToken cancellationToken);

    public abstract Stream Open(ArchiveEntry entry, CancellationToken cancellationToken);

    /// <summary>A random-access stream onto the entry when that costs nothing — a file of an uncompressed archive; else null.</summary>
    public virtual Stream? OpenSeekable(ArchiveEntry entry) => null;

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected abstract void Dispose(bool disposing);
}
