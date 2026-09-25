using TriasDev.Tabular.Extraction;

namespace TriasDev.Tabular.Import;

/// <summary>
/// A batch of rows, for a caller that writes in batches.
/// </summary>
/// <remarks>
/// Five million rows are not five million inserts. A caller reading in chunks hands each one to a
/// bulk write and reports the failures beside it, so the report keeps pace with the writing instead
/// of accumulating until the end.
/// </remarks>
/// <typeparam name="T">What the mapper builds.</typeparam>
public sealed class ImportChunk<T>
{
    internal ImportChunk(IReadOnlyList<T> items, IReadOnlyList<RowError> errors, int firstRow, int lastRow)
    {
        Items = items;
        Errors = errors;
        FirstRowNumber = firstRow;
        LastRowNumber = lastRow;
    }

    /// <summary>What the mapper built from the rows in this batch.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Why rows in this batch produced nothing.</summary>
    public IReadOnlyList<RowError> Errors { get; }

    /// <summary>The first row this batch covers.</summary>
    public int FirstRowNumber { get; }

    /// <summary>The last row this batch covers.</summary>
    public int LastRowNumber { get; }
}
