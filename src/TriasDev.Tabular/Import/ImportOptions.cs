
namespace TriasDev.Tabular;

/// <summary>Knobs for a run.</summary>
public sealed record ImportOptions
{
    /// <summary>The defaults.</summary>
    public static ImportOptions Default { get; } = new();

    /// <summary>
    /// How many rows to keep as a preview.
    /// </summary>
    /// <remarks>
    /// None by default. A screen that shows a person what an import will produce asks for a handful;
    /// a nightly load of five million rows has nobody to show them to, and building the dictionaries
    /// would be work done for no reader.
    /// </remarks>
    public int PreviewRows { get; init; }

    /// <summary>How the file is read underneath.</summary>
    public ExtractionOptions Extraction { get; init; } = ExtractionOptions.Default;

    /// <summary>
    /// How a file handed over as a stream is opened, and whether it is left open afterwards.
    /// </summary>
    /// <remarks>Ignored by a run over a cursor, which the caller opened and closes.</remarks>
    public TabularOpenOptions Open { get; init; } = TabularOpenOptions.Default;
}

/// <summary>How much of a field the file actually filled.</summary>
/// <remarks>
/// The check a person makes before committing, and the one an error list cannot make for them: a
/// field bound to the wrong column is not <em>invalid</em>, it is empty, so it passes every rule and
/// shows up only here.
/// </remarks>
public sealed record FieldCoverage
{
    /// <summary>The field.</summary>
    public required string Field { get; init; }

    /// <summary>Whether a row without it is invalid.</summary>
    public required bool Required { get; init; }

    /// <summary>Rows that carried a value.</summary>
    public required int Filled { get; init; }

    /// <summary>Rows that did not.</summary>
    public required int Empty { get; init; }

    /// <summary>The share of produced rows that carried a value, from zero to one.</summary>
    public required double Share { get; init; }
}

/// <summary>One row as it was mapped, for showing rather than for building.</summary>
public sealed record ImportPreviewRow
{
    /// <summary>The row's number as a person reading the file would count it.</summary>
    public required int RowNumber { get; init; }

    /// <summary>Each field's value rendered as text, invariantly.</summary>
    public required IReadOnlyDictionary<string, string?> Values { get; init; }
}
