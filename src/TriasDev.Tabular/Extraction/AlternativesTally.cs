namespace TriasDev.Tabular;

/// <summary>
/// The counters behind <see cref="AlternativesReport"/>, kept by a run as it reads.
/// </summary>
/// <remarks>
/// Fixed in size for the whole run except the row-number lists, which stop at their cap: a file of
/// five million unlocatable rows keeps fifty numbers and a count.
/// </remarks>
internal sealed class AlternativesTally
{
    private readonly ResolvedAlternatives[] _sets;
    private readonly int[] _offset;
    private readonly int[] _maxReachable;
    private readonly int _cap;

    private readonly int[] _judged;
    private readonly int[] _unresolved;
    private readonly List<int>[] _unresolvedRows;

    private readonly int[] _needing;
    private readonly int[] _won;
    private readonly int[][] _atLevel;
    private readonly int[] _incomplete;
    private readonly int[] _empty;
    private readonly int[] _ignored;
    private readonly List<int>[] _incompleteRows;

    public AlternativesTally(ResolvedAlternatives[] sets, int[] offset, int[] maxReachable, int cap)
    {
        _sets = sets;
        _offset = offset;
        _maxReachable = maxReachable;
        _cap = cap;

        _judged = new int[sets.Length];
        _unresolved = new int[sets.Length];
        _unresolvedRows = [.. sets.Select(_ => new List<int>())];

        ResolvedGroup[] groups = [.. sets.SelectMany(s => s.Groups)];
        _needing = new int[groups.Length];
        _won = new int[groups.Length];
        _atLevel = [.. groups.Select(g => new int[g.Levels.Length + 1])];
        _incomplete = new int[groups.Length];
        _empty = new int[groups.Length];
        _ignored = new int[groups.Length];
        _incompleteRows = [.. groups.Select(_ => new List<int>())];
    }

    /// <summary>Counts one row that produced values.</summary>
    public void Row(int rowNumber, RowJudgement row)
    {
        for (int s = 0; s < _sets.Length; s++)
        {
            _judged[s]++;
            AlternativeResolution resolution = row.Resolutions[s];

            if (!resolution.IsResolved)
            {
                _unresolved[s]++;
                Keep(_unresolvedRows[s], rowNumber);
            }

            for (int g = 0; g < _sets[s].Groups.Length; g++)
            {
                Group(_offset[s] + g, resolution.GroupIndex == g, rowNumber, row);
            }
        }
    }

    public IReadOnlyList<AlternativesReport> Snapshot() =>
    [
        .. _sets.Select((set, s) => new AlternativesReport
        {
            Name = set.Declared.Name,
            RowsJudged = _judged[s],
            Unresolved = _unresolved[s],
            UnresolvedRows = [.. _unresolvedRows[s]],
            UnresolvedRowsComplete = _unresolvedRows[s].Count == _unresolved[s],
            Groups = [.. set.Groups.Select((group, g) => Report(group, _offset[s] + g))],
        }),
    ];

    private void Group(int id, bool won, int rowNumber, RowJudgement row)
    {
        _ignored[id] += row.Ignored[id];

        if (!row.Needed[id])
        {
            return;
        }

        int level = row.Levels[id];
        _needing[id]++;
        _atLevel[id][level]++;

        if (won)
        {
            _won[id]++;
        }

        if (!row.AnyPresent[id])
        {
            _empty[id]++;
        }

        if (level < _maxReachable[id])
        {
            _incomplete[id]++;
            Keep(_incompleteRows[id], rowNumber);
        }
    }

    private AlternativeGroupReport Report(ResolvedGroup group, int id) => new()
    {
        Name = group.Name,
        RowsNeeding = _needing[id],
        Won = _won[id],
        MaxReachableLevel = _maxReachable[id],
        RowsAtLevel = [.. _atLevel[id]],
        Incomplete = _incomplete[id],
        Empty = _empty[id],
        IncompleteRows = [.. _incompleteRows[id]],
        IncompleteRowsComplete = _incompleteRows[id].Count == _incomplete[id],
        IgnoredInvalidValues = _ignored[id],
    };

    private void Keep(List<int> rows, int rowNumber)
    {
        if (rows.Count < _cap)
        {
            rows.Add(rowNumber);
        }
    }
}

/// <summary>What a run worked out about one row's alternatives, per flat group id; views of the run's own arrays.</summary>
internal readonly record struct RowJudgement(
    AlternativeResolution[] Resolutions,
    bool[] Needed,
    int[] Levels,
    bool[] AnyPresent,
    int[] Ignored);
