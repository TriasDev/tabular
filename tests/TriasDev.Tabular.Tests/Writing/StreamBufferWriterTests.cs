using System.Buffers;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A buffer writer that pushes every committed span into a stream at once.</summary>
public sealed class StreamBufferWriterTests
{
    [Fact]
    public void WritesWhatIsCommittedAndReusesItsBuffer()
    {
        MemoryStream target = new();
        StreamBufferWriter writer = new(initialSize: 8) { Target = target };

        Span<byte> first = writer.GetSpan(3);
        "abc"u8.CopyTo(first);
        writer.Advance(3);
        Span<byte> second = writer.GetSpan(2);
        "de"u8.CopyTo(second);
        writer.Advance(2);

        Assert.Equal("abcde"u8.ToArray(), target.ToArray());
    }

    [Fact]
    public void GrowsForALargerHint()
    {
        MemoryStream target = new();
        StreamBufferWriter writer = new(initialSize: 4) { Target = target };

        Span<byte> span = writer.GetSpan(100);
        Assert.True(span.Length >= 100);
        span[..100].Fill((byte)'x');
        writer.Advance(100);

        Assert.Equal(100, target.Length);
    }

    [Fact]
    public void RefusesToCommitMoreThanItLent()
    {
        StreamBufferWriter writer = new(initialSize: 4) { Target = new MemoryStream() };
        writer.GetSpan(4);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(5_000));
    }
}
