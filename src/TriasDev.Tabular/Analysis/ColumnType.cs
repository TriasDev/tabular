namespace TriasDev.Tabular;

/// <summary>A reading a column's values can be given.</summary>
public enum ColumnType
{
    /// <summary>Text. Always possible, which is why it is always the fallback and never the answer.</summary>
    Text,

    /// <summary>Whole numbers.</summary>
    Integer,

    /// <summary>Numbers with a fractional part.</summary>
    Decimal,

    /// <summary>Dates.</summary>
    Date,

    /// <summary>True and false.</summary>
    Boolean,
}
