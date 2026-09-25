using System.Security;

using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Xlsx;

/// <summary>
/// A custom number format decides whether a number is a date, by its date tokens outside the parts
/// of the code that are literal text.
/// </summary>
public sealed class CustomDateFormatTests
{
    private static RawCellKind KindUnder(string formatCode)
    {
        string code = SecurityElement.Escape(formatCode);

        byte[] workbook = new XlsxPackage()
            .WithSheet("S", """<row r="1"><c r="A1" s="1"><v>45000</v></c></row>""")
            .WithStyles($"""<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="{code}"/></numFmts><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="164" applyNumberFormat="1"/></cellXfs></styleSheet>""")
            .Build();

        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(cursor.ReadRow(TestContext.Current.CancellationToken));
        return cursor.CurrentRow[0].Kind;
    }

    [Theory]
    [InlineData("yyyy-mm-dd")]
    [InlineData("dd.mm.yyyy")]
    [InlineData("d/m/yy")]
    [InlineData("[$-407]dd. mmmm yyyy")]           // a locale tag in brackets, then a date
    [InlineData("h:mm AM/PM")]
    [InlineData("[h]:mm:ss")]                      // elapsed time: the bracket is skipped, :mm:ss is not
    [InlineData("mm:ss.0")]
    [InlineData("YYYY-MM-DD")]                     // upper case, as some producers write it
    [InlineData("dd\\.mm\\.yyyy")]                 // escaped separators around real tokens
    [InlineData("[$-411]ggge\"年\"m\"月\"d\"日\"")]  // a Japanese era date: quoted CJK text between tokens
    public void ReadsAFormatWithDateTokensAsADate(string formatCode)
    {
        Assert.Equal(RawCellKind.Date, KindUnder(formatCode));
    }

    [Theory]
    [InlineData("0.00")]
    [InlineData("#,##0 \"Dm\"")]                   // a currency word in quotes, with a d in it
    [InlineData("0 \"days\"")]
    [InlineData("\\d0")]                           // an escaped d is a literal character
    [InlineData("[Red]0.00")]
    [InlineData("#,##0_);[Red](#,##0)")]
    [InlineData("_-* #,##0.00 [$€-407]_-")]        // accounting, with the currency in brackets
    [InlineData("0.00E+00")]
    [InlineData("General")]
    [InlineData("@")]
    public void ReadsAFormatWithoutDateTokensAsANumber(string formatCode)
    {
        Assert.Equal(RawCellKind.Number, KindUnder(formatCode));
    }
}
