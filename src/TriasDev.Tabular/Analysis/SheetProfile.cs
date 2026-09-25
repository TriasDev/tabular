using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular;

/// <summary>What is known about one sheet.</summary>
public sealed record SheetProfile
{
    /// <summary>Position in the file, zero-based.</summary>
    public required int Index { get; init; }

    /// <summary>The sheet's name, or the file's for a csv.</summary>
    public required string Name { get; init; }

    /// <summary>The format of the file the sheet was read from.</summary>
    public required TabularFormat Format { get; init; }

    /// <summary>Where inside an archive the sheet's file lies, or null for a plain file.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// How the sheet's file was punctuated and encoded, and how that was decided. Null for a
    /// workbook sheet.
    /// </summary>
    /// <remarks>
    /// On the sheet rather than the file because an archive holds several csv files, each detected
    /// on its own — one in UTF-8 with semicolons beside one in Windows-1252 with commas.
    /// </remarks>
    public CsvDialect? Dialect { get; init; }

    /// <summary>What the reader had to repair while reading this sheet.</summary>
    public required CursorDiagnostics Diagnostics { get; init; }

    /// <summary>
    /// Rows of data, not counting the header.
    /// </summary>
    public required int RowCount { get; init; }

    /// <summary>One entry per column, in file order.</summary>
    public required IReadOnlyList<ColumnProfile> Columns { get; init; }

    /// <summary>
    /// The row the header was read from, zero-based.
    /// </summary>
    /// <remarks>
    /// Recorded because a mapping names one too, and a profile measured against a different one
    /// describes a different file: the real header row and everything above it were counted as data,
    /// so lengths, types and distinct values all include cells the import will never read. Carrying
    /// the number is what lets a precheck notice, rather than answer confidently about the wrong
    /// thing.
    /// </remarks>
    public required int HeaderRowIndex { get; init; }
}
