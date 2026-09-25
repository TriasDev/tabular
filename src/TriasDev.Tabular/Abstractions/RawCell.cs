using System.Globalization;

namespace TriasDev.Tabular.Abstractions;

/// <summary>
/// One cell as the file presents it.
/// </summary>
/// <remarks>
/// <para>
/// A struct, and a deliberately small one. The prior art this library replaces stored every cell as
/// <c>object</c>, which boxes on a path that runs to eighty million cells on the largest file
/// measured. Here a cell is a reference and eight bytes.
/// </para>
/// <para>
/// The eight bytes are shared: a number keeps its bit pattern, a date keeps its ticks, a boolean
/// keeps zero or one. Only <see cref="Kind"/> says which reading is the valid one, so the accessors
/// are the only sanctioned way in.
/// </para>
/// </remarks>
public readonly struct RawCell : IEquatable<RawCell>
{
    private readonly string? _text;
    private readonly long _bits;

    private RawCell(RawCellKind kind, string? text, long bits)
    {
        Kind = kind;
        _text = text;
        _bits = bits;
    }

    /// <summary>What the file says this cell is.</summary>
    public RawCellKind Kind { get; }

    /// <summary>A cell holding nothing.</summary>
    public static RawCell Empty => default;

    /// <summary>
    /// A cell holding text, trimmed. Null, empty and whitespace-only all become <see cref="Empty"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trimming here rather than in a binding, and unconditionally, so that everything downstream
    /// measures the same text. It used to be a per-binding option applied only while extracting, so
    /// the profile measured <c>" DE "</c> as three characters and the import saw two: a length rule
    /// was judged before the file was read against values the read would never produce, and the
    /// precheck refused files that imported without a single error.
    /// </para>
    /// <para>
    /// Nothing is lost that anyone wanted. Leading and trailing whitespace in a spreadsheet cell is
    /// an artefact of how it was typed, not a value — and the cost of keeping the option was a
    /// permanent disagreement between the two halves of the library.
    /// </para>
    /// </remarks>
    public static RawCell FromText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Empty : new RawCell(RawCellKind.Text, text.Trim(), 0);

    /// <summary>A cell holding a number.</summary>
    public static RawCell FromNumber(double value) =>
        new(RawCellKind.Number, null, BitConverter.DoubleToInt64Bits(value));

    /// <summary>A cell holding a date.</summary>
    public static RawCell FromDate(DateTime value) =>
        new(RawCellKind.Date, null, value.Ticks);

    /// <summary>A cell holding a boolean.</summary>
    public static RawCell FromBoolean(bool value) =>
        new(RawCellKind.Boolean, null, value ? 1 : 0);

    /// <summary>A cell holding an error value, carried as the text the file holds.</summary>
    public static RawCell FromError(string text) =>
        string.IsNullOrWhiteSpace(text) ? Empty : new RawCell(RawCellKind.Error, text.Trim(), 0);

    /// <summary>True when this cell carries no value.</summary>
    public bool IsEmpty => Kind == RawCellKind.Empty;

    /// <summary>The text of a <see cref="RawCellKind.Text"/> or <see cref="RawCellKind.Error"/> cell.</summary>
    public string? Text => _text;

    /// <summary>The value of a <see cref="RawCellKind.Number"/> cell.</summary>
    public double Number => BitConverter.Int64BitsToDouble(_bits);

    /// <summary>The value of a <see cref="RawCellKind.Date"/> cell.</summary>
    /// <remarks>
    /// Deliberately <see cref="DateTimeKind.Unspecified"/>. A spreadsheet date is wall-clock: the
    /// file records 31 December, not an instant, and nothing in it says in which zone. Stamping it
    /// as local or UTC would invent information the file never had, and would move the date by a day
    /// for half the world.
    /// </remarks>
    public DateTime Date => new(_bits, DateTimeKind.Unspecified);

    /// <summary>The value of a <see cref="RawCellKind.Boolean"/> cell.</summary>
    public bool Boolean => _bits != 0;

    /// <summary>
    /// The cell rendered as text, in a form that does not depend on the current culture.
    /// </summary>
    /// <remarks>
    /// Used where a value has to be compared or measured as text — a length, a distinct count, a
    /// pattern. It is not the value the extractor hands to a caller, which keeps its type.
    /// </remarks>
    public string? AsText() =>
        Kind switch
        {
            RawCellKind.Empty => null,
            RawCellKind.Text or RawCellKind.Error => _text,
            RawCellKind.Number => Number.ToString("R", CultureInfo.InvariantCulture),
            RawCellKind.Date => Date.TimeOfDay == TimeSpan.Zero
                ? Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : Date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            RawCellKind.Boolean => Boolean ? "true" : "false",
            _ => null,
        };

    public bool Equals(RawCell other) =>
        Kind == other.Kind && _bits == other._bits && string.Equals(_text, other._text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is RawCell other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, _text, _bits);

    public override string ToString() => AsText() ?? "(empty)";

    public static bool operator ==(RawCell left, RawCell right) => left.Equals(right);

    public static bool operator !=(RawCell left, RawCell right) => !left.Equals(right);
}
