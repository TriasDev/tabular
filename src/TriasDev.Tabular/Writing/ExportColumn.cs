namespace TriasDev.Tabular;

/// <summary>One column of an export: its header and width, and how to write an item's value.</summary>
internal abstract class ExportColumn<T>(WriteColumn column)
{
    public WriteColumn Column { get; } = column;

    /// <summary>Writes the item's value for this column as the writer's next cell.</summary>
    public abstract void Write(TabularWriter writer, T item);
}

/// <summary>
/// A column of a known type: the caller's lambda for the value, and a static delegate that writes a
/// value of that type — two delegate calls per cell, no boxing.
/// </summary>
internal sealed class ExportColumn<T, TValue>(WriteColumn column, Func<T, TValue> value, Action<TabularWriter, TValue> write)
    : ExportColumn<T>(column)
{
    public override void Write(TabularWriter writer, T item) => write(writer, value(item));
}

/// <summary>The typed writes, one per value type a column can have; a missing value is an empty cell.</summary>
internal static class CellWriters
{
    public static readonly Action<TabularWriter, string?> Text = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, long> Long = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, long?> NullableLong = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, decimal> Decimal = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, decimal?> NullableDecimal = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, double> Double = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, double?> NullableDouble = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, DateTime> DateTime = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, DateTime?> NullableDateTime = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, DateOnly> DateOnly = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, DateOnly?> NullableDateOnly = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };

    public static readonly Action<TabularWriter, bool> Boolean = static (writer, value) => writer.Write(value);

    public static readonly Action<TabularWriter, bool?> NullableBoolean = static (writer, value) =>
    {
        if (value is { } present)
        {
            writer.Write(present);
        }
        else
        {
            writer.WriteEmpty();
        }
    };
}
