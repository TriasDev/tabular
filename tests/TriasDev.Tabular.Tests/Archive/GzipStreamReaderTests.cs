using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>
/// The content of a gzip file, decompressed and checked: exact when the file is whole, refused —
/// never shorter — when it is cut off or damaged.
/// </summary>
public sealed class GzipStreamReaderTests
{
    private static byte[] Text(int lines) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, lines).Select(i => $"{i};Zeile {i * 7919 % 1000};Köln\n")));

    private static byte[] ReadAll(byte[] file, long limit = long.MaxValue, int piece = 81920)
    {
        using GzipStreamReader reader = new(new MemoryStream(file, writable: false), limit);
        using MemoryStream content = new();
        byte[] buffer = new byte[piece];
        int read;

        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            content.Write(buffer, 0, read);
        }

        return content.ToArray();
    }

    private static TabularFormatException Refusal(byte[] file) =>
        Assert.Throws<TabularFormatException>(() => ReadAll(file));

    [Fact]
    public void ReadsOneMemberExactly()
    {
        byte[] content = Text(20_000);

        Assert.Equal(content, ReadAll(GzipFile.Of(content)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1 << 20)]
    public void ReadsTheSameInPiecesOfAnySize(int piece)
    {
        byte[] content = Text(3_000);

        Assert.Equal(content, ReadAll(GzipFile.Of(content), piece: piece));
    }

    [Fact]
    public void ReadsTheNameOfTheFirstMember()
    {
        using GzipStreamReader reader = new(new MemoryStream(GzipFile.Of("a\n", "orders.csv")), long.MaxValue);

        Assert.Equal("orders.csv", reader.Name);
    }

    [Fact]
    public void ReadsEveryMemberOfAConcatenatedFile()
    {
        byte[] a = Text(5_000), b = Text(10), c = Text(40_000);
        byte[] file = [.. GzipFile.Of(a), .. GzipFile.Of(b), .. GzipFile.Of([]), .. GzipFile.Of(c)];

        byte[] expected = [.. a, .. b, .. c];

        Assert.Equal(expected, ReadAll(file));
    }

    [Fact]
    public void ReadsAnEmptyFile() => Assert.Empty(ReadAll(GzipFile.Of([])));

    [Fact]
    public void IgnoresTrailingBytesThatAreNoMember()
    {
        byte[] content = Text(2_000);
        byte[] noise = new byte[200_000];
        new Random(3).NextBytes(noise);
        noise[0] = 0x41;

        Assert.Equal(content, ReadAll([.. GzipFile.Of(content), 0, 0, 0, 0]));
        Assert.Equal(content, ReadAll([.. GzipFile.Of(content), .. noise]));
    }

    [Fact]
    public void RefusesEveryCutOfAFile()
    {
        byte[] file = GzipFile.Of(Text(300));

        for (int cut = 0; cut < file.Length; cut++)
        {
            TabularFormatException error = Refusal(file[..cut]);

            Assert.True(error.Code is TabularFormatException.Truncated or TabularFormatException.Corrupt, $"cut {cut}: {error.Code}");
        }
    }

    [Fact]
    public void RefusesEveryCutOfAnEmptyFile()
    {
        // Its trailer is eight zero bytes, which its own deflate data 03 00 helps to spell.
        byte[] file = GzipFile.Of([]);

        for (int cut = 0; cut < file.Length; cut++)
        {
            Refusal(file[..cut]);
        }
    }

    [Fact]
    public void RefusesEveryCutOfATwoMemberFileButTheOneBetweenThem()
    {
        byte[] first = GzipFile.Of(Text(200));
        byte[] file = [.. first, .. GzipFile.Of(Text(150))];

        for (int cut = 0; cut < file.Length; cut++)
        {
            if (cut == first.Length)
            {
                // A whole file of one member: nothing about it says a second one is missing.
                Assert.Equal(Text(200), ReadAll(file[..cut]));
                continue;
            }

            Refusal(file[..cut]);
        }
    }

    [Fact]
    public void RefusesAFileCutOneByteIntoItsNextMember()
    {
        Assert.Equal(TabularFormatException.Truncated, Refusal([.. GzipFile.Of(Text(100)), 0x1F]).Code);
    }

    [Fact]
    public void RefusesAWrongChecksum()
    {
        byte[] file = GzipFile.Of(Text(1_000));
        file[^6] ^= 0xFF;

        // Followed by more bytes the reader can tell the end was there and wrong; as the file's very
        // last bytes it cannot tell a wrong trailer from a missing one, and refuses it either way.
        Assert.Equal(TabularFormatException.Corrupt, Refusal([.. file, .. new byte[200_000]]).Code);
        Refusal(file);
    }

    [Fact]
    public void RefusesAWrongSize()
    {
        byte[] file = GzipFile.Of(Text(1_000));
        file[^2] ^= 0xFF;

        Assert.Equal(TabularFormatException.Corrupt, Refusal([.. file, .. new byte[200_000]]).Code);
    }

    [Fact]
    public void RefusesBrokenCompressedDataWithoutLettingTheDecompressorsExceptionOut()
    {
        byte[] file = GzipFile.Of(Text(20_000));

        foreach (int at in new[] { 12, 20, file.Length / 2 })
        {
            byte[] broken = (byte[])file.Clone();
            broken[at] ^= 0xFF;

            Refusal(broken);
        }
    }

    [Fact]
    public void StopsAtTheBoundOnDecompressedBytes()
    {
        byte[] content = Text(1_000);

        TabularLimitException error = Assert.Throws<TabularLimitException>(() => ReadAll(GzipFile.Of(content), limit: content.Length - 1));

        Assert.Equal(nameof(ArchiveCursorOptions.MaxUncompressedBytes), error.Limit);
        Assert.Equal(content, ReadAll(GzipFile.Of(content), limit: content.Length));
    }

    [Fact]
    public void CountsTheBoundAcrossMembers()
    {
        byte[] content = Text(1_000);

        Assert.Throws<TabularLimitException>(() => ReadAll([.. GzipFile.Of(content), .. GzipFile.Of(content)], limit: (content.Length * 2) - 1));
    }

    [Fact]
    public void SaysHowFarIntoTheFileItHasRead()
    {
        byte[] file = GzipFile.Of(Text(50_000));
        using GzipStreamReader reader = new(new MemoryStream(file), long.MaxValue);

        Assert.Equal(10, reader.FilePosition);

        reader.ReadExactly(new byte[100_000]);
        long part = reader.FilePosition;
        Assert.InRange(part, 11, file.Length - 1);

        reader.CopyTo(Stream.Null);
        Assert.Equal(file.Length, reader.FilePosition);
    }

    // An empty member as writers that flush emit it: deflate data 00 00 00 FF FF 03 00, trailer of zeros.
    private static readonly byte[] _flushedEmptyMember =
        [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00,
         0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void ReadsAMemberAfterAnEmptyMemberWrittenWithAFlush()
    {
        byte[] file = [.. _flushedEmptyMember, .. GzipFile.Of("a;b\n1;2\n")];

        Assert.Equal(Encoding.UTF8.GetBytes("a;b\n1;2\n"), ReadAll(file));
    }

    [Fact]
    public void ReadsAnEmptyMemberWrittenWithAFlushAsEmpty() => Assert.Empty(ReadAll(_flushedEmptyMember));

    [Fact]
    public void RefusesEveryCutOfAFlushedEmptyMemberFollowedByAnother()
    {
        byte[] file = [.. _flushedEmptyMember, .. GzipFile.Of("a;b\n1;2\n")];

        for (int cut = 0; cut < file.Length; cut++)
        {
            if (cut == _flushedEmptyMember.Length)
            {
                Assert.Empty(ReadAll(file[..cut]));
                continue;
            }

            if (cut == _flushedEmptyMember.Length - 1)
            {
                // Seven of the trailer's eight zeros, and the deflate data's own last 00 spells the
                // eighth: no byte tells this from a whole file, and nothing of the content is missing.
                Assert.Empty(ReadAll(file[..cut]));
                continue;
            }

            Refusal(file[..cut]);
        }
    }

    [Fact]
    public void NeverReportsAFilePositionBehindAnEarlierOne()
    {
        byte[] file = [.. Enumerable.Range(0, 50).SelectMany(i => GzipFile.Of(Text(100 + i)))];
        using GzipStreamReader reader = new(new MemoryStream(file), long.MaxValue);
        byte[] buffer = new byte[500];
        long last = reader.FilePosition;

        while (reader.Read(buffer, 0, buffer.Length) > 0)
        {
            Assert.True(reader.FilePosition >= last, $"{reader.FilePosition} after {last}");
            last = reader.FilePosition;
        }

        Assert.Equal(file.Length, reader.FilePosition);
    }

    /// <summary>
    /// One member whose deflate data is a stored block of content, then <paramref name="emptyBlocks"/>
    /// blocks that produce no output, then the final empty block — the shape that pushes the end of
    /// the data far behind the decompressor's last read of output.
    /// </summary>
    private static byte[] MemberWithOutputFreeTail(byte[] content, int emptyBlocks)
    {
        using MemoryStream member = new();
        member.Write([0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0xFF]);
        member.WriteByte(0x00);
        member.Write(BitConverter.GetBytes((ushort)content.Length));
        member.Write(BitConverter.GetBytes((ushort)~content.Length));
        member.Write(content);

        for (int i = 0; i < emptyBlocks; i++)
        {
            member.Write([0x00, 0x00, 0x00, 0xFF, 0xFF]);
        }

        member.Write([0x01, 0x00, 0x00, 0xFF, 0xFF]);
        member.Write(BitConverter.GetBytes(Crc32.Append(0, content)));
        member.Write(BitConverter.GetBytes((uint)content.Length));
        return member.ToArray();
    }

    [Fact]
    public void ReadsAMemberWhoseDataEndsInALongRunOfOutputFreeBlocks()
    {
        byte[] content = Text(50);

        Assert.Equal(content, ReadAll(MemberWithOutputFreeTail(content, 20_000)));
    }

    [Fact]
    public void ReadsTheMemberAfterALongRunOfOutputFreeBlocks()
    {
        byte[] content = Text(50);
        byte[] file = [.. MemberWithOutputFreeTail(content, 20_000), .. GzipFile.Of("x\n")];

        Assert.Equal([.. content, .. "x\n"u8.ToArray()], ReadAll(file));
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(3)]
    public void RefusesALongOutputFreeTailCutOff(int cut)
    {
        byte[] file = MemberWithOutputFreeTail(Text(50), 20_000);

        Refusal(file[..^cut]);
    }
}
