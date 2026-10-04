namespace TriasDev.Tabular;

/// <summary>A column a writer declares when it begins a sheet: its header, and how wide to show it.</summary>
/// <param name="Header">The header text, written as the sheet's first row. Not empty, without leading or trailing whitespace, and unique in its sheet.</param>
/// <param name="Width">
/// The width in characters, for workbook formats; csv has none. Null leaves it to the program that
/// opens the file — which shows a date-time in a default-width column as <c>#####</c>.
/// </param>
public readonly record struct WriteColumn(string Header, double? Width = null);
