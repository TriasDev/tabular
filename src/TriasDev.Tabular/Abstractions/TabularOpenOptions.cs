using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular;

/// <summary>How <see cref="TabularFile.Open"/> opens a file of whichever kind it turns out to be.</summary>
public sealed record TabularOpenOptions
{
    /// <summary>The defaults.</summary>
    public static TabularOpenOptions Default { get; } = new();

    /// <summary>Options for the cursor, should the file be csv.</summary>
    public CsvCursorOptions Csv { get; init; } = CsvCursorOptions.Default;

    /// <summary>Options for the cursor, should the file be a workbook.</summary>
    public XlsxCursorOptions Xlsx { get; init; } = XlsxCursorOptions.Default;

    /// <summary>Options for the cursor, should the file be an OpenDocument spreadsheet.</summary>
    public OdsCursorOptions Ods { get; init; } = OdsCursorOptions.Default;

    /// <summary>
    /// Whether the stream stays open once the cursor is disposed — or once opening it fails.
    /// </summary>
    /// <remarks>
    /// False by default: a stream handed over is the library's to close, on every path, failure
    /// included. True for a stream the caller still needs afterwards, such as an upload it archives.
    /// </remarks>
    public bool LeaveOpen { get; init; }
}
