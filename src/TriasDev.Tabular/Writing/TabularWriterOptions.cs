using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular;

/// <summary>Knobs for writing a file, one set per format, as <see cref="TabularOpenOptions"/> has for reading.</summary>
public sealed record TabularWriterOptions
{
    /// <summary>The defaults: invariant csv with a byte order mark, and the target closed when the writer is.</summary>
    public static TabularWriterOptions Default { get; } = new();

    /// <summary>How a csv file is written.</summary>
    public CsvWriterOptions Csv { get; init; } = CsvWriterOptions.Default;

    /// <summary>How an xlsx workbook is written.</summary>
    public XlsxWriterOptions Xlsx { get; init; } = XlsxWriterOptions.Default;

    /// <summary>Leaves the target stream open when the writer is disposed, or fails to be created.</summary>
    public bool LeaveOpen { get; init; }
}
