using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace TriasDev.Tabular;

/// <summary>
/// Collections that compare by their elements, for the records that carry them.
/// </summary>
/// <remarks>
/// <para>
/// A record compares its members with their own <c>Equals</c>, and a list's is reference equality — so
/// two plans with the same bindings, or two profiles of one file, were unequal, and a plan read back
/// from storage never equalled the one written. Wrapping the collection as it is assigned keeps the
/// record's generated equality, and with it every member added later, instead of a hand-written
/// <c>Equals</c> that would have to be kept in step with the record by hand.
/// </para>
/// <para>
/// The wrapper holds the caller's collection rather than copying it: a record is a shallow value, as
/// before, and nothing on the read path pays for it.
/// </para>
/// </remarks>
internal static class Equatable
{
    [return: NotNullIfNotNull(nameof(items))]
    public static IReadOnlyList<T>? List<T>(IReadOnlyList<T>? items) =>
        items is null or EquatableList<T> ? items : new EquatableList<T>(items);

    [return: NotNullIfNotNull(nameof(items))]
    public static IReadOnlyDictionary<TKey, TValue>? Dictionary<TKey, TValue>(IReadOnlyDictionary<TKey, TValue>? items) =>
        items is null or EquatableDictionary<TKey, TValue> ? items : new EquatableDictionary<TKey, TValue>(items);

    public static IReadOnlyList<T> Empty<T>() => EquatableList<T>.Empty;
}

/// <summary>A read-only list equal to another with the same elements in the same order.</summary>
internal sealed class EquatableList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    public static readonly EquatableList<T> Empty = new([]);

    public int Count => items.Count;

    public T this[int index] => items[index];

    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(EquatableList<T>? other)
    {
        if (other is null || other.Count != Count)
        {
            return false;
        }

        EqualityComparer<T> comparer = EqualityComparer<T>.Default;

        for (int i = 0; i < Count; i++)
        {
            if (!comparer.Equals(items[i], other[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    public override int GetHashCode()
    {
        HashCode hash = new();

        foreach (T item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A read-only dictionary equal to another holding the same pairs, in any order.</summary>
internal sealed class EquatableDictionary<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> items)
    : IReadOnlyDictionary<TKey, TValue>, IEquatable<EquatableDictionary<TKey, TValue>>
{
    public int Count => items.Count;

    public TValue this[TKey key] => items[key];

    public IEnumerable<TKey> Keys => items.Keys;

    public IEnumerable<TValue> Values => items.Values;

    public bool ContainsKey(TKey key) => items.ContainsKey(key);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => items.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(EquatableDictionary<TKey, TValue>? other)
    {
        if (other is null || other.Count != Count)
        {
            return false;
        }

        EqualityComparer<TValue> comparer = EqualityComparer<TValue>.Default;

        foreach (KeyValuePair<TKey, TValue> pair in items)
        {
            if (!other.TryGetValue(pair.Key, out TValue? value) || !comparer.Equals(pair.Value, value))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as EquatableDictionary<TKey, TValue>);

    public override int GetHashCode()
    {
        // Order-independent, as the equality is: the sum of each pair's hash.
        int hash = Count;

        foreach (KeyValuePair<TKey, TValue> pair in items)
        {
            hash = unchecked(hash + HashCode.Combine(pair.Key, pair.Value));
        }

        return hash;
    }
}
