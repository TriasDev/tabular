namespace TriasDev.Tabular.Archive;

/// <summary>One entry of an archive, as the archive cursor sees it whatever the archive's format.</summary>
/// <param name="Path">The entry's path in the archive, as written — a sheet's <see cref="SheetInfo.Source"/>.</param>
/// <param name="Name">The entry's file name, which a csv sheet is named after.</param>
/// <param name="Length">The bytes the entry declares, uncompressed; 0 for what is not a file.</param>
/// <param name="Kind">Whether it is a file to judge, or why not.</param>
/// <param name="Ordinal">Its position in the archive.</param>
/// <param name="DataOffset">Where its data begins in the archive stream, where that has a meaning; else -1.</param>
internal sealed record ArchiveEntry(string Path, string Name, long Length, ArchiveEntryKind Kind, int Ordinal, long DataOffset);

/// <summary>What an archive entry is, as far as reading tables goes.</summary>
internal enum ArchiveEntryKind
{
    /// <summary>A file, judged by its bytes.</summary>
    File,

    /// <summary>A file encrypted in the archive.</summary>
    Encrypted,

    /// <summary>A file stored in a form not read, such as a GNU sparse file.</summary>
    Unsupported,

    /// <summary>Not a file — a directory, a link, a device — left out without a word.</summary>
    LeftOut,
}
