using System.Collections;

namespace TriasDev.Tabular;

/// <summary>Builds one item from one validated row.</summary>
/// <remarks>
/// A delegate rather than an interface, because a mapper is one expression and an interface would be
/// ceremony around it. The row is a <c>ref struct</c>, which is why this is declared here rather than
/// being a <see cref="Func{T, TResult}"/>.
/// </remarks>
/// <typeparam name="T">What is built.</typeparam>
public delegate T TabularRowMapper<out T>(ImportRow row);

/// <summary>
/// Reads a file through a mapping and hands back what a caller asked to build.
/// </summary>
/// <remarks>
/// The whole of the library's work in one call. A caller declares its fields, writes one expression
/// that turns a row into its own type, and reads the result; swapping one kind of entity for another
/// is a different schema and a different expression, and nothing else.
/// </remarks>
public static class TabularImporter
{
    /// <summary>Starts a run.</summary>
    /// <param name="cursor">An open cursor over the file.</param>
    /// <param name="plan">What a person decided in a mapping screen.</param>
    /// <param name="schema">The fields to fill, declared with <see cref="ImportField"/>.</param>
    /// <param name="mapper">Turns one validated row into an item.</param>
    /// <param name="options">Run options, or null for the defaults.</param>
    /// <param name="cancellationToken">
    /// Stops the run: the opening and positioning done here, and every read after it. A read can
    /// take a token of its own as well.
    /// </param>
    /// <typeparam name="T">What the mapper builds.</typeparam>
    public static ImportRun<T> Import<T>(
        ITabularCursor cursor,
        MappingPlan plan,
        TargetSchema schema,
        TabularRowMapper<T> mapper,
        ImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(schema);

        ImportOptions effective = options ?? ImportOptions.Default;

        ExtractionSession session = TabularExtractor.Start(
            cursor,
            plan,
            schema,
            effective.Extraction,
            cancellationToken);

        return new ImportRun<T>(session, schema, new FieldIndex(schema), mapper, effective, null);
    }

    /// <summary>
    /// Starts a run straight from a file, opening it and closing it.
    /// </summary>
    /// <remarks>
    /// What a caller normally wants. The cursor exists so that the layers underneath can be reached,
    /// not so that everybody has to construct one and then hand it over untouched.
    /// </remarks>
    /// <param name="stream">
    /// The file. Must be seekable. Closed with the run — or when the call fails, before or after
    /// opening it — unless <see cref="TabularOpenOptions.LeaveOpen"/> in
    /// <see cref="ImportOptions.Open"/> says otherwise.
    /// </param>
    /// <param name="name">What to call it; a csv file's single sheet takes this name.</param>
    /// <param name="plan">What a person decided in a mapping screen.</param>
    /// <param name="schema">The fields to fill, declared with <see cref="ImportField"/>.</param>
    /// <param name="mapper">Turns one validated row into an item.</param>
    /// <param name="options">Run options, or null for the defaults.</param>
    /// <param name="cancellationToken">
    /// Stops the run: the opening and positioning done here, and every read after it. A read can
    /// take a token of its own as well.
    /// </param>
    /// <typeparam name="T">What the mapper builds.</typeparam>
    public static ImportRun<T> Import<T>(
        Stream stream,
        string name,
        MappingPlan plan,
        TargetSchema schema,
        TabularRowMapper<T> mapper,
        ImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ImportOptions effective = options ?? ImportOptions.Default;

        try
        {
            ArgumentNullException.ThrowIfNull(mapper);
            ArgumentNullException.ThrowIfNull(schema);
            Validate(plan, schema);
        }
        catch when (!effective.Open.LeaveOpen)
        {
            // One rule for a stream handed over: closed on every path unless the caller said not to.
            stream.Dispose();
            throw;
        }

        ITabularCursor cursor = TabularFile.Open(stream, name, effective.Open, cancellationToken);

        try
        {
            ExtractionSession session = TabularExtractor.Start(
                cursor,
                plan,
                schema,
                effective.Extraction,
                cancellationToken);

            return new ImportRun<T>(session, schema, new FieldIndex(schema), mapper, effective, cursor);
        }
        catch
        {
            cursor.Dispose();
            throw;
        }
    }

