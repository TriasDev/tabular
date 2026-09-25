using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// One run of a file through a mapping.
/// </summary>
/// <remarks>
/// <para>
/// Shaped like the cursor beneath it — advance, then look at what is current — for the same reason:
/// the values belong to a buffer that is reused, and a shape that handed out a fresh object per row
/// would allocate one per row of a file that may hold millions.
/// </para>
/// <para>
/// A row is either values or errors, never both. Half a row invites a caller to build half an
/// entity, which is how silent corruption starts.
/// </para>
/// </remarks>
public sealed class ExtractionSession
{
    private readonly ITabularCursor _cursor;
    private readonly MappingPlan _plan;
    private readonly ExtractionOptions _options;
    private readonly CancellationToken _cancellationToken;
    private readonly CultureInfo _culture;
    private readonly Mapped[] _mappings;
    private readonly MappedValue[] _values;
    private readonly bool[] _present;
    private readonly RequiredGroup[] _requiredGroups;
    private readonly List<RowError> _errors = [];

    private bool _finished;

    /// <summary>
    /// How many failing rows end the run: the options' tolerance, or one when the schema says a
    /// partial import is no import at all.
    /// </summary>
    private readonly int _errorLimit;

    internal ExtractionSession(
        ITabularCursor cursor,
        MappingPlan plan,
        TargetSchema schema,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        _cursor = cursor;
        _plan = plan;
        _options = options;
        _errorLimit = schema.Policy == ImportPolicy.AllOrNothing ? 1 : options.MaxErrorRows;
        _cancellationToken = cancellationToken;
        _culture = plan.Culture is { Length: > 0 } name
            ? CultureInfo.GetCultureInfo(name)
            : CultureInfo.InvariantCulture;

        TargetField[] fields = [.. schema.Fields];
        _values = new MappedValue[fields.Length];
        _present = new bool[fields.Length];

        // Resolved once, not per row. Which field a binding feeds cannot change during a run, and
        // looking it up again for every binding of every row would spend a dictionary probe per cell
        // on a file that may hold millions of them.
        SchemaFields.ByName(schema);

        Dictionary<string, int> positions = new(StringComparer.Ordinal);

        for (int i = 0; i < fields.Length; i++)
        {
            positions[fields[i].Name] = i;
        }

        List<Mapped> mappings = [];

        foreach (ColumnBinding binding in plan.Bindings)
        {
            if (positions.TryGetValue(binding.TargetFieldName, out int position))
            {
                mappings.Add(new Mapped(
                    binding,
                    position,
                    fields[position],
                    // A set rather than the list itself: membership is asked once per cell, and the
                    // spellings a source uses for "unknown" do not change during a run. Trimmed on the
                    // way in, because the text it is compared against arrives trimmed — an entry
                    // written with spaces would otherwise be configuration that can never match, with
                    // nothing to say so.
                    new HashSet<string>(binding.TreatAsEmpty.Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase)));
            }
        }

        _mappings = [.. mappings];

        // A group's rule is about the row, not about any column, so it is checked once the row is
        // read rather than while it is being read. Resolved here for the same reason the positions
        // are: what belongs to which group cannot change during a run.
        _requiredGroups =
        [
            .. fields
                .Select((field, position) => (field, position))
                .Where(f => f.field.Required && f.field.Group is not null)
                .GroupBy(f => f.field.Group!, StringComparer.Ordinal)
                .Select(g => new RequiredGroup(
                    g.Key,
                    [.. g.Select(f => f.position)],
                    // The column to point at when nothing was there to point at. The first mapped
                    // member is the least surprising: it is where the person looking expected the
                    // value to be.
                    mappings.FirstOrDefault(m => g.Any(f => f.position == m.Position)).Binding?.SourceColumnIndex ?? -1)),
        ];

        Position();
    }

    /// <summary>What the run amounted to. Complete once the rows have been read out.</summary>
    public ExtractionSummary Summary { get; } = new();

    /// <summary>The current row's number as the file counts it.</summary>
    public int CurrentRowNumber { get; private set; }

    /// <summary>Whether the current row failed.</summary>
    public bool CurrentRowHasErrors => _errors.Count > 0;

    /// <summary>
    /// The current row's values, one per schema field in schema order. Valid until the next
    /// <see cref="ReadRow"/>, and empty for a failing row.
    /// </summary>
    public ReadOnlySpan<MappedValue> CurrentValues =>
        CurrentRowHasErrors ? [] : _values.AsSpan();

    /// <summary>The current row's errors, empty for a row that produced values.</summary>
    public IReadOnlyList<RowError> CurrentErrors => _errors;

