using System.Runtime.CompilerServices;

namespace TriasDev.Tabular;

/// <summary>The value types a column of a batch or an export can have.</summary>
internal static class CellValue
{
    public const string SupportedTypes = "string, long, int, short, double, decimal, bool, DateTime, DateOnly and their nullable forms";
}

/// <summary>
/// Writes a value of a known type into the writer's next cell through its typed <c>Write</c>: no
/// boxing, and for value types the type tests fold away when the JIT specialises the method.
/// </summary>
internal static class CellValue<T>
{
    /// <summary>Whether values of <typeparamref name="T"/> can be written.</summary>
    public static readonly bool Supported =
        typeof(T) == typeof(string)
        || typeof(T) == typeof(long) || typeof(T) == typeof(long?)
        || typeof(T) == typeof(int) || typeof(T) == typeof(int?)
        || typeof(T) == typeof(short) || typeof(T) == typeof(short?)
        || typeof(T) == typeof(double) || typeof(T) == typeof(double?)
        || typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?)
        || typeof(T) == typeof(bool) || typeof(T) == typeof(bool?)
        || typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?)
        || typeof(T) == typeof(DateOnly) || typeof(T) == typeof(DateOnly?);

    /// <summary>Writes the value in a style; null (a null string or an empty nullable) is an empty cell in that style.</summary>
    public static void Write(TabularWriter writer, T value, StyleId style)
    {
        if (typeof(T) == typeof(double))
        {
            writer.Write(Unsafe.As<T, double>(ref value), style);
        }
        else if (typeof(T) == typeof(long))
        {
            writer.Write(Unsafe.As<T, long>(ref value), style);
        }
        else if (typeof(T) == typeof(string))
        {
            writer.Write(Unsafe.As<T, string?>(ref value), style);
        }
        else
        {
            WriteOther(writer, value, style);
        }
    }

    private static void WriteOther(TabularWriter writer, T value, StyleId style)
    {
        if (typeof(T) == typeof(int))
        {
            writer.Write((long)Unsafe.As<T, int>(ref value), style);
        }
        else if (typeof(T) == typeof(short))
        {
            writer.Write((long)Unsafe.As<T, short>(ref value), style);
        }
        else if (typeof(T) == typeof(decimal))
        {
            writer.Write(Unsafe.As<T, decimal>(ref value), style);
        }
        else if (typeof(T) == typeof(bool))
        {
            writer.Write(Unsafe.As<T, bool>(ref value), style);
        }
        else if (typeof(T) == typeof(DateTime))
        {
            writer.Write(Unsafe.As<T, DateTime>(ref value), style);
        }
        else if (typeof(T) == typeof(DateOnly))
        {
            writer.Write(Unsafe.As<T, DateOnly>(ref value), style);
        }
        else
        {
            WriteNullable(writer, value, style);
        }
    }

    private static void WriteNullable(TabularWriter writer, T value, StyleId style)
    {
        if (typeof(T) == typeof(long?))
        {
            long? v = Unsafe.As<T, long?>(ref value);
            if (v.HasValue)
            { writer.Write(v.GetValueOrDefault(), style); }
            else
            { writer.WriteEmpty(style); }
        }
        else if (typeof(T) == typeof(int?))
        {
            int? v = Unsafe.As<T, int?>(ref value);
            if (v.HasValue)
            { writer.Write((long)v.GetValueOrDefault(), style); }
            else
            { writer.WriteEmpty(style); }
        }
        else if (typeof(T) == typeof(short?))
        {
            short? v = Unsafe.As<T, short?>(ref value);
            if (v.HasValue)
            { writer.Write((long)v.GetValueOrDefault(), style); }
            else
            { writer.WriteEmpty(style); }
        }
        else
        {
            WriteNullableOther(writer, value, style);
        }
    }

    private static void WriteNullableOther(TabularWriter writer, T value, StyleId style)
    {
        if (typeof(T) == typeof(double?))
        {
            double? v = Unsafe.As<T, double?>(ref value);
            if (v.HasValue)
            { writer.Write(v.GetValueOrDefault(), style); }
            else
            { writer.WriteEmpty(style); }
        }
        else if (typeof(T) == typeof(decimal?))
        {
            decimal? v = Unsafe.As<T, decimal?>(ref value);
            if (v.HasValue)
            { writer.Write(v.GetValueOrDefault(), style); }
            else
            { writer.WriteEmpty(style); }
        }
        else if (typeof(T) == typeof(bool?))
        {
            bool? v = Unsafe.As<T, bool?>(ref value);
            if (v.HasValue)
            { writer.Write(v.GetValueOrDefault(), style); }
            else
            { writer.WriteEmpty(style); }
        }
        else
        {
            WriteNullableDate(writer, value, style);
        }
    }

    private static void WriteNullableDate(TabularWriter writer, T value, StyleId style)
    {
        if (typeof(T) == typeof(DateTime?))
        {
            DateTime? v = Unsafe.As<T, DateTime?>(ref value);
            if (v.HasValue)
            { writer.Write(v.GetValueOrDefault(), style); }
            else
            { writer.WriteEmpty(style); }
        }
        else if (typeof(T) == typeof(DateOnly?))
        {
            DateOnly? v = Unsafe.As<T, DateOnly?>(ref value);
            if (v.HasValue)
            { writer.Write(v.GetValueOrDefault(), style); }
            else
            { writer.WriteEmpty(style); }
        }
        else
        {
            throw new InvalidOperationException($"Values of type {typeof(T).Name} cannot be written; supported: {CellValue.SupportedTypes}.");
        }
    }
}
