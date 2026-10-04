using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The memory a writer writes into synchronously, drained into the target asynchronously.</summary>
[SuppressMessage("Sonar", "S6966", Justification = "SpillBuffer is intentionally written to synchronously and drained asynchronously; the split is fundamental to the design.")]
public sealed class SpillBufferTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DrainsEverythingWrittenInOrder()
    {
        using SpillBuffer buffer = new();
        byte[] large = [.. Enumerable.Range(0, 200_000).Select(i => (byte)i)];

        buffer.Write([1, 2, 3]);
        buffer.Write(large);
        buffer.WriteByte(9);

        using MemoryStream target = new();
        await buffer.DrainToAsync(target, Token);

        Assert.Equal([1, 2, 3, .. large, 9], target.ToArray());
    }

    [Fact]
    public async Task CountsPendingUntilDrainedAndTotalForever()
    {
        using SpillBuffer buffer = new();
        buffer.Write(new byte[100]);

        Assert.Equal(100, buffer.Pending);

        await buffer.DrainToAsync(Stream.Null, Token);
        buffer.Write(new byte[5]);

        Assert.Equal(5, buffer.Pending);
        Assert.Equal(105, buffer.TotalWritten);
    }

    [Fact]
    public async Task HandsOutASpanAtLeastAsLargeAsAsked()
    {
        using SpillBuffer buffer = new();
        buffer.Write(new byte[65_000]);

        Span<byte> span = buffer.GetSpan(10_000);
        Assert.True(span.Length >= 10_000);
        span[..3].Fill(7);
        buffer.Advance(3);

        using MemoryStream target = new();
        await buffer.DrainToAsync(target, Token);

        Assert.Equal(65_003, target.Length);
        Assert.Equal([7, 7, 7], target.ToArray()[^3..]);
    }

    [Fact]
    public async Task DiscardDropsWhatIsPending()
    {
        using SpillBuffer buffer = new();
        buffer.Write(new byte[10]);

        buffer.Discard();

        using MemoryStream target = new();
        await buffer.DrainToAsync(target, Token);
        Assert.Equal(0, target.Length);
        Assert.Equal(0, buffer.Pending);
    }

    [Fact]
    public void RefusesToAdvancePastTheSpanItHandedOut()
    {
        using SpillBuffer buffer = new();
        int length = buffer.GetSpan(1).Length;

        Assert.Throws<InvalidOperationException>(() => buffer.Advance(length + 1));
    }

    [Fact]
    public void IsAWriteOnlyForwardOnlyStream()
    {
        using SpillBuffer buffer = new();

        Assert.True(buffer.CanWrite);
        Assert.False(buffer.CanRead);
        Assert.False(buffer.CanSeek);
    }
}
