namespace TriasDev.Tabular.Analysis;

/// <summary>
/// A 64-bit hash of a value, used so that counting distinct values costs eight bytes each rather
/// than a string object each.
/// </summary>
/// <remarks>
/// <para>
/// FNV-1a. Not cryptographic, and not meant to be: nothing here defends against an adversary, and
/// the worst a crafted file achieves is a distinct count reported slightly low.
/// </para>
/// <para>
/// The chance of an accidental collision follows the birthday bound, n squared over twice the
/// number of hashes: about one in 37 million over a million values, and about one in 9 million over
/// the two million the default budget tracks. It rises with the count rather than falling — an
/// earlier version of this comment claimed the opposite — and it is small enough that the count is
/// described as exact, but it is not zero, and a collision undercounts.
/// </para>
/// </remarks>
internal static class ValueHash
{
    private const ulong OffsetBasis = 14695981039346656037;
    private const ulong Prime = 1099511628211;

    public static ulong Of(ReadOnlySpan<char> value)
    {
        ulong hash = OffsetBasis;

        foreach (char c in value)
        {
            hash ^= (byte)(c & 0xFF);
            hash *= Prime;
            hash ^= (byte)(c >> 8);
            hash *= Prime;
        }

        return hash;
    }
}
