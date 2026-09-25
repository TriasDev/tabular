using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Every sheet says what kind of source it came from, and from where — the facts an archive holding
/// several files will need per sheet.
/// </summary>
public sealed class SheetSourceTests
{
    [Fact]
    public void ACsvSheetIsCsvAndHasNoSource()
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("a;b\n1;2\n")), "t.csv");

        SheetInfo sheet = Assert.Single(cursor.Sheets);

        Assert.Equal((TabularFormat.Csv, (string?)null), (sheet.Format, sheet.Source));
    }

    [Fact]
    public void AWorkbookSheetIsXlsxAndHasNoSource()
    {
        byte[] workbook = new XlsxPackage()
            .WithSheet("S1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""")
            .WithSheet("S2", """<row r="1"><c r="A1" t="inlineStr"><is><t>b</t></is></c></row>""")
            .Build();

        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(cursor.Sheets, s => Assert.Equal((TabularFormat.Xlsx, (string?)null), (s.Format, s.Source)));
    }
}
