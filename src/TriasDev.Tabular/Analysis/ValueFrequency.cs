namespace TriasDev.Tabular.Analysis;

/// <summary>A value and how often it was seen.</summary>
public readonly record struct ValueFrequency
{
    /// <summary>The value.</summary>
    public required string Value { get; init; }

    /// <summary>How many rows hold it.</summary>
    public required int Count { get; init; }
}
