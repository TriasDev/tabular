
namespace TriasDev.Tabular;

/// <summary>
/// What one row of the file amounted to: an item, or the reasons it produced none.
/// </summary>
/// <remarks>
/// Never both. A row that failed produces no item, because half a row invites a caller to persist
/// half an entity, and that is how silent corruption starts.
/// </remarks>
/// <typeparam name="T">What the mapper builds.</typeparam>
public sealed class ImportOutcome<T>
{
    private ImportOutcome(int rowNumber, T? value, IReadOnlyList<RowError> errors)
    {
        RowNumber = rowNumber;
        Value = value;
        Errors = errors;
    }

    /// <summary>The row's number as a person reading the file would count it.</summary>
    public int RowNumber { get; }

    /// <summary>What the mapper built, or null when the row failed.</summary>
    public T? Value { get; }

    /// <summary>Why the row failed, empty when it did not.</summary>
    public IReadOnlyList<RowError> Errors { get; }

    /// <summary>True when the row produced no item.</summary>
    public bool HasErrors => Errors.Count > 0;

    internal static ImportOutcome<T> Built(int rowNumber, T value) => new(rowNumber, value, []);

    internal static ImportOutcome<T> Failed(int rowNumber, IReadOnlyList<RowError> errors) =>
        new(rowNumber, default, errors);
}
