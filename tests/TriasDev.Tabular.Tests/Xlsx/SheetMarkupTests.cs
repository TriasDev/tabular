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
}
