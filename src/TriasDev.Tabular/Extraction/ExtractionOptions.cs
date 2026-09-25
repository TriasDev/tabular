namespace TriasDev.Tabular.Extraction;

/// <summary>Knobs for a run.</summary>
public sealed record ExtractionOptions
{
    /// <summary>The defaults.</summary>
    public static ExtractionOptions Default { get; } = new();

    /// <summary>
    /// How many failing rows a run tolerates before stopping.
    /// </summary>
    /// <remarks>
    /// A mapping that is simply wrong turns every row into a failure, and half a million of them help
    /// nobody: the user has to fix the mapping, and the thousand-and-first error says nothing the
    /// first did not.
    /// </remarks>
    public int MaxErrorRows { get; init; } = 1000;

    /// <summary>
    /// Run everything but keep no values, so a user can be shown what an import would reject before
    /// anything is written.
    /// </summary>
    public bool ValidateOnly { get; init; }
}
