using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// Values spelt the ways real writers spell them, found by reading the same table written by each
/// (see <c>Fixtures/Producers</c>): OOXML's own escapes, booleans in words, line ends written as they are.
/// </summary>
public sealed class ProducerSpellingsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RawCell[] FirstRow(XlsxPackage package)
    {
        using XlsxCursor cursor = new(new MemoryStream(package.Build()), cancellationToken: Token);
        Assert.True(cursor.ReadRow(Token));
        return cursor.CurrentRow.ToArray();
    }

    private static string?[] Texts(XlsxPackage package) => [.. FirstRow(package).Select(c => c.AsText())];

    [Fact]
    public void DecodesTheEscapesOoxmlUsesForCharactersInEveryKindOfString()
    {
        // ECMA-376 writes a character as _xHHHH_, and an underscore that would start one as _x005F_.
        // MiniExcel spells an emoji as its two surrogates this way; ClosedXML, EPPlus, Sylvan and
        // LibreOffice escape a typed "_x000D_" as "_x005F_x000D_", which read raw is text nobody wrote.
        const string Escaped = "a_x000D_b _x005F_x000D_ _xD83D__xDE00_ _x00e4_";
        const string Meant = "a\rb _x000D_ 😀 ä";

        string?[] row = Texts(new XlsxPackage()
            .WithSharedStrings($"<si><t>{Escaped}</t></si><si><r><t>_x00</t></r><r><t>41_</t></r></si>")
            .WithSheet("S", $"""<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="inlineStr"><is><t>{Escaped}</t></is></c><c r="C1" t="str"><f>A1</f><v>{Escaped}</v></c><c r="D1" t="s"><v>1</v></c></row>"""));

        Assert.Equal([Meant, Meant, Meant, "A"], row);
    }

    [Fact]
    public void KeepsWhatOnlyLooksLikeAnEscape()
    {
        string?[] row = Texts(new XlsxPackage()
            .WithSheet("S", """<row r="1"><c r="A1" t="inlineStr"><is><t>_x12_ _xZZZZ_ _x0041 x0041_ __x0041__</t></is></c></row>"""));

        Assert.Equal(["_x12_ _xZZZZ_ _x0041 x0041_ _A_"], row);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    public void ReadsABooleanInTheSpellingsXmlSchemaAllows(string written, bool meant)
    {
        // The Open XML SDK writes new CellValue(true) as "true", which read as anything but "1" was false.
        RawCell[] row = FirstRow(new XlsxPackage().WithSheet("S", $"""<row r="1"><c r="A1" t="b"><v>{written}</v></c></row>"""));

        Assert.Equal(RawCell.FromBoolean(meant), row[0]);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("2")]
    [InlineData("")]
    public void ReadsABooleanCellHoldingSomethingElseAsItsText(string written)
    {
        // Silently false is the worst reading of a value that is no boolean at all (#49).
        RawCell[] row = FirstRow(new XlsxPackage().WithSheet("S", $"""<row r="1"><c r="A1" t="b"><v>{written}</v></c></row>"""));

        Assert.Equal(RawCell.FromText(written), row[0]);
    }

    [Fact]
    public void ReadsLineEndsWrittenAsTheyAreAsXmlDoes()
    {
        // XML reads a carriage return, alone or before a line feed, as a line feed; only a character
        // reference keeps one. MiniExcel writes a value's line end as it is, and the shared string
        // table, read through XmlReader, was already normalised — so one value read two ways.
        string?[] row = Texts(new XlsxPackage()
            .WithSharedStrings("<si><t>s\r\nt</t></si>")
            .WithSheet("S", "<row r=\"1\"><c r=\"A1\" t=\"str\"><v>a\r\nb\rc</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>d\r\ne &amp; f\r</t><t><![CDATA[g\r\nh]]></t></is></c><c r=\"C1\" t=\"inlineStr\"><is><t>i&#13;&#10;j&#xD;k</t></is></c><c r=\"D1\" t=\"s\"><v>0</v></c></row>"));

        Assert.Equal(["a\nb\nc", "d\ne & f\ng\nh", "i\r\nj\rk", "s\nt"], row);
    }

    [Fact]
    public void ReadsLineEndsWrittenAsTheyAreInAnOpenDocumentCell()
    {
        byte[] file = new OdsPackage()
            .WithTable("S", "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>a\r\nb</text:p></table:table-cell><table:table-cell office:value-type=\"string\"><text:p>c&#13;d</text:p></table:table-cell></table:table-row>")
            .Build();
        using OdsCursor cursor = new(new MemoryStream(file), cancellationToken: Token);

        Assert.True(cursor.ReadRow(Token));
        Assert.Equal(["a\nb", "c\rd"], cursor.CurrentRow.ToArray().Select(c => c.AsText()));
    }
}
