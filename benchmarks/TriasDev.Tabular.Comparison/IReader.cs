namespace TriasDev.Tabular.Comparison;

/// <summary>Which kind of file a reader takes.</summary>
internal enum FileKind
{
    Csv,
    Xlsx,
}

/// <summary>What one pass over a file produced.</summary>
/// <param name="Rows">Rows the library reported, the header row included.</param>
/// <param name="Values">Cells that carried a non-empty value.</param>
internal readonly record struct ReadCount(long Rows, long Values);

/// <summary>
/// One library, reading one file from start to end the way its documentation recommends for speed.
/// </summary>
/// <remarks>
/// Every reader does the same work: visit every row of the first sheet and take every cell's value
/// as text. Text because that is what an import consumes and what every library can hand over; a
/// library that returns typed values pays for formatting them, which is stated in the results rather
/// than hidden. Each reader loops in its own idiom, so the harness adds nothing per cell.
/// </remarks>
internal interface IReader
{
    /// <summary>The name shown in the results.</summary>
    string Name { get; }

    /// <summary>A type from the library, used to report which version was measured.</summary>
    Type Anchor { get; }

    FileKind Kind { get; }

    /// <summary>The csv delimiter, for libraries that do not detect it.</summary>
    ReadCount Read(string path, char delimiter);
}

/// <summary>Counts values without letting the compiler discard the reads.</summary>
internal struct Tally
{
    public long Rows;
    public long Values;

    public void Row() => Rows++;

    public void Value(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Values++;
        }
    }

    public readonly ReadCount Result => new(Rows, Values);
}
