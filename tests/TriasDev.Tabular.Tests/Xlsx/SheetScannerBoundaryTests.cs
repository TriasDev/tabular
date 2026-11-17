using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// The scanner at the end of what it has buffered: a text node that stops where the buffer stops
/// cannot yet say what follows it, and must not guess that nothing does.
/// </summary>
/// <remarks>
/// A worksheet part arrives in 64 KB reads, so the boundary falls inside a value only on large
/// files, at a place no fixture chooses. A reader that hands over one character per call puts it
/// after every character instead.
/// </remarks>
public sealed class SheetScannerBoundaryTests
{
    /// <summary>A reader that never gives more than one character per call.</summary>
    private sealed class OneCharReader(string text) : TextReader
    {
        private int _position;

        public override int Peek() => _position < text.Length ? text[_position] : -1;

        public override int Read() => _position < text.Length ? text[_position++] : -1;

        public override int Read(char[] buffer, int index, int count)
        {
            if (count == 0 || _position >= text.Length)
            {
                return 0;
            }

            buffer[index] = text[_position++];
            return 1;
        }
    }

    /// <summary>The scanner standing on the text node right after <c>&lt;v&gt;</c>.</summary>
    private static SheetScanner OnValue(TextReader reader)
    {
        SheetScanner scanner = new(reader);

        Assert.True(scanner.Read());
        Assert.Equal("v", scanner.Name.ToString());
        Assert.True(scanner.Read());
        Assert.Equal(XmlNodeKind.Text, scanner.Kind);
        Assert.Equal("4", scanner.Value.ToString());

        return scanner;
    }

    [Theory]
    [InlineData("<!-- note -->")]
    [InlineData("<![CDATA[2]]>")]
    [InlineData("<?pi x?>")]
    public void SaysATextNodeMayGoOnWhenTheBufferEndsAtTheMarkupAfterIt(string markup)
    {
        // The buffer ends on the '<', so the character that tells a tag from a comment, CDATA or an
        // instruction has not arrived. Answering no read "4<!-- -->2" as 4.
        using SheetScanner scanner = OnValue(new OneCharReader($"<v>4{markup}2</v>"));

        Assert.True(scanner.MayContinueText);
    }

    [Theory]
    [InlineData("<!-- note -->")]
    [InlineData("<![CDATA[2]]>")]
    [InlineData("<?pi x?>")]
    public void SaysATextNodeMayGoOnWhenMarkupThatIsNotATagFollowsIt(string markup)
    {
        using SheetScanner scanner = OnValue(new StringReader($"<v>4{markup}2</v>"));

        Assert.True(scanner.MayContinueText);
    }

    [Fact]
    public void SaysATextNodeEndsWhenItsClosingTagIsBuffered()
    {
        // The ordinary path: one look, no copy.
        using SheetScanner scanner = OnValue(new StringReader("<v>4</v>"));

        Assert.False(scanner.MayContinueText);
    }
}
