namespace TriasDev.Tabular;

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
    /// <summary>What kind of file this was — the container, for an archive; each sheet says its own.</summary>
    public required TabularFormat Format { get; init; }

    /// <summary>One entry per sheet, in file order.</summary>
    public required IReadOnlyList<SheetProfile> Sheets { get; init; }

    /// <summary>What the reader had to repair to get through the file, every sheet together.</summary>
    /// <remarks>Taken when the pass ended: reading the cursor on does not change it.</remarks>
    public required CursorDiagnostics Diagnostics { get; init; }

    /// <summary>The files of an archive that were not read as tables, and why; empty for any other file.</summary>
    public IReadOnlyList<SkippedEntry> SkippedEntries { get; init; } = [];
}
