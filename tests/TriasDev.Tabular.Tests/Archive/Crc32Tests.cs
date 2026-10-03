using System.Buffers.Binary;
using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>The checksum a gzip trailer records: the known values, and every path agreeing.</summary>
public sealed class Crc32Tests
{
    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("123456789", 0xCBF43926u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    public void ComputesTheKnownValues(string text, uint expected) =>
        Assert.Equal(expected, Crc32.Append(0, Encoding.ASCII.GetBytes(text)));

    [Fact]
    public void AppendingPiecesEqualsComputingAtOnce()
    {
        byte[] data = new byte[100_003];
        new Random(5).NextBytes(data);
        uint crc = 0;

        for (int at = 0; at < data.Length; at += 777)
        {
            crc = Crc32.Append(crc, data.AsSpan(at, Math.Min(777, data.Length - at)));
        }

        Assert.Equal(Crc32.Append(0, data), crc);
    }

    [Fact]
    public void TheInstructionAndTheTableAgree()
    {
        Assert.SkipUnless(ArmCrc32.Arm64.IsSupported, "This CPU has no ARM64 CRC32 instruction.");

        byte[] data = new byte[4099];
        new Random(9).NextBytes(data);

        for (int length = 0; length < 40; length++)
        {
            Assert.Equal(Crc32.UpdateManaged(~0u, data.AsSpan(0, length)), Crc32.UpdateArm64(~0u, data.AsSpan(0, length)));
        }

        Assert.Equal(Crc32.UpdateManaged(~0u, data), Crc32.UpdateArm64(~0u, data));
    }

    [Fact]
    public void MatchesWhatGZipStreamRecords()
    {
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"{i};Köln\n")));
        byte[] gzip = GzipFile.Of(data);

        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(gzip.AsSpan(gzip.Length - 8)), Crc32.Append(0, data));
    }
}
