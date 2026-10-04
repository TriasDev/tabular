namespace TriasDev.Tabular;

/// <summary>One column of an export: its header and width, and how to write an item's value.</summary>
internal abstract class ExportColumn<T>(WriteColumn column)
{
    public WriteColumn Column { get; } = column;

    /// <summary>Writes the item's value for this column as the writer's next cell.</summary>
    public abstract void Write(TabularWriter writer, T item);
}

/// <summary>
/// A column of a known type: the caller's lambda for the value, an optional rule for its style, and the
/// typed write of <see cref="CellValue{T}"/> — no boxing. The style cache lives in the writer, so the
/// column holds no state and one export serves concurrent writers.
/// </summary>
internal sealed class ExportColumn<T, TValue>(WriteColumn column, Func<T, TValue> value, Func<TValue, CellStyle?>? style)
    : ExportColumn<T>(column)
{
    public override void Write(TabularWriter writer, T item)
    {
        TValue cell = value(item);
        CellValue<TValue>.Write(writer, cell, style is null ? default : writer.StyleFor(style(cell)));
    }
}
