using System.Formats.Tar;
using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A tar header is known by its magic and its checksum, raw or inside gzip.</summary>
public sealed class TarHeaderTests
{
    [Theory]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Gnu)]
    public void KnowsTheFirstHeaderOfEveryFormatWithAMagic(TarEntryFormat format)
    {
        byte[] tar = TarArchive.Of(format, ("a.csv", "a;b\n"));

        Assert.True(TarHeader.IsHeader(tar.AsSpan(0, 512)));
    }

    [Fact]
    public void DoesNotKnowAV7HeaderWhichHasNoMagic()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.V7, ("a.csv", "a;b\n"));

        Assert.False(TarHeader.IsHeader(tar.AsSpan(0, 512)));
    }

    [Fact]
    public void RefusesAHeaderWhoseChecksumIsWrong()
    {
        byte[] tar = TarArchive.Of(TarEntryFormat.Ustar, ("a.csv", "a;b\n"));
        tar[0] ^= 0x01;

        Assert.False(TarHeader.IsHeader(tar.AsSpan(0, 512)));
    }

    [Fact]
    public void KeepsTextThatHappensToSpellTheMagicForText()
    {
        // A csv line long enough to put "ustar" at offset 257: the checksum cannot match by chance.
        byte[] text = Encoding.ASCII.GetBytes(new string('a', 257) + "ustar\0" + "00" + new string('b', 300));

        Assert.False(TarHeader.IsHeader(text.AsSpan(0, 512)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    public void RefusesABlockShorterThan512Bytes(int length) =>
        Assert.False(TarHeader.IsHeader(new byte[length]));

    [Fact]
    public void RefusesAZeroBlock() => Assert.False(TarHeader.IsHeader(new byte[512]));

    [Fact]
    public void KnowsAGzippedTarAndLeavesTheStreamWhereItWas()
    {
        MemoryStream stream = new([.. "xx"u8, .. GzipFile.Of(TarArchive.Of(TarEntryFormat.Pax, ("a.csv", "a;b\n")))]) { Position = 2 };

        Assert.True(TarHeader.IsGzippedTar(stream));
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void DoesNotKnowAGzippedCsvOrAShortOrDamagedGzipForATar()
    {
        Assert.False(TarHeader.IsGzippedTar(new MemoryStream(GzipFile.Of("a;b\n1;2\n"))));
        Assert.False(TarHeader.IsGzippedTar(new MemoryStream(GzipFile.Of("a")[..12])));
        Assert.False(TarHeader.IsGzippedTar(new MemoryStream("a;b\n"u8.ToArray())));
    }
}
