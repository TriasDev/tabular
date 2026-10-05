using System.Formats.Tar;

namespace TriasDev.Tabular.Archive;

/// <summary>What the archive cursor needs from <see cref="TarReader"/>'s entries, and its failures in the library's terms.</summary>
internal static class TarEntries
{
    /// <summary>Whether the entry's path may need its ustar header read: see <see cref="PathOf"/>.</summary>
    public static bool NeedsHeader(TarEntry entry) =>
        entry is PaxTarEntry pax && !pax.ExtendedAttributes.ContainsKey("path");

    /// <summary>
    /// The entry's full path. <see cref="TarReader"/> (.NET 8 and 10) joins the ustar prefix field to the
    /// name for a ustar entry, but drops it for a PAX entry whose extended header carries no path —
    /// which is how bsdtar, the tar of macOS and Windows, writes every path that fits the ustar fields.
    /// A path over 100 characters would lose its folders.
    /// </summary>
    /// <param name="entry">The entry as read.</param>
    /// <param name="header">The entry's own 512-byte ustar header, when <see cref="NeedsHeader"/> said to read it; else empty.</param>
    public static string PathOf(TarEntry entry, ReadOnlySpan<byte> header)
    {
        if (!TarHeader.IsHeader(header))
        {
            return entry.Name;
        }

        ReadOnlySpan<byte> field = header.Slice(345, 155);
        int end = field.IndexOf((byte)0);
        string prefix = System.Text.Encoding.UTF8.GetString(end < 0 ? field : field[..end]);

        return prefix.Length == 0 || entry.Name.StartsWith(prefix + "/", StringComparison.Ordinal)
            ? entry.Name
            : prefix + "/" + entry.Name;
    }

    public static ArchiveEntry ToEntry(TarEntry entry, string path, int ordinal, long dataOffset)
    {
        ArchiveEntryKind kind = entry.EntryType switch
        {
            TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => ArchiveEntryKind.File,

            // Its stream is the sparse map and the stored runs, not the file.
            TarEntryType.SparseFile => ArchiveEntryKind.Unsupported,
            _ => ArchiveEntryKind.LeftOut,
        };

        int slash = path.TrimEnd('/').LastIndexOf('/');
        return new ArchiveEntry(path, slash < 0 ? path : path[(slash + 1)..], kind == ArchiveEntryKind.LeftOut ? 0 : entry.Length, kind, ordinal, dataOffset);
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
    /// <remarks>
    /// It also throws <see cref="InvalidOperationException"/> when a PAX or GNU metadata entry's size
    /// field claims more than it will read — a damaged header, not a misuse. Taken only from
    /// <c>System.Formats.Tar</c> itself, so one raised by the library's own code is not mistaken for it.
    /// </remarks>
    public static bool IsFailure(Exception e) =>
        e is EndOfStreamException or InvalidDataException or FormatException or OverflowException or NotSupportedException
        || e is InvalidOperationException { Source: "System.Formats.Tar" };

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
