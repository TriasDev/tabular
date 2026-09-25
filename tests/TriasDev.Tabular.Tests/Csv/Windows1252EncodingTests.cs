using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Csv;

/// <summary>
/// The fallback decoder agrees with the platform's code page 1252 on every assigned byte, and says
/// "unassigned" where the specification does.
/// </summary>
public sealed class Windows1252EncodingTests
{
    private static readonly byte[] Unassigned = [0x81, 0x8D, 0x8F, 0x90, 0x9D];

    [Fact]
    public void DecodesEveryAssignedByteAsThePlatformDoes()
    {
        // The platform's table is the oracle. It is not the library's decoder because it lives behind
        // an encoding provider a caller must register — see the class's own remarks.
        Encoding oracle = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

        for (int b = 0; b <= 0xFF; b++)
        {
            if (Unassigned.Contains((byte)b))
            {
                continue;
            }

            byte[] one = [(byte)b];

            Assert.True(
                oracle.GetString(one) == Windows1252Encoding.Instance.GetString(one),
                $"byte 0x{b:X2}: expected U+{(int)oracle.GetString(one)[0]:X4}, got U+{(int)Windows1252Encoding.Instance.GetString(one)[0]:X4}");
        }
    }

    [Fact]
    public void DecodesTheUnassignedBytesAsTheReplacementCharacter()
    {
        Assert.All(Unassigned, b => Assert.Equal("�", Windows1252Encoding.Instance.GetString([b])));
    }

    [Fact]
    public void ReadsTheHighRangeThroughTheCsvFallback()
    {
        // € Š œ Ÿ, then ü — which makes the file invalid UTF-8 and forces the fallback.
        byte[] content = [0x80, 0x8A, 0x9C, 0x9F, 0xFC, (byte)';', (byte)'x', (byte)'\n'];

        using CsvCursor cursor = new(new MemoryStream(content, writable: false), "t.csv");

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("windows-1252", cursor.Dialect.Encoding.WebName);
        Assert.Equal("€ŠœŸü", cursor.CurrentRow[0].AsText());
    }
}
