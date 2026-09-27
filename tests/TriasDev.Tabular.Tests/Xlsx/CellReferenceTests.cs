using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>A cell reference that cannot name a cell is refused, not wrapped into one.</summary>
public sealed class CellReferenceTests
{

    // -- A malformed reference is refused, not placed --------------------------------------------

    [Fact]
    public void RefusesACellReferenceThatOverflowsInsteadOfPlacingIt()
    {
        // The accumulation wrapped to a negative number, which passed the width guard and was then
        // normalised into the next column: a malformed reference silently accepted as data.
        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c r="BBBBBBBBBBBBB1" t="inlineStr"><is><t>x</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(stream);

        Assert.Throws<TabularFormatException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SaysNoNumberForAReferenceTooLongToMeanOne()
    {
        // It used to report column 16385 for a reference naming column 321,272,406 — the ceiling
        // presented as if it were the answer.
        byte[] package = new XlsxPackage()
            .WithSheet("Sheet1", """<row><c r="ZZZZZZ1" t="inlineStr"><is><t>x</t></is></c></row>""")
            .Build();

        using MemoryStream stream = new(package, writable: false);
        using XlsxCursor cursor = new(stream);

        TabularFormatException failure = Assert.Throws<TabularFormatException>(() => cursor.ReadRow(TestContext.Current.CancellationToken));

        Assert.DoesNotContain("16385", failure.Message);
        Assert.Contains("beyond", failure.Message);
    }
}
