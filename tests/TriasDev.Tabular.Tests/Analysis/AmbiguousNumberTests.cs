using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// A column of three-digit decimals — <c>48.137</c> — reads completely both as decimals and as German
/// grouped integers. The reading the evidence favours goes first, the extremes are measured under it,
/// and the facts say the readings disagree (#61).
/// </summary>
/// <remarks>
/// The evidence, in order: the sheet's other numeric columns, whose separator is not in doubt; then a
/// csv's delimiter, as a comma-delimited file cannot use an unquoted comma for decimals and a
/// semicolon-delimited one is German as a rule; and, with nothing to go on, the decimal over the
/// grouped integer, one group being weak evidence of grouping.
/// </remarks>
public sealed class AmbiguousNumberTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly AnalysisOptions Cultures = new() { Cultures = ["", "de-DE", "en-US"] };

    private static SheetProfile Csv(string text)
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(text)), "t.csv");
        return TabularAnalyzer.Analyze(cursor, Cultures, cancellationToken: Token).Sheets[0];
    }

    private static string CultureOf(TypeHypothesis hypothesis) => hypothesis.Culture ?? "(none)";

    [Fact]
    public void ReadsThreeDigitDecimalsInACommaDelimitedFileAsDecimals()
    {
        ColumnProfile lat = Csv("id,lat,lon\n1,48.137,11.575\n2,52.520,13.405\n").Columns[1];

        Assert.Equal(ColumnType.Decimal, lat.Hypotheses[0].Type);
        Assert.NotEqual("de-DE", CultureOf(lat.Hypotheses[0]));
        Assert.Equal(48.137m, lat.Facts.MinNumeric);
        Assert.Equal(52.52m, lat.Facts.MaxNumeric);
        Assert.True(lat.Facts.NumberReadingsDisagree);
    }

    [Fact]
    public void ReadsThemAsGermanInASemicolonDelimitedFile()
    {
        ColumnProfile amount = Csv("id;amount\n1;1.250\n2;3.500\n").Columns[1];

        Assert.Equal("de-DE", CultureOf(amount.Hypotheses[0]));
        Assert.Equal(1250m, amount.Facts.MinNumeric);
        Assert.Equal(3500m, amount.Facts.MaxNumeric);
        Assert.True(amount.Facts.NumberReadingsDisagree);
    }

    [Fact]
    public void FollowsTheSeparatorTheSheetsOtherColumnsLeaveNoDoubtAbout()
    {
        // Tab-delimited, so the delimiter says nothing; the unambiguous column says comma.
        ColumnProfile amount = Csv("price\tamount\n1,5\t1.250\n2,75\t3.500\n").Columns[1];

        Assert.Equal("de-DE", CultureOf(amount.Hypotheses[0]));
        Assert.Equal(1250m, amount.Facts.MinNumeric);

        ColumnProfile other = Csv("price\tamount\n1.5\t1.250\n2.75\t3.500\n").Columns[1];

        Assert.NotEqual("de-DE", CultureOf(other.Hypotheses[0]));
        Assert.Equal(1.25m, other.Facts.MinNumeric);
    }

    [Fact]
    public void PrefersTheDecimalWhenNothingElseDecides()
    {
        string rows = """<row r="1"><c r="A1" t="inlineStr"><is><t>lat</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>48.137</t></is></c></row><row r="3"><c r="A3" t="inlineStr"><is><t>52.520</t></is></c></row>""";
        using XlsxCursor cursor = new(new MemoryStream(new XlsxPackage().WithSheet("S", rows).Build()), cancellationToken: Token);

        ColumnProfile lat = TabularAnalyzer.Analyze(cursor, Cultures, cancellationToken: Token).Sheets[0].Columns[0];

        Assert.Equal(ColumnType.Decimal, lat.Hypotheses[0].Type);
        Assert.NotEqual("de-DE", CultureOf(lat.Hypotheses[0]));
        Assert.Equal(48.137m, lat.Facts.MinNumeric);
    }

    [Fact]
    public void ChangesNothingWhereTheReadingsAgree()
    {
        SheetProfile sheet = Csv("id;n\n1;42\n2;7\n");

        Assert.False(sheet.Columns[1].Facts.NumberReadingsDisagree);
        Assert.Equal(ColumnType.Integer, sheet.Columns[1].Hypotheses[0].Type);
        Assert.Equal("", CultureOf(sheet.Columns[1].Hypotheses[0]));
    }

    [Fact]
    public void SaysNothingWhenOnlyOneReadingTakesEveryValue()
    {
        ColumnProfile column = Csv("id;n\n1;1,5\n2;2,25\n").Columns[1];

        Assert.False(column.Facts.NumberReadingsDisagree);
    }
}
