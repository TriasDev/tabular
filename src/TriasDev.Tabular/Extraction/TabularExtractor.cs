
namespace TriasDev.Tabular;

/// <summary>
/// Reads a file through a mapping a user confirmed.
/// </summary>
/// <remarks>
/// Never runs on a hypothesis. Analysis proposes readings and a person accepts or overrules them;
/// what arrives here is that decision, and the extractor's job is to carry it out and to say plainly
/// where the file does not bear it.
/// </remarks>
public static class TabularExtractor
{
    /// <summary>
    /// Starts a run.
    /// </summary>
    /// <param name="cursor">An open cursor over the file.</param>
    /// <param name="plan">What a user decided.</param>
    /// <param name="schema">What the caller wants filled.</param>
    /// <param name="options">Run options, or null for the defaults.</param>
    /// <param name="cancellationToken">
    /// Stops the run: the positioning done here, and every read after it. A read can take a token
    /// of its own as well.
    /// </param>
    /// <exception cref="MappingPlanException">The plan does not fit the schema.</exception>
    /// <exception cref="TabularStructureException">The file is not the one the plan was built against.</exception>
    public static ExtractionRun Extract(
        ITabularCursor cursor,
        MappingPlan plan,
        ImportSchema schema,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(schema);

        IReadOnlyList<MappingFault> faults = MappingPlanValidator.Validate(plan, schema);

        if (faults.Count > 0)
        {
            // Checked here as well as by whoever built the plan, because starting a run on a plan
            // that cannot work would report the mapping's faults as if they were the data's.
            throw new MappingPlanException(faults);
        }

        return new ExtractionRun(cursor, plan, schema, (options ?? ExtractionOptions.Default).Checked(), cancellationToken);
    }

    /// <summary>
    /// Reads the whole file through a mapping without keeping a value, and reports what an import
    /// would meet: its errors, its counts and how each set of alternatives covers the rows.
    /// </summary>
    /// <remarks>
    /// Exact where a precheck can only estimate, because it reads every row as the import will — at
    /// the cost of one more pass over the file. It never stops at an error limit, nor for
    /// <see cref="ImportPolicy.AllOrNothing"/>: it is the whole file's account.
    /// </remarks>
    /// <param name="cursor">An open cursor over the file.</param>
    /// <param name="plan">What a user decided.</param>
    /// <param name="schema">What the caller wants filled.</param>
    /// <param name="options">Review options, or null for the defaults; checked here, before anything is read.</param>
    /// <param name="progress">Told as analysis tells it; null to report nothing.</param>
    /// <param name="cancellationToken">Stops the review, including inside a single read.</param>
    /// <exception cref="MappingPlanException">The plan does not fit the schema.</exception>
    /// <exception cref="TabularStructureException">The file is not the one the plan was built against.</exception>
    public static ImportReview Review(
        ITabularCursor cursor,
        MappingPlan plan,
        ImportSchema schema,
        ReviewOptions? options = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(schema);

        ReviewOptions effective = (options ?? ReviewOptions.Default).Checked();
        ExtractionOptions extraction = new ExtractionOptions { ValidateOnly = true, MaxReportedRows = effective.MaxReportedRows }.Checked();

        IReadOnlyList<MappingFault> faults = MappingPlanValidator.Validate(plan, schema);

        if (faults.Count > 0)
        {
            throw new MappingPlanException(faults);
        }

        ExtractionRun run = new(cursor, plan, schema, extraction, cancellationToken, ignoreErrorLimit: true);
        ProgressReporter reporter = new(progress, cursor, effective.ProgressInterval, effective.ProgressStep);
        SheetInfo sheet = cursor.Sheets[plan.SheetIndex];
        List<RowError> errors = [];

        // Progress counts the rows the run hands back; the blank rows it skips are few, and counting
        // them would need a hook into its inner loop for no visible difference.
        while (run.ReadRow(cancellationToken))
        {
            reporter.Row(sheet);

            for (int i = 0; i < run.CurrentErrors.Count && errors.Count < effective.MaxErrors; i++)
            {
                errors.Add(run.CurrentErrors[i]);
            }
        }

        reporter.Complete();
        ExtractionSummary summary = run.Summary;

        return new ImportReview
        {
            Summary = summary,
            Errors = errors,
            ErrorsComplete = errors.Count == summary.ErrorCount,
            Alternatives = run.Alternatives,
        };
    }
}
