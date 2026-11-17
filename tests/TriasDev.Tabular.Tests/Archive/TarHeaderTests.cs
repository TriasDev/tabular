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

    [Theory]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Gnu)]
    public void KnowsAHeaderWhoseNameHoldsBytesAbove127(TarEntryFormat format)
    {
        // The checksum sums bytes, and a name in UTF-8 brings bytes the signed and unsigned sums
        // disagree on. Unsigned is the standard; some old writers summed signed bytes, so both count.
        // (A pax archive opens with its extended header, whose own name is ASCII.)
        byte[] tar = TarArchive.Of(format, ("Grüße.csv", "a;b\n"));
        Assert.True(TarHeader.IsHeader(tar.AsSpan(0, 512)));

        byte[] signed = (byte[])tar.Clone();
        long sum = 0;

        for (int i = 0; i < 512; i++)
        {
            sum += i is >= 148 and < 156 ? ' ' : (sbyte)signed[i];
        }

        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ").CopyTo(signed, 148);
        Assert.NotEqual(tar[148..156], signed[148..156]);
        Assert.True(TarHeader.IsHeader(signed.AsSpan(0, 512)));
    }

    [Theory]
    [InlineData("  12345\0")]   // right-aligned behind spaces, as old writers padded it
    [InlineData("00012345")]    // eight digits, the whole field, with no terminator
    [InlineData("012345 \0")]
    [InlineData("0012345 ")]
    public void ReadsTheChecksumInEveryFormWritersGiveIt(string form)
    {
        // The header is laid out so its sum is exactly octal 12345 in each form; the field is summed
        // as spaces whatever it holds.
        byte[] header = HeaderSummingTo(Convert.ToInt64("12345", 8));
        Encoding.ASCII.GetBytes(form).CopyTo(header, 148);

        Assert.True(TarHeader.IsHeader(header));
    }

    [Theory]
    [InlineData("        ")]   // spaces only: no digit at all
    [InlineData("  12x4\0 ")]
    [InlineData("0001234x")]
    public void RefusesAChecksumFieldThatIsNotANumber(string form)
    {
        byte[] header = HeaderSummingTo(Convert.ToInt64("1234", 8));
        Encoding.ASCII.GetBytes(form).CopyTo(header, 148);

        Assert.False(TarHeader.IsHeader(header));
    }

    /// <summary>A ustar header whose bytes, with the checksum field counted as spaces, sum to <paramref name="sum"/>.</summary>
    private static byte[] HeaderSummingTo(long sum)
    {
        byte[] header = new byte[512];
        Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(header, 257);
        long rest = sum - (8 * ' ') - "ustar00".Sum(c => (long)c);

        // Spread what is left over the name field, at most 0x7F a byte, so no byte is negative signed.
        int at = 0;

        while (rest > 0)
        {
            byte b = (byte)Math.Min(0x7F, rest);
            header[at++] = b;
            rest -= b;
        }

        return header;
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
