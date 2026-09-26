namespace TriasDev.Tabular;

/// <summary>A file inside an archive that was not read as a table, and why.</summary>
/// <remarks>
/// So that a UI can say "<c>scan.pdf</c> skipped: not a table" rather than leave a user wondering
/// where a file went. Directories, hidden files and <c>__MACOSX/</c> are left out silently: nobody
/// put them there on purpose.
/// </remarks>
public sealed record SkippedEntry
{
    /// <summary>The entry's path inside the archive.</summary>
    public required string Path { get; init; }

    /// <summary>Why it was not read.</summary>
    public required SkippedEntryReason Reason { get; init; }
}

/// <summary>Why a file inside an archive was not read as a table.</summary>
/// <remarks>Open to new members, as <see cref="TabularFormat"/> is.</remarks>
public enum SkippedEntryReason
{
    /// <summary>A zip inside the archive that is not a workbook. Archives are not opened recursively.</summary>
    NestedArchive,

    /// <summary>An OpenDocument file that is not a spreadsheet — a text document, a presentation.</summary>
    OtherDocument,

    /// <summary>A legacy Excel workbook (.xls), or an Excel file protected with a password.</summary>
    LegacyWorkbook,

    /// <summary>An XML document, such as a flat OpenDocument or an Excel 2003 XML spreadsheet.</summary>
    XmlDocument,

    /// <summary>Not text: an image, a PDF, any binary file.</summary>
    Binary,

    /// <summary>Encrypted in the archive.</summary>
    Encrypted,

    /// <summary>A workbook of a kind this library does not read, such as a binary .xlsb.</summary>
    Unsupported,

    /// <summary>A workbook that is damaged or cut off.</summary>
    Unreadable,
}
