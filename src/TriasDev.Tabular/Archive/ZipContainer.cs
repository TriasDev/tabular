using System.IO.Compression;

namespace TriasDev.Tabular.Archive;

/// <summary>The entries of a zip archive, read through its central directory.</summary>
internal sealed class ZipContainer : ArchiveContainer
{
    private readonly ZipArchive _zip;
    private readonly List<ZipArchiveEntry> _zipEntries;
    private readonly List<ArchiveEntry> _entries;

    /// <param name="stream">The archive; left open.</param>
    /// <param name="maxEntries">
    /// Checked against the count the end record declares before the directory is walked, when the
    /// stream can seek to it; the cursor counts the entries again as it lists them.
    /// </param>
    public ZipContainer(Stream stream, int maxEntries)
    {
        if (stream.CanSeek && ZipEndRecord.DeclaredEntries(stream) is { } declared && declared > maxEntries)
        {
            throw new TabularLimitException(nameof(ArchiveCursorOptions.MaxEntries), maxEntries,
                $"The archive declares {declared} files, more than the {maxEntries} allowed.");
        }

        try
        {
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            _zipEntries = [.. _zip.Entries];
        }
        catch (InvalidDataException e)
        {
            throw new TabularFormatException(TabularFormatException.Corrupt, $"The archive is not readable: {e.Message}", e);
        }

        _entries = [.. _zipEntries.Select((entry, ordinal) => new ArchiveEntry(
            entry.FullName, entry.Name, entry.Length, entry.IsEncrypted ? ArchiveEntryKind.Encrypted : ArchiveEntryKind.File, ordinal, -1))];
    }

    public override TabularFormat Format => TabularFormat.Zip;

    public override IReadOnlyList<ArchiveEntry> Directory => _entries;

    public override IEnumerable<ArchiveEntry> Entries(CancellationToken cancellationToken) => _entries;

    /// <remarks>Raises the decompressor's <see cref="InvalidDataException"/> unchanged: one damaged entry is not a damaged archive.</remarks>
    public override Stream Open(ArchiveEntry entry, CancellationToken cancellationToken) => _zipEntries[entry.Ordinal].Open();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _zip.Dispose();
        }
    }
}