    private static void Validate(MappingPlan plan, TargetSchema schema)
    {
        // Before the file is touched. A mapping that does not fit its schema is a fault of the
        // mapping, and answering it costs nothing — whereas opening the file reads its head to
        // detect the dialect, or its whole central directory to open the package. The contract says
        // no stream is opened for a plan that cannot work, and this is what makes that true when the
        // caller hands over a stream rather than a cursor.
        IReadOnlyList<MappingFault> faults = MappingPlanValidator.Validate(plan, schema);

        if (faults.Count > 0)
        {
            throw new MappingPlanException(faults);
        }
    }
}

/// <summary>
/// One run, read row by row, in batches, or all at once.
/// </summary>
/// <remarks>
/// The three shapes sit over one streaming core, so the convenient one and the scalable one cannot
/// disagree about what the file contains. A run is read once.
/// </remarks>
/// <typeparam name="T">What the mapper builds.</typeparam>
public sealed class ImportRun<T> : IEnumerable<ImportOutcome<T>>, IDisposable
{
    private readonly ExtractionSession _session;
    private readonly TargetField[] _fields;
    private readonly FieldIndex _index;
    private readonly TabularRowMapper<T> _mapper;
    private readonly ImportOptions _options;

    /// <summary>The cursor this run opened itself, and must therefore close.</summary>
    private readonly ITabularCursor? _owned;

    private readonly int[] _filled;
    private readonly bool _allOrNothing;
    private readonly List<ImportPreviewRow> _preview = [];
    private bool _read;
    private bool _disposed;

    internal ImportRun(
        ExtractionSession session,
        TargetSchema schema,
        FieldIndex index,
        TabularRowMapper<T> mapper,
        ImportOptions options,
        ITabularCursor? owned)
    {
        _session = session;
        _fields = [.. schema.Fields];
        _index = index;
        _mapper = mapper;
        _options = options;
        _owned = owned;
        _filled = new int[_fields.Length];
        _allOrNothing = schema.Policy == ImportPolicy.AllOrNothing;
    }

    /// <summary>What the run amounted to. Complete once it has been read out.</summary>
    public ExtractionSummary Summary => _session.Summary;

    /// <summary>
    /// How much of each field the file filled. Complete once the run has been read out.
    /// </summary>
    /// <remarks>
    /// Counted always, because it costs one check per field and answers the question an error list
    /// cannot: whether the columns a person mapped actually carry anything.
    /// </remarks>
    public IReadOnlyList<FieldCoverage> Coverage
    {
        get
        {
            int produced = _session.Summary.RowsProduced;

            return
            [
                // `declared` rather than `field`: inside a property accessor that word is now a
                // keyword bound to the synthesised backing field.
                .. _fields.Select((declared, i) => new FieldCoverage
                {
                    Field = declared.Name,
                    Required = declared.Required,
                    Filled = _filled[i],
                    Empty = produced - _filled[i],
                    Share = produced == 0 ? 0 : Math.Round((double)_filled[i] / produced, 3),
                }),
            ];
        }
    }

    /// <summary>
    /// The first rows as they were mapped, when <see cref="ImportOptions.PreviewRows"/> asked for any.
    /// </summary>
    public IReadOnlyList<ImportPreviewRow> Preview => _preview;

    /// <summary>Reads the file one row at a time; the run's own token stops it.</summary>
    public IEnumerator<ImportOutcome<T>> GetEnumerator() => Rows().GetEnumerator();

    /// <summary>Reads the file one row at a time.</summary>
    /// <param name="cancellationToken">
    /// Stops the read. The token the run was started with stops it too.
    /// </param>
    public IEnumerable<ImportOutcome<T>> Rows(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_read)
        {
            throw new InvalidOperationException("A run is read once; start another to read the file again.");
        }

        _read = true;

