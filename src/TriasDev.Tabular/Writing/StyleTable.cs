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
    public int Add(CellStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);

        if (_indices.TryGetValue(style, out int index))
        {
            return index;
        }

        if (!Enum.IsDefined(style.Horizontal))
        {
            throw new ArgumentOutOfRangeException(nameof(style), style.Horizontal, "The horizontal alignment is not one HorizontalAlignment defines.");
        }

        if (_indices.Count == MaxStyles)
        {
            throw new TabularLimitException("MaxStyles", MaxStyles, $"A file holds at most {MaxStyles} distinct styles.");
        }

        index = _styles.Count;
        _styles.Add(style);
        _indices.Add(style, index);
        return index;
    }
}
