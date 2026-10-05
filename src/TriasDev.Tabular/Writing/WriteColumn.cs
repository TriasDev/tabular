namespace TriasDev.Tabular;

/// <summary>A column a writer declares when it begins a sheet: its header, and how wide to show it.</summary>
/// <param name="Header">The header text, written as the sheet's first row. Not empty, without leading or trailing whitespace, and unique in its sheet.</param>
/// <param name="Width">
/// The width in characters, for workbook formats; csv has none. A column is never narrower than its
/// header: the width written is the larger of this one and the header's, measured generously — by its
/// longest line, or its longest word when the header style wraps, with room for the filter button when
/// the sheet has one — and at most 255. Null gives the header's width. The data is not measured, since
/// a streaming writer cannot see it in advance: a value wider than both still needs a width declared.
/// </param>
public readonly record struct WriteColumn(string Header, double? Width = null);
