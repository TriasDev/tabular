using System.Diagnostics.CodeAnalysis;

namespace TriasDev.Tabular;

/// <summary>Where an export of objects is declared: <c>TabularExport.For&lt;Portfolio&gt;().Column(…).Build()</c>.</summary>
public static class TabularExport
{
    /// <summary>Begins declaring an export of <typeparamref name="T"/>.</summary>
    public static TabularExportBuilder<T> For<T>() => new();
}

/// <summary>
/// An export of objects to a table: built once, kept in a static field, used by any number of
/// writes at once.
/// </summary>
/// <remarks>Immutable and thread-safe: it holds the declared columns and nothing a write changes.</remarks>
public sealed class TabularExport<T>
{
    private readonly ExportColumn<T>[] _columns;
    private readonly WriteColumn[] _declared;

    internal TabularExport(ExportColumn<T>[] columns, WriteColumn[] declared)
    {
        _columns = columns;
        _declared = declared;
    }

    /// <summary>The columns, in order, as a sheet's header row writes them.</summary>
    public IReadOnlyList<WriteColumn> Columns => _declared;

    [SuppressMessage("Major Code Smell", "S1144", Justification = "Used by the write methods added next.")]
    private void WriteRow(TabularWriter writer, T item)
    {
        writer.BeginRow();

        foreach (ExportColumn<T> column in _columns)
        {
            column.Write(writer, item);
        }

        writer.EndRow();
    }
}
