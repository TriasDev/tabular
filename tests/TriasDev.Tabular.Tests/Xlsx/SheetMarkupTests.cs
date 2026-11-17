using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// XML the sheet scanner must read past or read through: comments, processing instructions, CDATA.
/// </summary>
public sealed class SheetMarkupTests
{
    private static string FirstRow(string rowsXml)
    {
        byte[] workbook = new XlsxPackage().WithSheet("S", rowsXml).Build();
        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        return string.Join("|", cursor.CurrentRow.ToArray().Select(c => c.AsText()));
    }

    [Fact]
    public void ReadsPastCommentsBetweenCells()
    {
        string row = FirstRow("""<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c><!-- <c r="B1"><v>9</v></c> --><c r="B1" t="inlineStr"><is><t>b</t></is></c></row>""");

        Assert.Equal("a|b", row);
    }

    [Fact]
    public void ReadsPastACommentInsideAValue()
    {
        string row = FirstRow("""<row r="1"><c r="A1"><v>4<!-- no -->2</v></c></row>""");

        Assert.Equal("42", row);
    }

    [Fact]
    public void ReadsCdataAsTheTextItHolds()
    {
        string row = FirstRow("""<row r="1"><c r="A1" t="inlineStr"><is><t><![CDATA[a<b & "c"]]></t></is></c></row>""");

        Assert.Equal("a<b & \"c\"", row);
    }

    [Fact]
    public void ReadsAValueWhoseCdataIsFollowedByText()
    {
        string row = FirstRow("""<row r="1"><c r="A1" t="str"><f>A2</f><v><![CDATA[a ]]>7<![CDATA[&]]>b</v></c><c r="B1"><v><![CDATA[4]]>2</v></c></row>""");

        Assert.Equal("a 7&b|42", row);
    }

    [Fact]
    public void ReadsPastAProcessingInstructionBetweenRows()
    {
        byte[] workbook = new XlsxPackage()
            .WithSheet("S", """<row r="1"><c r="A1"><v>1</v></c></row><?producer note?><row r="2"><c r="A2"><v>2</v></c></row>""")
            .Build();
        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

        int rows = 0;

        while (cursor.ReadRow(TestContext.Current.CancellationToken))
        {
            rows++;
        }

        Assert.Equal(2, rows);
    }

    [Fact]
    public void ReadsCdataWithLineEndsAndWhatLooksLikeAnEntityAsItsText()
    {
        // Its line ends are read as XML reads them; nothing inside it is an entity, so "&amp;" is
        // five characters of text, not an ampersand.
        string row = FirstRow("<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t><![CDATA[a&amp;b\r\nc\rd&#65;]]></t></is></c></row>");

        Assert.Equal("a&amp;b\nc\nd&#65;", row);
    }

    private const string SheetOpen =
        """<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""";

    private const string TwoRows =
        """<row r="1"><c r="A1"><v>1</v></c></row><row r="2"><c r="A2"><v>2</v></c></row>""";

    /// <summary>How many rows a raw worksheet part reads to, under a deadline: the scanner checks no token.</summary>
    private static int RowsOf(string worksheetXml) =>
        Deadline.Within(() =>
        {
            byte[] workbook = new XlsxPackage().WithRawSheet("S", worksheetXml).Build();
            using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

            int rows = 0;

            while (cursor.ReadRow(TestContext.Current.CancellationToken))
            {
                rows++;
            }

            return rows;
        });

    [Fact]
    public void ReadsPastADocumentTypeDeclaration()
    {
        // A declaration the scanner neither needs nor resolves: skipped to its '>', like a comment.
        Assert.Equal(
            2,
            RowsOf("""<?xml version="1.0"?><!DOCTYPE worksheet><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""" + TwoRows + "</sheetData></worksheet>"));
    }

    [Theory]
    [InlineData("<!-- cut off")]
    [InlineData("<!-- cut off -")]
    [InlineData("<![CDATA[cut off")]
    [InlineData("<![CDATA[cut off]]")]
    [InlineData("<!DOCTYPE cut off")]
    [InlineData("<?pi cut off")]
    [InlineData("""<row r="3"><c r="A3" t="str"><v><![CDATA[cut off""")]
    [InlineData("""<row r="3"><c r="A3" t="str"><v>a<!-- cut off""")]
    public void RefusesAWorksheetCutOffInsideMarkupThatIsNotATag(string tail)
    {
        // The part ends inside a comment, a CDATA section, a declaration or an instruction. That is a
        // truncated file, not a clean end of sheet — and not a reason to look for the end forever.
        TabularFormatException cut = Assert.Throws<TabularFormatException>(() => RowsOf(SheetOpen + TwoRows + tail));

        Assert.Equal(TabularFormatException.Truncated, cut.Code);
    }
}
