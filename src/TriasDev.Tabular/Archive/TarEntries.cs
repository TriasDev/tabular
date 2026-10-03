using System.Formats.Tar;

namespace TriasDev.Tabular.Archive;

/// <summary>What the archive cursor needs from <see cref="TarReader"/>'s entries, and its failures in the library's terms.</summary>
internal static class TarEntries
{
    public static ArchiveEntry ToEntry(TarEntry entry, int ordinal, long dataOffset)
    {
        ArchiveEntryKind kind = entry.EntryType switch
        {
            TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => ArchiveEntryKind.File,

            // Its stream is the sparse map and the stored runs, not the file.
            TarEntryType.SparseFile => ArchiveEntryKind.Unsupported,
            _ => ArchiveEntryKind.LeftOut,
        };

        string name = entry.Name;
        int slash = name.TrimEnd('/').LastIndexOf('/');
        return new ArchiveEntry(name, slash < 0 ? name : name[(slash + 1)..], kind == ArchiveEntryKind.LeftOut ? 0 : entry.Length, kind, ordinal, dataOffset);
    }

    /// <summary>Where the entry's data begins in a seekable archive stream, just after <see cref="TarReader.GetNextEntry"/> returned it.</summary>
    public static long DataOffset(TarEntry entry, Stream archive) =>
#if NET9_0_OR_GREATER
        entry.DataOffset;
#else
        // net8 has no DataOffset, and on a seekable stream its TarReader has already sought past the
        // data — padded to 512 bytes — when GetNextEntry returns. Goes with net8 at the .NET 11 release.
        archive.Position - PaddedLength(entry.Length);
#endif

    /// <summary>
    /// The exceptions <see cref="TarReader"/> raises for a cut, damaged or unreadable archive —
    /// <see cref="NotSupportedException"/> for an entry type it cannot read, such as a GNU sparse file,
    /// which it refuses at the header, so nothing after it can be reached either.
    /// </summary>
    public static bool IsFailure(Exception e) =>
        e is EndOfStreamException or InvalidDataException or FormatException or OverflowException or NotSupportedException;

    public static TabularFormatException Map(Exception e) => e switch
    {
        EndOfStreamException => new(TabularFormatException.Truncated, "The tar archive is cut off: it ends before its content does.", e),
        NotSupportedException => new(TabularFormatException.Unsupported,
            $"The tar archive holds an entry that cannot be read ({e.Message}); a GNU sparse file is one. Archive the files without --sparse.", e),
        _ => new(TabularFormatException.Corrupt, $"The tar archive is damaged: {e.Message}", e),
    };

    /// <summary>The bytes an entry's data takes in the archive, padded to whole blocks.</summary>
    public static long PaddedLength(long length) => (length + TarHeader.BlockSize - 1) / TarHeader.BlockSize * TarHeader.BlockSize;
}
