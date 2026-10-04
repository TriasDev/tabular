using System.Buffers;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>A buffer writer that pushes every committed span into a stream at once.</summary>
public sealed class StreamBufferWriterTests
{
    [Fact]
    public void CollectsCommittedBytesAndWritesThemOnFlush()
    {
        MemoryStream target = new();
        StreamBufferWriter writer = new(workingSize: 8) { Target = target };

        "abc"u8.CopyTo(writer.GetSpan(3));
        writer.Advance(3);
        "de"u8.CopyTo(writer.GetSpan(2));
        writer.Advance(2);
        Assert.Equal(0, target.Length);

        writer.Flush();

        Assert.Equal("abcde"u8.ToArray(), target.ToArray());
    }

    [Fact]
    public void WritesWhatItHoldsWhenTheNextRequestDoesNotFit()
    {
        MemoryStream target = new();
        StreamBufferWriter writer = new(workingSize: 8) { Target = target };

        "abcdef"u8.CopyTo(writer.GetSpan(6));
        writer.Advance(6);
        Span<byte> next = writer.GetSpan(4);
        Assert.True(next.Length >= 4);
        Assert.Equal("abcdef"u8.ToArray(), target.ToArray());
        "gh"u8.CopyTo(next);
        writer.Advance(2);
        writer.Flush();

        Assert.Equal("abcdefgh"u8.ToArray(), target.ToArray());
    }

    [Fact]
    public void ALargerRequestIsLentAOneOffBufferDroppedOnceCommitted()
    {
        MemoryStream target = new();
        StreamBufferWriter writer = new(workingSize: 4) { Target = target };

        Span<byte> span = writer.GetSpan(100);
        Assert.True(span.Length >= 100);
        span[..100].Fill((byte)'x');
        writer.Advance(100);

        Assert.Equal(100, target.Length);
        Assert.Equal(4, writer.BufferLength);
    }

    [Fact]
    public void RefusesToCommitMoreThanItLent()
    {
        StreamBufferWriter writer = new(workingSize: 4) { Target = new MemoryStream() };
        writer.GetSpan(4);

        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Advance(5_000));
    }

    [Fact]
    public void FlushingBytesWithoutATargetThrowsAnInvalidOperation()
    {
        StreamBufferWriter writer = new(workingSize: 4);
        "ab"u8.CopyTo(writer.GetSpan(2));
        writer.Advance(2);

        Assert.Throws<InvalidOperationException>(writer.Flush);
    }
}
