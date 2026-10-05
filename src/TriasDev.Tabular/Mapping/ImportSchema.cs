namespace TriasDev.Tabular;

/// <summary>What a caller wants an import to produce.</summary>
public sealed class ImportSchema
{
    /// <summary>The fields, in the order a caller wants them.</summary>
    public required IReadOnlyList<ImportField> Fields { get; init; }

    /// <summary>
    /// Sets of field groups in priority order, judged per row.
    /// </summary>
    /// <remarks>
    /// Empty by default, and then costs nothing: a run with no alternatives does not look for them.
    /// </remarks>
    public IReadOnlyList<FieldAlternatives> Alternatives { get; init; } = [];

    /// <summary>
    /// What to do about a file that does not import cleanly.
    /// </summary>
    /// <remarks>
    /// The programmer's decision, not the person uploading. Whether half an import is better than
    /// none depends on what is being imported, and only whoever declared the target knows.
    /// </remarks>
    public ImportPolicy Policy { get; init; } = ImportPolicy.BestEffort;
}