    /// <summary>
    /// Advances to the next row that produced either values or errors.
    /// </summary>
    /// <remarks>
    /// Rows whose mapped cells are all empty are neither, and are skipped rather than reported: a
    /// spreadsheet accumulates such rows below its data as a matter of course, and calling them
    /// failures would bury the real ones.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Stops this read. The token the session was started with stops it too.
    /// </param>
    public bool ReadRow(CancellationToken cancellationToken = default)
    {
        if (_finished)
        {
            return false;
        }

        // The cursor takes one token; the call's, when it has one. The session's is checked below.
        CancellationToken read = cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken;

        while (_cursor.ReadRow(read))
        {
            // Checked per row rather than per file: a run over five million rows that cannot be
            // stopped is a run that holds a request open long after anyone stopped waiting for it.
            _cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();

            Summary.RowsRead++;
            CurrentRowNumber = _cursor.CurrentRowNumber;

            ReadOnlySpan<RawCell> row = _cursor.CurrentRow;

            if (IsBlank(row))
            {
                Summary.RowsSkipped++;

                // Told apart, because they are different things. Padding below the data is expected
                // and uninteresting; a row holding a note in a column nobody mapped is a record this
                // run drops, and dropping records without a word is worse than a warning nobody
                // needed.
                if (!IsEntirelyBlank(row))
                {
                    Summary.RowsWithNothingMapped++;
                }

                continue;
            }

            Convert(row);

            if (CurrentRowHasErrors)
            {
                Summary.RowsFailed++;
                Summary.ErrorCount += _errors.Count;

                if (Summary.RowsFailed >= _errorLimit)
                {
                    Summary.StoppedEarly = true;
                    _finished = true;
                }

                return true;
            }

            Summary.RowsProduced++;
            return true;
        }

        _finished = true;
        return false;
    }

    /// <summary>
    /// Skips to the first data row, checking on the way that the file is still the one that was
    /// mapped.
    /// </summary>
    private void Position()
    {
        if (!_cursor.MoveToSheet(_plan.SheetIndex))
        {
            throw new TabularStructureException(
                TabularStructureException.SheetMissing,
                $"The file has no sheet at index {_plan.SheetIndex}.")
            {
                SheetIndex = _plan.SheetIndex,
            };
        }

        // Safe: MoveToSheet returned true, so the index exists.
        VerifySheet(_cursor.Sheets[_plan.SheetIndex]);

        // The header is the first row at or below the spreadsheet row the plan names — by the row's
        // own number, as the analyzer counts it, not by how many rows the file happens to write.
        do
        {
            if (!_cursor.ReadRow(_cancellationToken))
            {
                throw new TabularStructureException(
                    TabularStructureException.HeaderRowMissing,
                    $"The sheet ends before row {_plan.HeaderRowIndex + 1}, where the plan expects its headers.")
                {
                    SheetIndex = _plan.SheetIndex,
                };
            }
        }
        while (_cursor.CurrentRowNumber <= _plan.HeaderRowIndex);

        VerifyHeaders(_cursor.CurrentRow);
    }

    /// <summary>
    /// Checks the sheet at the plan's index against the one the plan recorded, where it recorded one.
    /// </summary>
    /// <remarks>
    /// Ordinal, as headers are compared: "Orders" and "orders" are different tabs in a workbook that
    /// has both, and guessing which was meant is what this check exists to stop.
    /// </remarks>
    private void VerifySheet(SheetInfo sheet)
    {
        bool nameChanged = _plan.SheetName is not null && !string.Equals(_plan.SheetName, sheet.Name, StringComparison.Ordinal);
        bool sourceChanged = _plan.SheetSource is not null && !string.Equals(_plan.SheetSource, sheet.Source, StringComparison.Ordinal);

        if (nameChanged || sourceChanged)
        {
            throw new TabularStructureException(
                TabularStructureException.SheetChanged,
                $"The sheet at index {_plan.SheetIndex} is not the one the plan was built for.")
            {
                SheetIndex = _plan.SheetIndex,
            };
        }
    }

    /// <summary>
    /// Checks each binding's recorded header against the one standing there now.
    /// </summary>
    /// <remarks>
    /// Analysis and extraction are separate reads of the file, with however long a user spends in a
    /// mapping screen between them. Without this check a file swapped in the meantime would be read
    /// against the wrong columns and reported as a success.
    /// </remarks>
    private void VerifyHeaders(ReadOnlySpan<RawCell> header)
    {
        foreach ((ColumnBinding binding, _, _, _) in _mappings)
        {
            string actual = binding.SourceColumnIndex < header.Length
                ? header[binding.SourceColumnIndex].AsText() ?? string.Empty
                : string.Empty;

            if (!string.Equals(actual, binding.SourceHeader, StringComparison.Ordinal))
            {
                throw new TabularStructureException(
                    TabularStructureException.HeaderChanged,
                    $"Column {binding.SourceColumnIndex} was mapped as '{binding.SourceHeader}' and now reads "
                    + $"'{actual}'. The file is not the one the mapping was built against.")
                {
                    SheetIndex = _plan.SheetIndex,
                    SourceColumnIndex = binding.SourceColumnIndex,
                    ExpectedHeader = binding.SourceHeader,
                    ActualHeader = actual,
                };
            }
        }
    }

