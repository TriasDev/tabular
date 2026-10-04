namespace TriasDev.Tabular;

/// <summary>
/// A batch of rows given by column — one list per column, all of the batch's row count — written by
/// <see cref="TabularWriter.WriteBatch"/> row by row, typed and without boxing. Reuse one batch for
/// every chunk: <see cref="Reset"/> keeps its column slots, so a batch allocates nothing once warm.
/// </summary>
/// <remarks>
/// <para>
/// Values: <c>string</c>, <c>long</c>, <c>int</c>, <c>short</c>, <c>double</c>, <c>decimal</c>,
/// <c>bool</c>, <c>DateTime</c>, <c>DateOnly</c>, and the nullable forms; a null is an empty cell.
/// Arrays, <see cref="List{T}"/> and any other <see cref="IReadOnlyList{T}"/> — a protobuf repeated
/// field among them — are read where they are, never copied; they must not change until the batch
/// is written.
/// </para>
/// <para>
/// A column's style is a constant id, a vector of ids by row, or a rule on the value. A rule should
/// return styles declared once (<c>static readonly</c>): the same instance costs a reference compare.
/// <c>Add(values, v =&gt; …)</c> takes the lambda as a style rule when it returns a <see cref="CellStyle"/>,
/// and as a value selector otherwise.
/// </para>
/// <para>Not thread-safe.</para>
/// </remarks>
public sealed class ColumnBatch
{
    private IBatchColumn[] _columns = new IBatchColumn[16];

    /// <summary>The rows each column holds.</summary>
    public int RowCount { get; private set; }

    /// <summary>The columns added since the last <see cref="Reset"/>.</summary>
    public int ColumnCount { get; private set; }

    /// <summary>Empties the batch for a chunk of <paramref name="rowCount"/> rows; its slots are kept for reuse.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowCount"/> is negative.</exception>
    public void Reset(int rowCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);

        for (int i = 0; i < ColumnCount; i++)
        {
            _columns[i].Clear();
        }

        RowCount = rowCount;
        ColumnCount = 0;
    }

    /// <summary>Adds the next column: its values, one per row.</summary>
    /// <exception cref="ArgumentException">A type that cannot be written, or not one value per row.</exception>
    public void Add<T>(IReadOnlyList<T> values) => AddValues(values, default);

    /// <summary>Adds the next column, every cell in one style.</summary>
    public void Add<T>(IReadOnlyList<T> values, StyleId style) => AddValues(values, new BatchStyle<T> { Constant = style });

    /// <summary>Adds the next column, each cell in the style at its row.</summary>
    public void Add<T>(IReadOnlyList<T> values, IReadOnlyList<StyleId> styles) => AddValues(values, new BatchStyle<T> { Vector = Checked(styles, nameof(styles)) });

    /// <summary>Adds the next column, each cell in the style the rule returns for its value; null for none.</summary>
    public void Add<T>(IReadOnlyList<T> values, Func<T, CellStyle?> style) => AddValues(values, new BatchStyle<T> { Rule = style ?? throw new ArgumentNullException(nameof(style)) });

    /// <summary>Adds the next column: a value selected from each item, one item per row.</summary>
    public void Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value) => AddSelected(items, value, default);

    /// <summary>Adds the next column of selected values, every cell in one style.</summary>
    public void Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value, StyleId style) => AddSelected(items, value, new BatchStyle<T> { Constant = style });

    /// <summary>Adds the next column of selected values, each cell in the style at its row.</summary>
    public void Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value, IReadOnlyList<StyleId> styles) => AddSelected(items, value, new BatchStyle<T> { Vector = Checked(styles, nameof(styles)) });

    /// <summary>Adds the next column of selected values, each cell in the style the rule returns; null for none.</summary>
    public void Add<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value, Func<T, CellStyle?> style) => AddSelected(items, value, new BatchStyle<T> { Rule = style ?? throw new ArgumentNullException(nameof(style)) });

    internal IBatchColumn Column(int index) => _columns[index];

    private void AddValues<T>(IReadOnlyList<T> values, BatchStyle<T> style)
    {
        Checked(values, nameof(values));
        ExpectWritable<T>(nameof(values));
        Next<ValuesColumn<T>>().Bind(values, style);
    }

    private void AddSelected<TItem, T>(IReadOnlyList<TItem> items, Func<TItem, T> value, BatchStyle<T> style)
    {
        ArgumentNullException.ThrowIfNull(value);
        Checked(items, nameof(items));
        ExpectWritable<T>(nameof(items));
        Next<SelectedColumn<TItem, T>>().Bind(items, value, style);
    }

    private void ExpectWritable<T>(string name)
    {
        if (!CellValue<T>.Supported)
        {
            throw new ArgumentException($"Column {ColumnCount + 1}: values of type {typeof(T).Name} cannot be written; a column holds {CellValue.SupportedTypes}.", name);
        }
    }

    private IReadOnlyList<TList> Checked<TList>(IReadOnlyList<TList> list, string name)
    {
        if (list is null)
        {
            throw new ArgumentNullException(name);
        }


        if (list.Count != RowCount)
        {
            throw new ArgumentException($"The batch's column {ColumnCount + 1} has {list.Count} entries in {name}; the batch has {RowCount} rows.", name);
        }

        return list;
    }

    private TColumn Next<TColumn>()
        where TColumn : class, IBatchColumn, new()
    {
        if (ColumnCount == _columns.Length)
        {
            Array.Resize(ref _columns, _columns.Length * 2);
        }

        if (_columns[ColumnCount] is not TColumn slot)
        {
            slot = new TColumn();
            _columns[ColumnCount] = slot;
        }

        ColumnCount++;
        return slot;
    }
}
