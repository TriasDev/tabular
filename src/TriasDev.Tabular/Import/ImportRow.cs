
namespace TriasDev.Tabular;

/// <summary>
/// One validated row, read by the field declarations that built the schema.
/// </summary>
/// <remarks>
/// <para>
/// A view over memory the run reuses, so it cannot outlive the row it describes — which is the point
/// of it being a <c>ref struct</c>. A mapper that squirrelled one away would find it holding some
/// later row's values, and the compiler refuses that outright rather than leaving it to be
/// discovered.
/// </para>
/// <para>
/// Values are addressed by the field, not by its position. A caller that indexed by position would
/// have to know the order the schema declares, and would map a city into a country the day someone
/// reorders it.
/// </para>
/// </remarks>
public readonly ref struct ImportRow
{
    private readonly ReadOnlySpan<MappedValue> _values;
    private readonly ReadOnlySpan<AlternativeResolution> _resolutions;
    private readonly FieldIndex _index;

    internal ImportRow(ReadOnlySpan<MappedValue> values, ReadOnlySpan<AlternativeResolution> resolutions, FieldIndex index, int rowNumber)
    {
        _values = values;
        _resolutions = resolutions;
        _index = index;
        RowNumber = rowNumber;
    }

    /// <summary>The row's number as a person reading the file in a spreadsheet would count it.</summary>
    public int RowNumber { get; }

    /// <summary>The text of a text field, or null when the row leaves it empty.</summary>
    public string? this[TextImportField field] => Value(field).Text;

    /// <summary>The whole number of an integer field, or null when the row leaves it empty.</summary>
    public long? this[IntegerImportField field]
    {
        get
        {
            MappedValue value = Value(field);
            return value.IsPresent ? value.Integer : null;
        }
    }

    /// <summary>The number of a decimal field, or null when the row leaves it empty.</summary>
    public decimal? this[DecimalImportField field]
    {
        get
        {
            MappedValue value = Value(field);
            return value.IsPresent ? value.Number : null;
        }
    }

    /// <summary>The date of a date field, or null when the row leaves it empty.</summary>
    public DateTime? this[DateImportField field]
    {
        get
        {
            MappedValue value = Value(field);
            return value.IsPresent ? value.Date : null;
        }
    }

    /// <summary>The value of a boolean field, or null when the row leaves it empty.</summary>
    public bool? this[BooleanImportField field]
    {
        get
        {
            MappedValue value = Value(field);
            return value.IsPresent ? value.Boolean : null;
        }
    }

    /// <summary>
    /// Any field's value rendered as text, whatever its type.
    /// </summary>
    /// <remarks>
    /// For showing a row rather than building from one — a preview, a log line, a report. Culture
    /// plays no part: the rendering is the invariant one, so what is written here does not change
    /// with the machine reading it.
    /// </remarks>
    public string? AsText(ImportField field) => Value(field).Text;

    /// <summary>Whether the row carries anything for this field.</summary>
    public bool Has(ImportField field) => Value(field).IsPresent;

    /// <summary>
    /// Every language this row carries for one translated field, keyed by variant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A variant the row left empty is absent from the dictionary rather than present and empty. The
    /// two mean different things to whatever stores translations — one is "not translated", the
    /// other is "translated to nothing" — and only the first is true here.
    /// </para>
    /// <para>
    /// Allocates a dictionary per row, unlike everything else on this type. Deliberate: a dictionary
    /// is the shape the caller needs, and a view over the reused buffer would change under them the
    /// moment the row advances.
    /// </para>
    /// </remarks>
    public Dictionary<string, string> Translations(TranslatedImportField group)
    {
        ArgumentNullException.ThrowIfNull(group);

        (string Variant, int Position)[] positions = _index.PositionsOf(group);
        Dictionary<string, string> values = new(positions.Length, StringComparer.OrdinalIgnoreCase);

        foreach ((string variant, int position) in positions)
        {
            MappedValue value = _values[position];

            if (value.IsPresent && value.Text is { Length: > 0 } text)
            {
                values[variant] = text;
            }
        }

        return values;
    }

    /// <summary>Which group of a set of alternatives locates this row, and how far it reaches.</summary>
    /// <remarks>Information only: every value of the row is there whichever group won.</remarks>
    /// <exception cref="ArgumentException">The schema declares no such set.</exception>
    public AlternativeResolution Resolution(FieldAlternatives alternatives)
    {
        ArgumentNullException.ThrowIfNull(alternatives);
        return _resolutions[_index.SetOf(alternatives)];
    }

    private MappedValue Value(ImportField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        int position = _index.PositionOf(field);

        return position < _values.Length ? _values[position] : MappedValue.Absent;
    }
}
