namespace TriasDev.Tabular;

/// <summary>
/// A set of 64-bit value hashes: one flat array, open addressing, linear probing.
/// </summary>
/// <remarks>
/// <para>
/// What the distinct count spends its time in. A column of millions of different values asks the set
/// once per row, and every question lands somewhere random in memory. <see cref="HashSet{T}"/> keeps
/// an entry of sixteen to twenty-four bytes per value behind a bucket array; this keeps eight, in
/// the one array it probes, so far more of it stays in cache and a lookup touches one line instead of
/// two.
/// </para>
/// <para>
/// Zero marks an empty slot, so the value zero is held apart in a flag. The table is kept at most
/// half full, where linear probing stays short; the hashes are FNV-1a, well mixed in their low bits.
/// </para>
/// </remarks>
internal sealed class UInt64Set
{
    private ulong[] _slots = new ulong[16];
    private int _used;
    private bool _hasZero;

    /// <summary>How many values the set holds.</summary>
    public int Count => _used + (_hasZero ? 1 : 0);

    /// <summary>Whether the set holds <paramref name="value"/>.</summary>
    public bool Contains(ulong value)
    {
        if (value == 0)
        {
            return _hasZero;
        }

        ulong[] slots = _slots;
        int mask = slots.Length - 1;
        int i = (int)value & mask;

        while (true)
        {
            ulong slot = slots[i];

            if (slot == value)
            {
                return true;
            }

            if (slot == 0)
            {
                return false;
            }

            i = (i + 1) & mask;
        }
    }

    /// <summary>Adds <paramref name="value"/>; false when it was already there.</summary>
    public bool Add(ulong value)
    {
        if (value == 0)
        {
            bool added = !_hasZero;
            _hasZero = true;
            return added;
        }

        if ((_used + 1) * 2 > _slots.Length)
        {
            Grow();
        }

        return Insert(_slots, value);
    }

    private bool Insert(ulong[] slots, ulong value)
    {
        int mask = slots.Length - 1;
        int i = (int)value & mask;

        while (true)
        {
            ulong slot = slots[i];

            if (slot == value)
            {
                return false;
            }

            if (slot == 0)
            {
                slots[i] = value;
                _used++;
                return true;
            }

            i = (i + 1) & mask;
        }
    }

    private void Grow()
    {
        ulong[] old = _slots;
        ulong[] slots = new ulong[old.Length * 2];
        _used = 0;

        foreach (ulong value in old.Where(v => v != 0))
        {
            Insert(slots, value);
        }

        _slots = slots;
    }
}
