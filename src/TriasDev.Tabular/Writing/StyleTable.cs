namespace TriasDev.Tabular;

/// <summary>What a cell holds, as far as its style is concerned: each kind may need its own format style.</summary>
internal enum ValueKind
{
    Text,
    Integer,
    Number,
    Date,
    DateTime,
    Boolean,
    Empty,
}

/// <summary>
/// The styles one writer has handed out, by index: 0 is the unstyled cell, 1 onwards the registered
/// styles. Each format resolves an index, per <see cref="ValueKind"/>, into its own style lazily.
/// </summary>
internal sealed class StyleTable
{
    /// <summary>The most distinct styles a file holds.</summary>
    public const int MaxStyles = 4096;

    /// <summary>The number of <see cref="ValueKind"/> values, for per-kind caches.</summary>
    public const int ValueKinds = 7;

    private readonly Dictionary<CellStyle, int> _indices = [];
    private readonly List<CellStyle> _styles = [new CellStyle()];

    /// <summary>The indices in use: the registered styles plus index 0.</summary>
    public int Count => _styles.Count;

    /// <summary>The style at an index (1 onwards; 0 is the empty style).</summary>
    public CellStyle this[int index] => _styles[index];

    /// <summary>Registers a style, or finds it registered already; returns its index.</summary>
    /// <exception cref="InvalidOperationException">The table already holds <see cref="MaxStyles"/> styles.</exception>
    public int Add(CellStyle style) =>
        TryAdd(style, out int index)
            ? index
            : throw new InvalidOperationException($"A file holds at most {MaxStyles} distinct styles; this would be the {MaxStyles + 1}th.");

    /// <summary>
    /// Registers a style, or finds it registered already; false when it is new and the table is full,
    /// so the caller decides what kind of mistake that is — a call in the code, or a rule fed by data.
    /// </summary>
    public bool TryAdd(CellStyle style, out int index)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (_indices.TryGetValue(style, out index))
        {
            return true;
        }

        if (!Enum.IsDefined(style.Horizontal))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Horizontal, "The horizontal alignment is not one CellHorizontalAlignment defines.");
        }

        if (_indices.Count == MaxStyles)
        {
            return false;
        }

        index = _styles.Count;
        _styles.Add(style);
        _indices.Add(style, index);
        return true;
    }
}
