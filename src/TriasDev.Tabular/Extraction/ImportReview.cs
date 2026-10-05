namespace TriasDev.Tabular;

/// <summary>Knobs for <see cref="TabularExtractor.Review"/>.</summary>
public sealed record ReviewOptions
{
    /// <summary>The defaults.</summary>
    public static ReviewOptions Default { get; } = new();

    /// <summary>
    /// How many row errors the review keeps; every one is counted in the summary.
    /// </summary>
    /// <remarks>
    /// At most <see cref="ExtractionOptions.ReportedRowsCeiling"/>, so a file that fails on every row
    /// holds no list in proportion to it.
    /// </remarks>
    public int MaxErrors { get; init; } = 50;

    /// <summary>How many row numbers each list of an <see cref="AlternativesReport"/> keeps.</summary>
    public int MaxReportedRows { get; init; } = 50;

    /// <summary>Data rows between progress reports, as for analysis.</summary>
    public int ProgressInterval { get; init; } = 10_000;

    /// <summary>The fraction of the file read between progress reports, as for analysis.</summary>
    public double ProgressStep { get; init; } = 0.01;

    internal ReviewOptions Checked()
    {
        OptionChecks.AtLeast(MaxErrors, 1, nameof(ReviewOptions), nameof(MaxErrors));
        OptionChecks.AtMost(MaxErrors, ExtractionOptions.ReportedRowsCeiling, nameof(ReviewOptions), nameof(MaxErrors));
        OptionChecks.AtLeast(ProgressInterval, 1, nameof(ReviewOptions), nameof(ProgressInterval));
        OptionChecks.Fraction(ProgressStep, nameof(ReviewOptions), nameof(ProgressStep));

        return this;
    }
}

/// <summary>
/// What an import through a mapping would meet, found by reading the whole file once and keeping
/// nothing.
/// </summary>
public sealed record ImportReview
{
    /// <summary>The run's counts, over every row of the file.</summary>
    public required ExtractionSummary Summary { get; init; }

    /// <summary>The first row errors, up to <see cref="ReviewOptions.MaxErrors"/>.</summary>
    public required IReadOnlyList<RowError> Errors { get; init => field = Equatable.List(value); }

    /// <summary>Whether <see cref="Errors"/> holds every error the summary counts.</summary>
    public required bool ErrorsComplete { get; init; }

    /// <summary>What the run found about each set of alternatives; empty for a schema without them.</summary>
    public required IReadOnlyList<AlternativesReport> Alternatives { get; init => field = Equatable.List(value); }
}
