using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The zip checksum, checked against the standard's vectors and a bitwise reference.</summary>
public sealed class Crc32Tests
{
    /// <summary>The textbook bit-at-a-time CRC-32, independent of the table-driven one under test.</summary>
    private static uint Reference(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;

        foreach (byte b in data)
        {
            crc ^= b;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("123456789", 0xCBF43926u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    public void MatchesTheStandardVectors(string text, uint expected)
    {
        Assert.Equal(expected, Crc32.Compute(Encoding.ASCII.GetBytes(text)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(4_096)]
    [InlineData(100_003)]
    public void AgreesWithTheBitwiseReference(int length)
    {
        byte[] data = new byte[length];
        new Random(length).NextBytes(data);

        Assert.Equal(Reference(data), Crc32.Compute(data));
    }

    [Fact]
    public void UpdatesInPiecesAsInOne()
    {
        byte[] data = new byte[10_000];
        new Random(71).NextBytes(data);

        uint pieces = 0;
        int offset = 0;

        // Uneven pieces, so the eight-byte steps start mid-word in some of them.
        foreach (int size in new[] { 1, 3, 8, 15, 1000, 8973 })
        {
            pieces = Crc32.Update(pieces, data.AsSpan(offset, size));
            offset += size;
        }

        Assert.Equal(data.Length, offset);
        Assert.Equal(Crc32.Compute(data), pieces);
    }
}
