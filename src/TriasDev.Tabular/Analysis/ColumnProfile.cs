namespace TriasDev.Tabular.Analysis;

/// <summary>What is known about one column: what was measured, and what that suggests.</summary>
public sealed record ColumnProfile
{
    /// <summary>What was counted.</summary>
    public required ColumnFacts Facts { get; init; }

    /// <summary>What those counts suggest, most convincing first.</summary>
    public required IReadOnlyList<TypeHypothesis> Hypotheses { get; init; }
}
