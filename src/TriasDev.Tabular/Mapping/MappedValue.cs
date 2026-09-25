using System.Globalization;

namespace TriasDev.Tabular;

/// <summary>
/// One value after it has been read as the target field's type.
/// </summary>
/// <remarks>
/// A struct, and typed rather than boxed, for the same reason
/// <see cref="RawCell"/> is: this sits on a path that runs to millions of rows, and an
/// object per value would spend the whole budget on the garbage collector.
/// </remarks>
public readonly struct MappedValue : IEquatable<MappedValue>
{
    /// <summary>
    /// Every reading's payload, held as a decimal.
    /// </summary>
    /// <remarks>
    /// One field for four readings, because a decimal represents all of them exactly: a whole number
    /// and a date's tick count are integers well inside its range, and a boolean is one or zero. The
    /// obvious alternative — packing them into a long and scaling the decimal — silently rounds money
    /// to four places, which is precisely the kind of loss nobody notices until an invoice is wrong.
    /// </remarks>
    private readonly decimal _number;

    private MappedValue(ColumnType type, bool present, string? text, decimal number)
    {
        Type = type;
        IsPresent = present;
        Text = text;
        _number = number;
    }

    /// <summary>The reading this value was given.</summary>
    public ColumnType Type { get; }

    /// <summary>False when the cell held nothing.</summary>
    public bool IsPresent { get; }

    /// <summary>
    /// The value as text. Present for every reading, since a length or a pattern applies whatever
    /// the type is.
    /// </summary>
    public string? Text { get; }

    /// <summary>Nothing.</summary>
    public static MappedValue Absent => default;

    /// <summary>A text value.</summary>
    public static MappedValue FromText(string text) => new(ColumnType.Text, true, text, 0);

    /// <summary>A whole number.</summary>
    public static MappedValue FromInteger(long value) =>
        new(ColumnType.Integer, true, value.ToString(CultureInfo.InvariantCulture), value);

    /// <summary>A number with a fractional part.</summary>
    public static MappedValue FromDecimal(decimal value) =>
        new(ColumnType.Decimal, true, value.ToString(CultureInfo.InvariantCulture), value);

    /// <summary>A date.</summary>
    public static MappedValue FromDate(DateTime value) =>
        new(ColumnType.Date, true, value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value.Ticks);

    /// <summary>A boolean.</summary>
    public static MappedValue FromBoolean(bool value) =>
        new(ColumnType.Boolean, true, value ? "true" : "false", value ? 1 : 0);

    /// <summary>The whole number this value holds.</summary>
    public long Integer => (long)_number;

    /// <summary>The number this value holds, whatever its reading.</summary>
    public decimal Number => Type is ColumnType.Integer or ColumnType.Decimal ? _number : 0;

    /// <summary>The date this value holds.</summary>
    public DateTime Date => new((long)_number, DateTimeKind.Unspecified);

    /// <summary>The boolean this value holds.</summary>
    public bool Boolean => _number != 0;

    /// <inheritdoc />
    public bool Equals(MappedValue other) =>
        Type == other.Type
        && IsPresent == other.IsPresent
        && _number == other._number
        && string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MappedValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Type, IsPresent, Text, _number);

    /// <inheritdoc />
    public override string ToString() => IsPresent ? Text ?? string.Empty : "(absent)";

    public static bool operator ==(MappedValue left, MappedValue right) => left.Equals(right);

    public static bool operator !=(MappedValue left, MappedValue right) => !left.Equals(right);
}
