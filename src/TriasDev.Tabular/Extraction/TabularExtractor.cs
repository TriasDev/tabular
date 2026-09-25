
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
    /// <param name="cancellationToken">Stops the run, checked as each row is read.</param>
    /// <exception cref="ArgumentException">The plan does not fit the schema.</exception>
    /// <exception cref="TabularStructureException">The file is not the one the plan was built against.</exception>
    public static ExtractionSession Start(
        ITabularCursor cursor,
        MappingPlan plan,
        TargetSchema schema,
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
            throw new ArgumentException(
                $"The plan does not fit the schema: {string.Join(", ", faults.Select(f => f.Code))}.",
                nameof(plan));
        }

        return new ExtractionSession(cursor, plan, schema, options ?? ExtractionOptions.Default, cancellationToken);
    }
}
