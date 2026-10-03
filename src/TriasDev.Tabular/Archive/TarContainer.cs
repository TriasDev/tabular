using System.Formats.Tar;

namespace TriasDev.Tabular.Archive;

/// <summary>
/// The entries of an uncompressed tar: indexed in one pass over the headers, read in place.
/// </summary>
/// <remarks>
/// <see cref="TarReader"/> skips each entry's data by seeking, so listing reads little more than the
/// headers. An entry is opened as a window onto the file; a workbook needs nothing more, since a tar
/// does not compress — it is read where it lies, never copied.
/// </remarks>
internal sealed class TarContainer : ArchiveContainer
{
    private readonly Stream _file;
    private readonly List<ArchiveEntry> _entries = [];

    /// <param name="file">The archive, seekable, positioned at its first header; left open.</param>
    /// <param name="maxEntries">The entry bound, enforced while indexing so a hostile header run cannot grow the index first.</param>
    /// <param name="cancellationToken">Stops the indexing.</param>
    public TarContainer(Stream file, int maxEntries, CancellationToken cancellationToken)
    {
        _file = file;
        long end = file.Position;

        try
        {
            using TarReader reader = new(file, leaveOpen: true);

            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_entries.Count == maxEntries)
                {
                    throw new TabularLimitException(nameof(ArchiveCursorOptions.MaxEntries), maxEntries,
                        $"The archive holds more than the {maxEntries} files allowed.");
                }

                long dataOffset = TarEntries.DataOffset(entry, file);
                string path = TarEntries.NeedsHeader(entry) ? TarEntries.PathOf(entry, HeaderBefore(dataOffset)) : entry.Name;
                ArchiveEntry indexed = TarEntries.ToEntry(entry, path, _entries.Count, dataOffset);

                if (indexed.DataOffset + indexed.Length > file.Length)
                {
                    throw new TabularFormatException(TabularFormatException.Truncated,
                        "The tar archive is cut off: a file inside it ends past the end of the archive.");
                }

                _entries.Add(indexed);
                end = dataOffset + TarEntries.PaddedLength(entry.Length);
            }
        }
        catch (Exception e) when (TarEntries.IsFailure(e))
        {
            throw TarEntries.Map(e);
        }

        RequireEndMarker(end);
    }

    public override TabularFormat Format => TabularFormat.Tar;

    public override IReadOnlyList<ArchiveEntry> Directory => _entries;

    public override IEnumerable<ArchiveEntry> Entries(CancellationToken cancellationToken) => _entries;

    public override Stream Open(ArchiveEntry entry, CancellationToken cancellationToken) => new StreamWindow(_file, entry.DataOffset, entry.Length);

    public override Stream OpenSeekable(ArchiveEntry entry) => new StreamWindow(_file, entry.DataOffset, entry.Length);

    /// <summary>The ustar header right before an entry's data, read without moving the reader's place in the file.</summary>
    private byte[] HeaderBefore(long dataOffset)
    {
        byte[] header = new byte[TarHeader.BlockSize];

        if (dataOffset < TarHeader.BlockSize)
        {
            return [];
        }

        long place = _file.Position;
        _file.Position = dataOffset - TarHeader.BlockSize;
        int read = _file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        _file.Position = place;
        return read == header.Length ? header : [];
    }

    /// <summary>
    /// A zero block must follow the last entry. On a seekable stream <see cref="TarReader"/> takes the
    /// end of the file for the end of the archive, so a tar cut exactly between two entries would
    /// otherwise read as a complete, smaller one.
    /// </summary>
    private void RequireEndMarker(long end)
    {
        byte[] block = new byte[TarHeader.BlockSize];
        _file.Position = end;
        int read = _file.ReadAtLeast(block, block.Length, throwOnEndOfStream: false);

        if (read < block.Length || block.AsSpan().ContainsAnyExcept((byte)0))
        {
            throw new TabularFormatException(TabularFormatException.Truncated,
                "The tar archive is cut off: it ends without the zero block that closes a tar.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        // The windows own nothing and the file is the cursor's.
    }
}
