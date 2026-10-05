namespace TriasDev.Tabular;

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

    /// <summary>The most row numbers any list in a report may hold.</summary>
    public const int ReportedRowsCeiling = 10_000;

    /// <summary>
    /// How many row numbers each list of an <see cref="AlternativesReport"/> keeps; the counts are
    /// always complete.
    /// </summary>
    /// <remarks>
    /// Fifty by default: enough for a person to look at, while a list of every row of a large file
    /// would hold memory in proportion to it. At most <see cref="ReportedRowsCeiling"/>.
    /// </remarks>
    public int MaxReportedRows { get; init; } = 50;

    internal ExtractionOptions Checked()
    {
        OptionChecks.AtLeast(MaxErrorRows, 1, nameof(ExtractionOptions), nameof(MaxErrorRows));
        OptionChecks.AtLeast(MaxReportedRows, 1, nameof(ExtractionOptions), nameof(MaxReportedRows));
        OptionChecks.AtMost(MaxReportedRows, ReportedRowsCeiling, nameof(ExtractionOptions), nameof(MaxReportedRows));
        return this;
    }
}
