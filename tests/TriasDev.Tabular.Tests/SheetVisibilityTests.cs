using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// A sheet says whether its author hid it, so a consumer can leave helper sheets out of a choice it
/// offers or makes (#50). Hidden sheets are still read: this is information, not a filter.
/// </summary>
public sealed class SheetVisibilityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Workbook() => new XlsxPackage()
        .WithSheet("Data", """<row r="1"><c r="A1"><v>1</v></c></row>""")
        .WithSheet("Lists", """<row r="1"><c r="A1"><v>2</v></c></row>""")
        .WithSheet("Calc", """<row r="1"><c r="A1"><v>3</v></c></row>""")
        .WithSheet("Shown", """<row r="1"><c r="A1"><v>4</v></c></row>""")
        .WithSheetStates(null, "hidden", "veryHidden", "visible")
        .Build();

    private const string OdsStyles =
        """<office:automatic-styles><style:style style:name="ta1" style:family="table"><style:table-properties table:display="true"/></style:style><style:style style:name="ta2" style:family="table"><style:table-properties table:display="false"/></style:style><style:style style:name="ce1" style:family="table-cell"><style:table-cell-properties/></style:style></office:automatic-styles>""";

    private static string OdsContent() =>
        $"""<?xml version="1.0" encoding="UTF-8"?><office:document-content {OdsPackage.Namespaces}xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" office:version="1.3">{OdsStyles}<office:body><office:spreadsheet>"""
        + """<table:table table:name="Data" table:style-name="ta1"><table:table-row><table:table-cell office:value-type="float" office:value="1"/></table:table-row></table:table>"""
        + """<table:table table:name="Lists" table:style-name="ta2"><table:table-row><table:table-cell office:value-type="float" office:value="2"/></table:table-row></table:table>"""
        + """<table:table table:name="Plain"><table:table-row><table:table-cell office:value-type="float" office:value="3"/></table:table-row></table:table>"""
        + "</office:spreadsheet></office:body></office:document-content>";

    [Fact]
    public void ReadsAWorkbookSheetsStateAsItsVisibility()
    {
        using XlsxCursor cursor = new(new MemoryStream(Workbook()), cancellationToken: Token);

        Assert.Equal(
            [SheetVisibility.Visible, SheetVisibility.Hidden, SheetVisibility.VeryHidden, SheetVisibility.Visible],
            cursor.Sheets.Select(s => s.Visibility));
    }

    [Fact]
    public void StillReadsAHiddenSheet()
    {
        using XlsxCursor cursor = new(new MemoryStream(Workbook()), cancellationToken: Token);

        Assert.True(cursor.MoveToSheet(2, Token));
        Assert.True(cursor.ReadRow(Token));
        Assert.Equal(RawCell.FromNumber(3), cursor.CurrentRow[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsAnOpenDocumentTableHiddenByItsStyle(bool utf16)
    {
        OdsPackage package = new OdsPackage().WithRawContent(OdsContent());

        if (utf16)
        {
            package = package.WithContentEncoding(new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        }

        using OdsCursor cursor = new(new MemoryStream(package.Build()), cancellationToken: Token);

        Assert.Equal(["Data", "Lists", "Plain"], cursor.Sheets.Select(s => s.Name));
        Assert.Equal([SheetVisibility.Visible, SheetVisibility.Hidden, SheetVisibility.Visible], cursor.Sheets.Select(s => s.Visibility));
    }

    [Fact]
    public void CallsACsvVisible()
    {
        using CsvCursor cursor = new(new MemoryStream("a\n1\n"u8.ToArray()), "t.csv");

        Assert.Equal(SheetVisibility.Visible, Assert.Single(cursor.Sheets).Visibility);
    }

    [Fact]
    public void CarriesTheVisibilityOfAWorkbookInsideAnArchive()
    {
        byte[] archive = new ZipArchiveBuilder().With("book.xlsx", Workbook()).Build();
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(archive), "files.zip", cancellationToken: Token);

        Assert.Equal(SheetVisibility.Hidden, cursor.Sheets.Single(s => s.Name == "Lists").Visibility);
    }

    [Fact]
    public void CarriesTheVisibilityIntoTheProfile()
    {
        using XlsxCursor cursor = new(new MemoryStream(Workbook()), cancellationToken: Token);

        FileProfile profile = TabularAnalyzer.Analyze(cursor, cancellationToken: Token);

        Assert.Equal(SheetVisibility.VeryHidden, profile.Sheets.Single(s => s.Name == "Calc").Visibility);
    }
}
