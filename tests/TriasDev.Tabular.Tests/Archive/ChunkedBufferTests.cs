using TriasDev.Tabular.Archive;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>The in-memory copy of a workbook behaves as a stream does at its edges.</summary>
public sealed class ChunkedBufferTests
{
    private static ChunkedBuffer Copy(int length) =>
        ChunkedBuffer.CopyOf(new MemoryStream(new byte[length]), length, long.MaxValue, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ChunkedBuffer.PieceSize)]
    [InlineData(ChunkedBuffer.PieceSize + 1)]
    [InlineData((2 * ChunkedBuffer.PieceSize) + 17)]
    public void CopiesEveryByteWhateverTheSizeAgainstThePieces(int length)
    {
        byte[] source = new byte[length];
        new Random(length).NextBytes(source);

        using ChunkedBuffer buffer = ChunkedBuffer.CopyOf(new MemoryStream(source), length, long.MaxValue, TestContext.Current.CancellationToken);
        using MemoryStream back = new();
        buffer.CopyTo(back);

        Assert.Equal(source, back.ToArray());
    }

    [Theory]
    [InlineData(SeekOrigin.Begin, -1)]
    [InlineData(SeekOrigin.Current, -11)]
    [InlineData(SeekOrigin.End, -11)]
    public void RefusesASeekBeforeTheStartAsAnIOExceptionAndStaysWhereItWas(SeekOrigin origin, long offset)
    {
        using ChunkedBuffer buffer = Copy(10);
        buffer.Position = 5;

        // MemoryStream and FileStream answer the same way; ArgumentOutOfRangeException was thrown before.
        Assert.Throws<IOException>(() => buffer.Seek(offset, origin));
        Assert.Equal(5, buffer.Position);
    }

    [Fact]
    public void RefusesToBeReadOrSoughtAfterItWasDisposed()
    {
        ChunkedBuffer buffer = Copy(10);
        buffer.Dispose();

        // It used to answer 0, as though the copy were empty.
        Assert.Throws<ObjectDisposedException>(() => buffer.Read(new byte[4], 0, 4));
        Assert.Throws<ObjectDisposedException>(() => buffer.Seek(0, SeekOrigin.Begin));
        Assert.False(buffer.CanRead);
    }
}
