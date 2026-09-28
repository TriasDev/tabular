namespace TriasDev.Tabular;

/// <summary>
/// The file's allowance for exact distinct counting, shared by every column.
/// </summary>
/// <remarks>
/// Counting distinct values exactly costs memory in proportion to how many there are, and there is
/// no way around that — an estimate would answer "roughly five million", which cannot settle whether
/// a column identifies its rows. What can be done is make the constant small and the total bounded:
/// the set holds 64-bit hashes rather than strings, and this object caps how many of them the whole
/// file may keep at once. A sheet's columns hand theirs back when the sheet is done.
/// </remarks>
internal sealed class DistinctBudget
{
    private readonly int _capacity;

    /// <summary>The columns still counting, the only ones that hold any of the budget.</summary>
    private readonly List<ColumnProfiler> _counting = [];

    private int _used;

    /// <summary>Creates a budget for a number of tracked values.</summary>
    public DistinctBudget(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _capacity = capacity;
    }

    /// <summary>How many tracked values remain.</summary>
    public int Remaining => _capacity - _used;

    /// <summary>Takes a column in as one that counts against the budget.</summary>
    public void Join(ColumnProfiler column) => _counting.Add(column);

    /// <summary>Hands back everything a column held, once its facts are taken and it counts no more.</summary>
    public void Leave(ColumnProfiler column)
    {
        if (_counting.Remove(column))
        {
            _used -= column.Counted;
        }
    }

    /// <summary>
    /// Claims room for one more of <paramref name="requester"/>'s values, making room when there is
    /// none; false when the requester itself had to give up counting.
    /// </summary>
    /// <remarks>
    /// The columns spend the budget row by row, side by side, so they used to run dry at the same row
    /// and every one of them — the identifier column first — came out with its uniqueness undetermined
    /// (#46). Room is now made where an answer is settled or least likely to matter: first by a column
    /// that has already repeated a value, whose uniqueness is decided — the one holding the most, as it
    /// frees the most — and failing that by the rightmost column still counting, so the columns to the
    /// left, where keys usually stand, keep an answer. Nothing changes while the budget lasts.
    /// </remarks>
    public bool TryReserve(ColumnProfiler requester)
    {
        while (_used >= _capacity)
        {
            ColumnProfiler? victim = Victim();

            if (victim is null)
            {
                return false;
            }

            _counting.Remove(victim);
            _used -= victim.StopCounting();

            if (ReferenceEquals(victim, requester))
            {
                return false;
            }
        }

        _used++;
        return true;
    }

    private ColumnProfiler? Victim()
    {
        ColumnProfiler? settled = null;
        ColumnProfiler? rightmost = null;

        foreach (ColumnProfiler column in _counting)
        {
            if (column.HasRepeated && (settled is null || column.Counted > settled.Counted))
            {
                settled = column;
            }

            if (rightmost is null || column.Index > rightmost.Index)
            {
                rightmost = column;
            }
        }

        return settled ?? rightmost;
    }
}
