namespace TriasDev.Tabular;

/// <summary>Whether the author of a workbook left a sheet showing.</summary>
/// <remarks>
/// Information, not a filter: a hidden sheet is still listed and read. Templates carry hidden helper
/// sheets — lookup lists for drop-downs, instructions, calculations — which a screen offering sheets
/// to pick from, or an upload picking one itself, should pass over.
/// </remarks>
public enum SheetVisibility
{
    /// <summary>Shown, as every csv and most sheets are.</summary>
    Visible = 0,

    /// <summary>Hidden, and one a user can show again from the workbook.</summary>
    Hidden = 1,

    /// <summary>Hidden so that only code can show it again: an xlsx <c>veryHidden</c> sheet.</summary>
    VeryHidden = 2,
}
