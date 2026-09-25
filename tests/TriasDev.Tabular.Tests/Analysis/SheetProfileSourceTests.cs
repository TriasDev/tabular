using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>
/// What describes a sheet's source is reported on the sheet, and what the profile reports stays as
/// it was when the pass ended.
/// </summary>
public sealed class SheetProfileSourceTests
{
    private static CsvCursor Csv(string text) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false), "export.csv");

    [Fact]
    public void ACsvSheetCarriesItsFormatAndDialect()
    {
        using CsvCursor cursor = Csv("a;b\n1;2\n");

        SheetProfile sheet = Assert.Single(new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken).Sheets);

        Assert.Equal(TabularFormat.Csv, sheet.Format);
        Assert.Null(sheet.Source);
        Assert.Equal(';', sheet.Dialect?.Delimiter);
    }

    [Fact]
    public void AWorkbookSheetCarriesNoDialect()
    {
        byte[] workbook = new XlsxPackage()
            .WithSheet("S1", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""")
            .Build();
        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);

        SheetProfile sheet = Assert.Single(new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken).Sheets);

        Assert.Equal(TabularFormat.Xlsx, sheet.Format);
        Assert.Null(sheet.Dialect);
        Assert.True(sheet.Diagnostics.IsClean);
    }

    [Fact]
    public void ASheetReportsItsOwnRepairsAndTheFileTheirSum()
    {
        StringBuilder text = new("ID;Note\n1;\"unterminated\n");

        for (int i = 2; i <= 40; i++)
        {
            text.Append(i).Append(";row ").Append(i).Append('\n');
        }

        using CsvCursor cursor = Csv(text.ToString());
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(profile.Sheets).Diagnostics.RecoveredUnterminatedQuotes);
        Assert.Equal(1, profile.Diagnostics.RecoveredUnterminatedQuotes);
    }

    [Fact]
    public void AStoredProfileDoesNotChangeWhenTheCursorReadsOn()
    {
        // The profile used to hand out the cursor's own object, so anything the cursor counted after
        // the pass showed up in a profile a user was mapping against.
        using CsvCursor cursor = Csv("a;b\n1;2\n");
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        cursor.Diagnostics.RecoveredUnterminatedQuotes++;

        Assert.NotSame(cursor.Diagnostics, profile.Diagnostics);
        Assert.True(profile.Diagnostics.IsClean);
        Assert.True(profile.Sheets[0].Diagnostics.IsClean);
    }
}
