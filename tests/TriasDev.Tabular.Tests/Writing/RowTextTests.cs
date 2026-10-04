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
}
