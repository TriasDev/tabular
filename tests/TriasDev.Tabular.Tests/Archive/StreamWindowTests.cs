using TriasDev.Tabular.Archive;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A window onto part of a stream, and a stream that puts back the bytes already read.</summary>
public sealed class StreamWindowTests
{
    private static readonly byte[] Bytes = [.. Enumerable.Range(0, 1000).Select(i => (byte)i)];

    [Fact]
    public void ReadsOnlyItsPartOfTheStream()
    {
        using StreamWindow window = new(new MemoryStream(Bytes), 100, 50);
        byte[] read = new byte[200];

        int count = window.ReadAtLeast(read, read.Length, throwOnEndOfStream: false);

        Assert.Equal(50, count);
        Assert.Equal(Bytes[100..150], read[..50]);
        Assert.Equal(50, window.Length);
    }

    [Fact]
    public void SeeksWithinItsPart()
    {
        using StreamWindow window = new(new MemoryStream(Bytes), 100, 50);

        window.Position = 40;
        Assert.Equal(140, window.ReadByte());
        Assert.Equal(10, window.Seek(-31, SeekOrigin.Current));
        Assert.Equal(49, window.Seek(-1, SeekOrigin.End));
        Assert.Equal(149, window.ReadByte());
        Assert.Equal(-1, window.ReadByte());
    }

    [Fact]
    public void TwoWindowsOverOneStreamDoNotDisturbEachOther()
    {
        MemoryStream file = new(Bytes);
        using StreamWindow first = new(file, 0, 500);
        using StreamWindow second = new(file, 500, 500);

        Assert.Equal(0, first.ReadByte());
        Assert.Equal(244, second.ReadByte()); // byte 500 wraps to 244
        Assert.Equal(1, first.ReadByte());
        Assert.Equal(245, second.ReadByte());
    }

    [Fact]
    public void LeavesTheStreamOpen()
    {
        MemoryStream file = new(Bytes);

        new StreamWindow(file, 0, 10).Dispose();

        Assert.True(file.CanRead);
    }

    [Fact]
    public void RefusesAPartThatRunsPastTheEndOfTheStreamAsCut()
    {
        using StreamWindow window = new(new MemoryStream(Bytes), 990, 50);
        byte[] read = new byte[50];

        TabularFormatException error = Assert.Throws<TabularFormatException>(() => window.ReadAtLeast(read, read.Length, throwOnEndOfStream: false));

        Assert.Equal(TabularFormatException.Truncated, error.Code);
    }

    [Fact]
    public void PutsTheHeadBackInFrontOfTheRest()
    {
        MemoryStream rest = new(Bytes[10..]);
        using HeadedStream stream = new(Bytes[..10], rest);
        using MemoryStream all = new();

        stream.CopyTo(all, 7);

        Assert.Equal(Bytes, all.ToArray());
        Assert.True(rest.CanRead);
    }
}
