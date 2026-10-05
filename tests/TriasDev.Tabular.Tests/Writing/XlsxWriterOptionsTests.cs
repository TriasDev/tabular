using System.IO.Compression;


using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The xlsx options ship their defaults and refuse what cannot work, where they are handed over.</summary>
public sealed class XlsxWriterOptionsTests
{
    [Fact]
    public void DefaultsToTheFastestCompression()
    {
        Assert.Equal(CompressionLevel.Fastest, XlsxWriterOptions.Default.CompressionLevel);
        Assert.Same(XlsxWriterOptions.Default, TabularWriterOptions.Default.Xlsx);
    }

    [Fact]
    public void RefusesACompressionLevelThatDoesNotExist()
    {
        ArgumentException refused = Assert.ThrowsAny<ArgumentException>(() => new XlsxWriterOptions { CompressionLevel = (CompressionLevel)42 }.Checked());

        Assert.Contains(nameof(XlsxWriterOptions.CompressionLevel), refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CompressionLevel.Optimal)]
    [InlineData(CompressionLevel.Fastest)]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.SmallestSize)]
    public void TakesEveryCompressionLevel(CompressionLevel level)
    {
        Assert.Equal(level, new XlsxWriterOptions { CompressionLevel = level }.Checked().CompressionLevel);
    }
}
