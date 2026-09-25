namespace TriasDev.Tabular.Tests.Fixtures;

/// <summary>
/// A small file whose exact content is known, paired with the cells every reader must produce from it.
/// </summary>
/// <remarks>
/// <para>
/// These are the correctness gate of the parser spike: a candidate library reads every fixture and
/// must agree, cell for cell, before its speed is measured at all. A reader that is fast and returns
/// an empty string for a rich-text cell is not a candidate.
/// </para>
/// <para>
/// <see cref="ExpectedCells"/> is expressed in the normalised form defined by
/// <c>CellNormalization</c>: an absent or empty cell is <c>null</c>, a number is round-tripped
/// through the invariant culture, a date is <c>yyyy-MM-dd</c>, a boolean is <c>true</c> or
/// <c>false</c>, and text is verbatim. Normalising is what makes libraries with different native
/// types — one returns <see cref="double"/>, another the raw string — comparable at all.
/// </para>
/// </remarks>
public sealed record GoldenFixture
{
    /// <summary>Stable identifier, used in test output so a failure names the edge it broke.</summary>
    public required string Name { get; init; }

    /// <summary>File name including extension, as a reader that sniffs by extension would see it.</summary>
    public required string FileName { get; init; }

    /// <summary>The file's exact bytes.</summary>
    public required byte[] Content { get; init; }

    /// <summary>Expected cells, row-major, in normalised form.</summary>
    public required string?[][] ExpectedCells { get; init; }

    /// <summary>The single behaviour this fixture exists to pin.</summary>
    public required string Pins { get; init; }

    public override string ToString() => Name;
}
