using System.Text;

using TriasDev.Tabular.Archive;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Archive;

/// <summary>A gzip member's header: the name it stores, the fields it may carry, and its damage.</summary>
public sealed class GzipHeaderTests
{
    /// <summary>Every optional field: FHCRC, FEXTRA (3 bytes), FNAME, FCOMMENT.</summary>
    private static readonly byte[] FullHeader =
    [
        0x1F, 0x8B, 0x08, 0x1E, 0, 0, 0, 0, 0, 3,
        3, 0, (byte)'a', (byte)'b', (byte)'c',
        .. "n.csv"u8, 0,
        .. "a comment"u8, 0,
        0x12, 0x34,
    ];

    [Fact]
    public void ReadsTheNameTheHeaderStoresAndStopsRightAfterIt()
    {
        MemoryStream stream = new(GzipFile.Of("a;b\n", "daten.csv"));

        Assert.Equal("daten.csv", GzipHeader.Read(stream));
        Assert.Equal(10 + "daten.csv".Length + 1, stream.Position);
    }

    [Fact]
    public void ReadsNoNameFromAHeaderWithoutOne()
    {
        MemoryStream stream = new(GzipFile.Of("a;b\n"));

        Assert.Null(GzipHeader.Read(stream));
        Assert.Equal(10, stream.Position);
    }

    [Fact]
    public void SkipsTheExtraFieldTheCommentAndTheHeaderChecksum()
    {
        MemoryStream stream = new(FullHeader);

        Assert.Equal("n.csv", GzipHeader.Read(stream));
        Assert.Equal(FullHeader.Length, stream.Position);
    }

    [Fact]
    public void ReadsTheNameAsLatin1AsTheFormatSays()
    {
        MemoryStream stream = new(GzipFile.Of("a\n", "Übersicht.csv"));

        Assert.Equal("Übersicht.csv", GzipHeader.Read(stream));
    }

    [Fact]
    public void RefusesAHeaderCutShortAnywhere()
    {
        for (int cut = 0; cut < FullHeader.Length; cut++)
        {
            TabularFormatException error = Assert.Throws<TabularFormatException>(() => GzipHeader.Read(new MemoryStream(FullHeader[..cut])));

            Assert.Equal(TabularFormatException.Truncated, error.Code);
        }
    }

    [Fact]
    public void RefusesReservedFlags()
    {
        byte[] header = GzipFile.Of("a\n");
        header[3] = 0x20;

        Assert.Equal(TabularFormatException.Corrupt, Assert.Throws<TabularFormatException>(() => GzipHeader.Read(new MemoryStream(header))).Code);
    }

    [Fact]
    public void RefusesAnotherCompressionMethod()
    {
        byte[] header = GzipFile.Of("a\n");
        header[2] = 7;

        Assert.Equal(TabularFormatException.Corrupt, Assert.Throws<TabularFormatException>(() => GzipHeader.Read(new MemoryStream(header))).Code);
    }

    [Fact]
    public void RefusesANameThatNeverEnds()
    {
        byte[] header = [0x1F, 0x8B, 0x08, 0x08, 0, 0, 0, 0, 0, 3, .. Enumerable.Repeat((byte)'x', GzipHeader.MaxTextBytes + 1), 0];

        Assert.Equal(TabularFormatException.Corrupt, Assert.Throws<TabularFormatException>(() => GzipHeader.Read(new MemoryStream(header))).Code);
    }

    [Theory]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, true)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x41 }, true)]
    [InlineData(new byte[] { 0x1F, 0x8B }, false)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x07 }, false)]
    [InlineData(new byte[] { 0x1F, 0x41, 0x08 }, false)]
    [InlineData(new byte[0], false)]
    public void KnowsTheSignature(byte[] head, bool expected) =>
        Assert.Equal(expected, GzipHeader.HasSignature(head));
}
