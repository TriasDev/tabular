using System.Buffers;
using System.Globalization;
using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>The row buffer every sheet writer formats into before encoding it.</summary>
public sealed class RowTextTests
{
    [Fact]
    public void AppendsCharactersTextAndFormattedValues()
    {
        RowText row = new();
        row.Append('<');
        row.Append("c r=\"A1\">");
        int start = row.AppendFormatted(1234.5m, default, CultureInfo.InvariantCulture);
        row.Append('>');

        Assert.Equal("<c r=\"A1\">1234.5>", row.Written.ToString());
        Assert.Equal("1234.5", row.Written[start..^1].ToString());
    }

    [Fact]
    public void GrowsForAValueLongerThanItsBuffer()
    {
        RowText row = new();
        string text = new('x', 100_000);
        row.Append(text);
        row.AppendFormatted(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Unspecified), "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture);

        Assert.Equal(100_010, row.Length);
        Assert.EndsWith("2026-10-03", row.Written.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatesToWhereAValueStarted()
    {
        RowText row = new();
        row.Append("a,");
        int start = row.AppendFormatted(-2.5, "R", CultureInfo.InvariantCulture);
        row.Truncate(start);

        Assert.Equal("a,", row.Written.ToString());
    }

    [Fact]
    public void EncodesAsUtf8()
    {
        RowText row = new();
        row.Append("Grüße 👍");
        ArrayBufferWriter<byte> output = new();

        row.WriteUtf8To(output);

        Assert.Equal(Encoding.UTF8.GetBytes("Grüße 👍"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void LetsAHugeBufferGoWhenCleared()
    {
        RowText row = new();
        row.Append(new string('x', 10_000_000));

        row.Clear();

        Assert.Equal(0, row.Length);
        Assert.True(row.Capacity <= 4 * 1024);
    }

    [Fact]
    public void ALongRowIsEncodedInPiecesWithoutSplittingACharacter()
    {
        string text = string.Concat(Enumerable.Repeat("a\u00e4\u20ac\U0001F600", 20_000));
        RowText row = new();
        row.Append(text);
        ArrayBufferWriter<byte> output = new();

        row.WriteUtf8To(output);

        Assert.Equal(Encoding.UTF8.GetBytes(text), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void ALoneSurrogateIsReplacedAsTheEncodingDoes()
    {
        RowText row = new();
        row.Append("a\uD800b");
        ArrayBufferWriter<byte> output = new();

        row.WriteUtf8To(output);

        Assert.Equal(Encoding.UTF8.GetBytes("a\uD800b"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void AppendsACharacterWhenTheBufferIsExactlyFull()
    {
        RowText row = new();
        row.Append(new string('x', 4096));
        row.Append('"');

        Assert.Equal(4097, row.Length);
        Assert.EndsWith("x\"", row.Written.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An output that gives exactly the size asked for, never more: a character whose UTF-8 is longer
    /// than one byte must still fit the space the encoder asks for, or the loop asks forever.
    /// </summary>
    [Theory]
    [InlineData("€")]
    [InlineData("a😀")]
    [InlineData("x€")]
    [InlineData("ä€😀")]
    public void EncodesIntoAnOutputThatGivesNoMoreThanItIsAskedFor(string text)
    {
        RowText row = new();
        row.Append(text);
        StingyWriter output = new();

        row.WriteUtf8To(output);

        Assert.Equal(Encoding.UTF8.GetBytes(text), output.Written);
    }

    private sealed class StingyWriter : IBufferWriter<byte>
    {
        private readonly List<byte> _written = [];
        private byte[] _span = [];
        private int _asks;

        public byte[] Written => [.. _written];

        public void Advance(int count) => _written.AddRange(_span.AsSpan(0, count).ToArray());

        public Memory<byte> GetMemory(int sizeHint = 0) => Give(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Give(sizeHint).Span;

        private Memory<byte> Give(int sizeHint)
        {
            Assert.True(++_asks < 1_000, "the encoder kept asking for room without using it");
            _span = new byte[Math.Max(sizeHint, 1)];
            return _span;
        }
    }
}
