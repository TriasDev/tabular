namespace TriasDev.Tabular;

/// <summary>One column of a batch: writes its value for a row as the writer's next cell.</summary>
internal interface IBatchColumn
{
    void Write(TabularWriter writer, int row);

    /// <summary>Drops the caller's lists, so a reset batch keeps nothing alive.</summary>
    void Clear();
}

/// <summary>Where a column's style comes from: a constant, a vector by row, or a rule on the value.</summary>
internal struct BatchStyle<T>
{
    public StyleId Constant;
    public IReadOnlyList<StyleId>? Vector;
    public Func<T, CellStyle?>? Rule;

    public readonly StyleId For(TabularWriter writer, T value, int row)
    {
        if (Rule is not null)
        {
            return writer.StyleFor(Rule(value));
        }

        return Vector is not null ? Vector[row] : Constant;
    }
}

/// <summary>A column over a list of values; arrays and lists are indexed directly, other lists through the interface.</summary>
internal sealed class ValuesColumn<T> : IBatchColumn
{
    private T[]? _array;
    private List<T>? _list;
    private IReadOnlyList<T>? _values;
    private BatchStyle<T> _style;

    public void Bind(IReadOnlyList<T> values, BatchStyle<T> style)
    {
        _array = values as T[];
        _list = _array is null ? values as List<T> : null;
        _values = values;
        _style = style;
    }

    public void Write(TabularWriter writer, int row)
    {
        T value = _array is not null ? _array[row] : Item(row);
        CellValue<T>.Write(writer, value, _style.For(writer, value, row));
    }

    public void Clear()
    {
        _array = null;
        _list = null;
        _values = null;
        _style = default;
    }

    private T Item(int row) => _list is not null ? _list[row] : _values![row];
}

/// <summary>A column over items and a selector for the value: one delegate call per cell.</summary>
internal sealed class SelectedColumn<TItem, T> : IBatchColumn
{
    private IReadOnlyList<TItem>? _items;
    private Func<TItem, T>? _value;
    private BatchStyle<T> _style;

    public void Bind(IReadOnlyList<TItem> items, Func<TItem, T> value, BatchStyle<T> style)
    {
        _items = items;
        _value = value;
        _style = style;
    }

    public void Write(TabularWriter writer, int row)
    {
        T value = _value!(_items![row]);
        CellValue<T>.Write(writer, value, _style.For(writer, value, row));
    }

    public void Clear()
    {
        _items = null;
        _value = null;
        _style = default;
    }
}
