namespace TriasDev.Tabular;

/// <summary>
/// How a cell looks in a workbook. Every setting is optional; one left unset keeps the format's
/// default. Csv ignores styles.
/// </summary>
/// <remarks>
/// Compares by value: two styles with the same settings are the same style, and a file holds it
/// once. Declare the styles an export uses once (<c>static readonly</c>) and register each with
/// a writer's Style method.
/// </remarks>
public sealed record CellStyle
{
    /// <summary>The cell's background colour; null leaves it unfilled.</summary>
    public CellColor? Fill { get; init; }

    /// <summary>The text's colour, bold and italic; null keeps the default font.</summary>
    public CellFont? Font { get; init; }

    /// <summary>How an integer, decimal or double cell shows its value; ignored for other cells. Null keeps the default.</summary>
    public NumberFormat? Number { get; init; }

    /// <summary>How a date or date-time cell shows its value; ignored for other cells. Null keeps the writer's default date format.</summary>
    public DateFormat? Date { get; init; }

    /// <summary>Where the content sits across the cell.</summary>
    public HorizontalAlignment Horizontal { get; init; }

    /// <summary>Wraps text onto several lines within the column's width.</summary>
    public bool Wrap { get; init; }

    /// <summary>A border on all four sides; null draws none.</summary>
    public CellBorder? Border { get; init; }
}
