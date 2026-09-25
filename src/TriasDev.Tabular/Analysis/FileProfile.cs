using TriasDev.Tabular.Abstractions;
using TriasDev.Tabular.Csv;

namespace TriasDev.Tabular.Analysis;

/// <summary>
/// Everything analysis learned about a file.
/// </summary>
/// <remarks>
/// Produced by one complete pass. Not a sample: the questions this answers — is every value the same
/// length, does this column ever hold a number, are the values all different — are falsified by a
/// single row anywhere, so reading part of the file cannot answer them at all.
/// </remarks>
public sealed record FileProfile
{
    /// <summary>What kind of file this was.</summary>
    public required TabularFormat Format { get; init; }

    /// <summary>How the file was punctuated and encoded, and how that was decided. Null for a workbook.</summary>
    public CsvDialect? Dialect { get; init; }

    /// <summary>One entry per sheet, in file order.</summary>
    public required IReadOnlyList<SheetProfile> Sheets { get; init; }

    /// <summary>What the reader had to repair to get through the file.</summary>
    public required CursorDiagnostics Diagnostics { get; init; }
}