    private bool IsBlank(ReadOnlySpan<RawCell> row)
    {
        // A plain loop rather than LINQ throughout this class: these run once per row, and a file
        // may hold millions, so an enumerator and a closure per row would be paid for on every one.
        for (int i = 0; i < _mappings.Length; i++)
        {
            int index = _mappings[i].Binding.SourceColumnIndex;

            if (index < row.Length && !row[index].IsEmpty)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the row holds nothing at all, in any column, mapped or not.</summary>
    private static bool IsEntirelyBlank(ReadOnlySpan<RawCell> row)
    {
        for (int i = 0; i < row.Length; i++)
        {
            if (!row[i].IsEmpty)
            {
                return false;
            }
        }

        return true;
    }

    private void Convert(ReadOnlySpan<RawCell> row)
    {
        _errors.Clear();
        Array.Clear(_values);
        Array.Clear(_present);

        for (int b = 0; b < _mappings.Length; b++)
        {
            (ColumnBinding binding, int position, TargetField field, HashSet<string> emptyEquivalents) = _mappings[b];

            RawCell cell = binding.SourceColumnIndex < row.Length
                ? row[binding.SourceColumnIndex]
                : RawCell.Empty;

            string? text = Normalise(cell, emptyEquivalents);

            if (text is null)
            {
                // A grouped field is answered for by its group, below: no single member is required,
                // and failing here would refuse a row for the language it happens not to be in.
                if (field.Required && field.Group is null)
                {
                    Fail(binding, ErrorCodes.Value.Required, null);
                }

                continue;
            }

            if (!ValueReading.TryRead(cell, text, field.Type, _culture, out MappedValue value))
            {
                Fail(binding, ErrorCodes.Value.TypeMismatch, text);
                continue;
            }

            for (int c = 0; c < field.Constraints.Count; c++)
            {
                Check(binding, field.Constraints[c], value, text);
            }

            _present[position] = true;

            if (!_options.ValidateOnly)
            {
                _values[position] = value;
            }
        }

        CheckRequiredGroups();
    }

    /// <summary>
    /// Fails a row that carries none of a required group's languages.
    /// </summary>
    /// <remarks>
    /// One error for the group rather than one per member: a row missing a title has one thing wrong
    /// with it, and reporting it once per declared language would bury that under noise proportional
    /// to how many languages the subscription happens to have.
    /// </remarks>
    private void CheckRequiredGroups()
    {
        for (int g = 0; g < _requiredGroups.Length; g++)
        {
            RequiredGroup group = _requiredGroups[g];
            bool any = false;

            for (int p = 0; p < group.Positions.Length && !any; p++)
            {
                any = _present[group.Positions[p]];
            }

            if (!any)
            {
                _errors.Add(new RowError
                {
                    RowNumber = CurrentRowNumber,
                    SourceColumnIndex = group.SourceColumnIndex,
                    TargetFieldName = group.Name,
                    Code = ErrorCodes.Group.Required,
                    RawValue = null,
                });
            }
        }
    }

    /// <summary>
    /// Applies the binding's own preparation and decides whether anything is left.
    /// </summary>
    /// <remarks>
    /// The empty-equivalents list is what makes a real export usable: a column of amounts holding
    /// <c>k.A.</c> for the ones nobody measured is not a column of broken numbers, it is a column
    /// with gaps, and only the caller knows which spelling its source uses for a gap.
    /// </remarks>
    private static string? Normalise(in RawCell cell, HashSet<string> emptyEquivalents)
    {
        string? text = cell.AsText();

        if (text is null)
        {
            return null;
        }

        // Not trimmed here: the cursor hands over trimmed text, so that what was profiled and what
        // is imported are the same string rather than two strings that usually agree.
        if (text.Length == 0)
        {
            return null;
        }

        return emptyEquivalents.Contains(text) ? null : text;
    }

    /// <summary>
    /// Holds one value to one rule, reporting the rule's own code when it does not hold.
    /// </summary>
    /// <remarks>
    /// Every failing rule is reported, not merely the first: a value that is both too long and not in
    /// the allowed set is wrong in two ways, and telling a user about one of them wastes an upload.
    /// </remarks>
    private void Check(ColumnBinding binding, FieldConstraint constraint, in MappedValue value, string text)
    {
        if (constraint.IsSatisfiedBy(value))
        {
            return;
        }

        Fail(binding, constraint.Code, text);
    }

    private void Fail(ColumnBinding binding, string code, string? raw) =>
        _errors.Add(new RowError
        {
            RowNumber = CurrentRowNumber,
            SourceColumnIndex = binding.SourceColumnIndex,
            TargetFieldName = binding.TargetFieldName,
            Code = code,
            RawValue = raw,
        });

    /// <summary>A group that needs one of its members, and where its positions sit in a row.</summary>
    private readonly record struct RequiredGroup(string Name, int[] Positions, int SourceColumnIndex);

    /// <summary>A binding resolved to the field it feeds.</summary>
    private readonly record struct Mapped(
        ColumnBinding Binding,
        int Position,
        TargetField Field,
        HashSet<string> EmptyEquivalents);
}
