using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>The set of value hashes behind the distinct count answers as a HashSet would.</summary>
public sealed class UInt64SetTests
{
    [Fact]
    public void AddsEachValueOnce()
    {
        UInt64Set set = new();

        Assert.True(set.Add(42));
        Assert.False(set.Add(42));
        Assert.True(set.Contains(42));
        Assert.False(set.Contains(43));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void HoldsZeroLikeAnyOtherValue()
    {
        // Zero marks an empty slot inside the set, so it is kept apart; a hash of zero must still count.
        UInt64Set set = new();

        Assert.False(set.Contains(0));
        Assert.True(set.Add(0));
        Assert.False(set.Add(0));
        Assert.True(set.Contains(0));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void AgreesWithAHashSetAcrossGrowth()
    {
        // Values that collide in the low bits on purpose, and enough of them to grow several times.
        UInt64Set set = new();
        HashSet<ulong> reference = [];
        Random random = new(20260926);

        for (int i = 0; i < 200_000; i++)
        {
            ulong value = i % 3 == 0 ? (ulong)i << 32 : (ulong)random.NextInt64();

            Assert.Equal(reference.Add(value), set.Add(value));
        }

        Assert.Equal(reference.Count, set.Count);
        Assert.All(reference, v => Assert.True(set.Contains(v)));
        Assert.False(set.Contains(ulong.MaxValue - 7));
    }
}
