using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Xlsx;

namespace TriasDev.Tabular.Archive;

/// <summary>A workbook copied out of another file — a zip entry, a gzip file — to be read with random access.</summary>
internal static class EmbeddedWorkbook
{
    /// <summary>Opens the workbook's own cursor over the copy, with the options it has on its own.</summary>
    public static ITabularCursor Open(TabularFormat format, Stream content, TabularOpenOptions options, bool leaveOpen, CancellationToken cancellationToken) =>
        format == TabularFormat.Ods
            ? new OdsCursor(content, options.Ods, leaveOpen, cancellationToken)
            : new XlsxCursor(content, options.Xlsx, leaveOpen, cancellationToken);
}
