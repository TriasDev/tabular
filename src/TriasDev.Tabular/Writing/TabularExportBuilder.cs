namespace TriasDev.Tabular;

/// <summary>
/// Declares an export's columns, in order: a header and a lambda each, the column's type taken from
/// the lambda's.
/// </summary>
/// <remarks>
/// <para>
/// Overload resolution picks the column type: an <see cref="int"/> property resolves to
/// <see cref="long"/>, <c>int?</c> to <c>long?</c>, <see cref="float"/> to <see cref="double"/>.
/// Convert explicitly where it does not: a <see cref="char"/> would resolve to <see cref="long"/> and
/// write its code point; a <see cref="ulong"/> is ambiguous; a method group returning
/// <see cref="int"/> does not convert; <c>p =&gt; null</c> and <c>p =&gt; default</c> are ambiguous; an
/// <see cref="int"/> or <see cref="long"/> passed with a <see cref="DecimalImportField"/> is ambiguous
/// between decimal and double. <see cref="TimeSpan"/>, <see cref="DateTimeOffset"/>, <see cref="Guid"/>
/// and enumerations have no overload and fail with a misleading "cannot convert to string" (CS0029):
/// convert explicitly — <c>p =&gt; p.Status.ToString()</c>, <c>p =&gt; p.At.UtcDateTime</c>.
/// </para>
/// <para>
/// A column named by an import field takes the field's name as its header, so a file written with
/// the export maps back onto the import's schema by header; the lambda's type must suit the field's,
/// or the call does not compile.
/// </para>
/// </remarks>
public sealed class TabularExportBuilder<T>
{
    /// <summary>The width a date-time column gets unless told otherwise: <c>yyyy-mm-dd hh:mm:ss</c>.</summary>
    private const double DateTimeWidth = 19;

    /// <summary>The width a date column gets unless told otherwise: <c>yyyy-mm-dd</c>.</summary>
    private const double DateWidth = 10;

    private readonly List<ExportColumn<T>> _columns = [];

    internal TabularExportBuilder()
    {
    }

    /// <summary>A text column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, string?> value, double? width = null, Func<string?, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>An integer column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, long> value, double? width = null, Func<long, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>An integer column; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, long?> value, double? width = null, Func<long?, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>A decimal column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, decimal> value, double? width = null, Func<decimal, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>A decimal column; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, decimal?> value, double? width = null, Func<decimal?, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>A number column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, double> value, double? width = null, Func<double, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>A number column; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, double?> value, double? width = null, Func<double?, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>A date-time column, 19 characters wide unless told otherwise.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, DateTime> value, double? width = null, Func<DateTime, CellStyle?>? style = null) => Add(header, width ?? DateTimeWidth, value, style);

    /// <summary>A date-time column, 19 characters wide unless told otherwise; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, DateTime?> value, double? width = null, Func<DateTime?, CellStyle?>? style = null) => Add(header, width ?? DateTimeWidth, value, style);

    /// <summary>A date column, 10 characters wide unless told otherwise.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, DateOnly> value, double? width = null, Func<DateOnly, CellStyle?>? style = null) => Add(header, width ?? DateWidth, value, style);

    /// <summary>A date column, 10 characters wide unless told otherwise; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, DateOnly?> value, double? width = null, Func<DateOnly?, CellStyle?>? style = null) => Add(header, width ?? DateWidth, value, style);

    /// <summary>A boolean column.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, bool> value, double? width = null, Func<bool, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>A boolean column; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(string header, Func<T, bool?> value, double? width = null, Func<bool?, CellStyle?>? style = null) => Add(header, width, value, style);

    /// <summary>A text column named by an import field.</summary>
    public TabularExportBuilder<T> Column(TextImportField field, Func<T, string?> value, double? width = null, Func<string?, CellStyle?>? style = null) => Column(HeaderOf(field), value, width, style);

    /// <summary>An integer column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(IntegerImportField field, Func<T, long?> value, double? width = null, Func<long?, CellStyle?>? style = null) => Column(HeaderOf(field), value, width, style);

    /// <summary>A decimal column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(DecimalImportField field, Func<T, decimal?> value, double? width = null, Func<decimal?, CellStyle?>? style = null) => Column(HeaderOf(field), value, width, style);

    /// <summary>A number column named by a decimal import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(DecimalImportField field, Func<T, double?> value, double? width = null, Func<double?, CellStyle?>? style = null) => Column(HeaderOf(field), value, width, style);

    /// <summary>A date-time column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(DateImportField field, Func<T, DateTime?> value, double? width = null, Func<DateTime?, CellStyle?>? style = null) => Column(HeaderOf(field), value, width, style);

    /// <summary>A date column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(DateImportField field, Func<T, DateOnly?> value, double? width = null, Func<DateOnly?, CellStyle?>? style = null) => Column(HeaderOf(field), value, width, style);

    /// <summary>A boolean column named by an import field; null writes an empty cell.</summary>
    public TabularExportBuilder<T> Column(BooleanImportField field, Func<T, bool?> value, double? width = null, Func<bool?, CellStyle?>? style = null) => Column(HeaderOf(field), value, width, style);

    /// <summary>
    /// The export as declared so far, checked by the writer's rules for columns. The builder may go on
    /// being changed; the export it returned does not change with it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No column was declared.</exception>
    /// <exception cref="ArgumentException">A header is empty, padded, repeated ignoring case or not writable, or a width is out of range.</exception>
    public TabularExport<T> Build()
    {
        if (_columns.Count == 0)
        {
            throw new InvalidOperationException("An export has at least one column.");
        }

        ExportColumn<T>[] columns = [.. _columns];
        WriteColumn[] declared = [.. columns.Select(column => column.Column)];

        if (TabularWriter.ColumnsProblem(declared) is { } problem)
        {
            throw problem;
        }

        return new TabularExport<T>(columns, declared);
    }

    private static string HeaderOf(ImportField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.Variant is not null)
        {
            throw new ArgumentException(
                $"\"{field.Name}\" is one language of a translated field; name the column with a header of its own.",
                nameof(field));
        }

        return field.Name;
    }

    private TabularExportBuilder<T> Add<TValue>(string header, double? width, Func<T, TValue> value, Func<TValue, CellStyle?>? style)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(value);
        _columns.Add(new ExportColumn<T, TValue>(new WriteColumn(header, width), value, style));
        return this;
    }
}