        while (_session.ReadRow(cancellationToken))
        {
            // The mapper is called from a separate method because an iterator may not hold a
            // ref struct across a yield, and ImportRow is one on purpose.
            yield return Current();
        }
    }

    /// <summary>
    /// Reads the file in batches, for a caller that writes in batches.
    /// </summary>
    /// <param name="size">How many built items a batch holds at most.</param>
    /// <param name="cancellationToken">
    /// Stops the read. The token the run was started with stops it too.
    /// </param>
    /// <remarks>
    /// A batch closes when it has <paramref name="size"/> items, so a run of nothing but failures
    /// still reports them — at the end, in a final batch carrying no items.
    /// </remarks>
    public IEnumerable<ImportChunk<T>> InChunks(int size, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        List<T> items = new(size);
        List<RowError> errors = [];
        int first = 0;
        int last = 0;

        foreach (ImportOutcome<T> outcome in Rows(cancellationToken))
        {
            if (first == 0)
            {
                first = outcome.RowNumber;
            }

            last = outcome.RowNumber;

            if (outcome.HasErrors)
            {
                errors.AddRange(outcome.Errors);
            }
            else
            {
                items.Add(outcome.Value);
            }

            // Under AllOrNothing a failure ends the run, and the batch it falls in carries no items:
            // batches before it were handed out already, and are the caller's to roll back.
            if (_allOrNothing && errors.Count > 0)
            {
                items.Clear();
            }

            if (items.Count >= size)
            {
                yield return new ImportChunk<T>([.. items], [.. errors], first, last);
                items.Clear();
                errors.Clear();
                first = 0;
            }
        }

        if (items.Count > 0 || errors.Count > 0)
        {
            yield return new ImportChunk<T>([.. items], [.. errors], first, last);
        }
    }

    /// <summary>
    /// Reads the whole file into memory.
    /// </summary>
    /// <param name="limit">
    /// How many items may be held. Exceeding it fails the run rather than the host: a convenience
    /// that quietly consumes a machine is not a convenience.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the read. The token the run was started with stops it too.
    /// </param>
    public ImportResult<T> All(int limit = 100_000, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        List<T> items = [];
        List<RowError> errors = [];

        foreach (ImportOutcome<T> outcome in Rows(cancellationToken))
        {
            if (outcome.HasErrors)
            {
                errors.AddRange(outcome.Errors);
                continue;
            }

            if (items.Count >= limit)
            {
                throw new InvalidOperationException(
                    $"The file holds more than the {limit} items this call will keep. Read it in "
                    + $"batches with {nameof(InChunks)} instead.");
            }

            items.Add(outcome.Value);
        }

        // Nothing unless everything: the run stopped at the first failure, and what came before it
        // is not a partial result to keep.
        return new ImportResult<T>(_allOrNothing && errors.Count > 0 ? [] : items, errors, Summary);
    }

    private ImportOutcome<T> Current()
    {
        if (_session.CurrentRowHasErrors)
        {
            return ImportOutcome<T>.Failed(_session.CurrentRowNumber, [.. _session.CurrentErrors]);
        }

        ImportRow row = new(_session.CurrentValues, _index, _session.CurrentRowNumber);

        Observe(row);

        // Not wrapped in a catch. A mapper that throws has a defect in it, and turning a null
        // reference into a row error would file the caller's bug under the file's faults.
        return ImportOutcome<T>.Built(_session.CurrentRowNumber, _mapper(row));
    }

    /// <summary>Notes what the row carried, before the mapper turns it into something else.</summary>
    private void Observe(ImportRow row)
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            if (row.Has(_fields[i]))
            {
                _filled[i]++;
            }
        }

        if (_preview.Count >= _options.PreviewRows)
        {
            return;
        }

        Dictionary<string, string?> values = new(StringComparer.Ordinal);

        foreach (TargetField field in _fields)
        {
            values[field.Name] = row.AsText(field);
        }

        _preview.Add(new ImportPreviewRow { RowNumber = row.RowNumber, Values = values });
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owned?.Dispose();
    }
}

/// <summary>Everything a whole file produced.</summary>
/// <typeparam name="T">What the mapper built.</typeparam>
/// <param name="Items">What was built.</param>
/// <param name="Errors">Why rows produced nothing.</param>
/// <param name="Summary">What the run amounted to.</param>
public sealed record ImportResult<T>(
    IReadOnlyList<T> Items,
    IReadOnlyList<RowError> Errors,
    ExtractionSummary Summary);
