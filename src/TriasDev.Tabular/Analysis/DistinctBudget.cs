namespace TriasDev.Tabular.Analysis;

/// <summary>
/// The file's allowance for exact distinct counting, shared by every column.
/// </summary>
/// <remarks>
/// Counting distinct values exactly costs memory in proportion to how many there are, and there is
/// no way around that — an estimate would answer "roughly five million", which cannot settle whether
/// a column identifies its rows. What can be done is make the constant small and the total bounded:
/// the set holds 64-bit hashes rather than strings, and this object caps how many of them the whole
/// file may keep.
/// </remarks>
public sealed class DistinctBudget
{
    private readonly int _capacity;
    private int _used;

    /// <summary>Creates a budget for a number of tracked values.</summary>
    public DistinctBudget(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
    }

    /// <summary>How many tracked values remain.</summary>
    public int Remaining => _capacity - _used;

    /// <summary>
    /// Claims room for one more tracked value, or reports that there is none.
    /// </summary>
    public bool TryReserve()
    {
        if (_used >= _capacity)
        {
            return false;
        }

        _used++;
        return true;
    }
}
