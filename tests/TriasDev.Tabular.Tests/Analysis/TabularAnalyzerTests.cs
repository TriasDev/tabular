using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Analysis;

/// <summary>Pins how a whole file turns into a profile.</summary>
public sealed class TabularAnalyzerTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static FileProfile AnalyzeCsv(string text, AnalysisOptions? options = null)
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes(text), writable: false);
        using CsvCursor cursor = new(stream, "export.csv");

        return new TabularAnalyzer(options).Analyze(cursor);
    }

    private static FileProfile AnalyzeXlsx(byte[] content)
    {
        using MemoryStream stream = new(content, writable: false);
        using XlsxCursor cursor = new(stream);

        return new TabularAnalyzer().Analyze(cursor);
    }

    [Fact]
    public void TakesTheFirstRowAsHeadersAndTheRestAsData()
    {
        FileProfile profile = AnalyzeCsv("Name;Land\nMüller;DEU\nWeiß;AUT\n");

        SheetProfile sheet = Assert.Single(profile.Sheets);

        Assert.Equal(2, sheet.RowCount);
        Assert.Equal(["Name", "Land"], sheet.Columns.Select(c => c.Facts.Header));
        Assert.Equal(2, sheet.Columns[1].Facts.NonEmptyCount);
    }

    [Fact]
    public void NamesACsvsSingleSheetAfterTheFileAndReportsItsDialect()
    {
        FileProfile profile = AnalyzeCsv("a;b\n1;2\n");

        Assert.Equal(TabularFormat.Csv, profile.Format);
        Assert.Equal("export.csv", Assert.Single(profile.Sheets).Name);
        Assert.NotNull(profile.Dialect);
        Assert.Equal(';', profile.Dialect.Delimiter);
        Assert.Equal(DialectSource.Detected, profile.Dialect.DelimiterSource);
    }

    [Fact]
    public void KeepsAColumnWhoseHeaderCellIsEmpty()
    {
        FileProfile profile = AnalyzeCsv("Name;;Land\nMüller;x;DEU\n");

        SheetProfile sheet = Assert.Single(profile.Sheets);

        Assert.Equal(3, sheet.Columns.Count);
        Assert.Equal(string.Empty, sheet.Columns[1].Facts.Header);
        Assert.Equal(1, sheet.Columns[1].Facts.Index);
    }

    [Fact]
    public void KeepsAColumnThatOnlyAppearsBelowTheHeader()
    {
        // The header names two columns and the data carries three. The third is still data.
        FileProfile profile = AnalyzeCsv("a;b\n1;2;3\n");

        SheetProfile sheet = Assert.Single(profile.Sheets);

        Assert.Equal(3, sheet.Columns.Count);
        Assert.Equal(string.Empty, sheet.Columns[2].Facts.Header);
        Assert.Equal(1, sheet.Columns[2].Facts.NonEmptyCount);
    }

    [Fact]
    public void CountsAMissingCellAsEmptyRatherThanAsAbsent()
    {
        FileProfile profile = AnalyzeCsv("a;b;c\n1;2\n3;4;5\n");

        SheetProfile sheet = Assert.Single(profile.Sheets);

        Assert.Equal(1, sheet.Columns[2].Facts.EmptyCount);
        Assert.Equal(1, sheet.Columns[2].Facts.NonEmptyCount);
    }

    [Fact]
    public void ProfilesEverySheetOfAWorkbook()
    {
        byte[] content = new XlsxPackage()
            .WithSheet("First", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>x</t></is></c></row>""")
            .WithSheet("Second", """<row r="1"><c r="A1" t="inlineStr"><is><t>b</t></is></c></row><row r="2"><c r="A2" t="inlineStr"><is><t>y</t></is></c></row><row r="3"><c r="A3" t="inlineStr"><is><t>z</t></is></c></row>""")
            .Build();

        FileProfile profile = AnalyzeXlsx(content);

        Assert.Equal(TabularFormat.Xlsx, profile.Format);
        Assert.Null(profile.Dialect);
        Assert.Equal(2, profile.Sheets.Count);
        Assert.Equal("First", profile.Sheets[0].Name);
        Assert.Equal(1, profile.Sheets[0].RowCount);
        Assert.Equal(2, profile.Sheets[1].RowCount);
    }

    [Fact]
    public void CarriesTheHypothesesBesideTheFactsForEveryColumn()
    {
        FileProfile profile = AnalyzeCsv("ID;Betrag;Land\n1,00;1.234,56;DEU\n2,00;2.000,00;AUT\n");

        SheetProfile sheet = Assert.Single(profile.Sheets);

        // The amount column is unambiguously German; the identifier column is not, and its facts are
        // what say what it is.
        Assert.Equal("de-DE", sheet.Columns[1].Hypotheses[0].Culture);
        Assert.Equal(ColumnType.Decimal, sheet.Columns[1].Hypotheses[0].Type);
        Assert.True(sheet.Columns[0].Facts.IsUnique);
        Assert.Equal(ColumnType.Text, Assert.Single(sheet.Columns[2].Hypotheses).Type);
    }

    [Fact]
    public void ReportsWhatTheReaderHadToRepair()
    {
        StringBuilder text = new("ID;Note\n1;\"unterminated\n");

        for (int i = 2; i <= 40; i++)
        {
            text.Append(i).Append(";row ").Append(i).Append('\n');
        }

        FileProfile profile = AnalyzeCsv(text.ToString());

        Assert.Equal(1, profile.Diagnostics.RecoveredUnterminatedQuotes);
        Assert.False(profile.Diagnostics.IsClean);
    }

    [Fact]
    public void ReportsACleanFileAsClean()
    {
        FileProfile profile = AnalyzeCsv("a;b\n1;2\n");

        Assert.True(profile.Diagnostics.IsClean);
    }

    [Fact]
    public void ReadsEveryRowRatherThanASample()
    {
        // The requirement the whole design rests on. One violating value in the fifty-thousandth row
        // is the answer to "is this column always three characters".
        StringBuilder text = new("Land\n");

        for (int i = 1; i <= 50_000; i++)
        {
            text.Append(i == 49_999 ? "DEUX" : "DEU").Append('\n');
        }

        FileProfile profile = AnalyzeCsv(text.ToString());

        ColumnFacts facts = Assert.Single(Assert.Single(profile.Sheets).Columns).Facts;

        Assert.Equal(50_000, facts.NonEmptyCount);
        Assert.Equal(3, facts.MinLength);
        Assert.Equal(4, facts.MaxLength);
    }

    [Fact]
    public void StopsWhenAsked()
    {
        StringBuilder text = new("a\n");

        for (int i = 1; i <= 100_000; i++)
        {
            text.Append(i).Append('\n');
        }

        using MemoryStream stream = new(Utf8NoBom.GetBytes(text.ToString()), writable: false);
        using CsvCursor cursor = new(stream, "big.csv");
        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new TabularAnalyzer().Analyze(cursor, cancellation.Token));
    }
}
