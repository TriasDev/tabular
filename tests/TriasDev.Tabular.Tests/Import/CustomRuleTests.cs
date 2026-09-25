using System.Text;

using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Extraction;
using TriasDev.Tabular.Import;
using TriasDev.Tabular.Mapping;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// Rules a caller declares itself — a check digit, a checksum — beside the ones the library ships.
/// </summary>
public sealed class CustomRuleTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TextField Isin =
        ImportField.Text("isin").Require().Must("isin.check-digit", CheckDigits.Luhn);

    private static readonly TargetSchema Schema = new() { Fields = [Isin] };

    private static MappingPlan Plan() => new()
    {
        Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "isin", TargetFieldName = "isin" }],
    };

    private static CsvCursor Cursor(string csv) =>
        new(new MemoryStream(Utf8NoBom.GetBytes(csv), writable: false), "test.csv");

    [Fact]
    public void RefusesARowWhoseValueFailsTheRuleUnderTheCallersCode()
    {
        using CsvCursor cursor = Cursor("isin\nUS0378331005\nUS0378331006\nDE0007164600\n");
        using ImportRun<string> run = TabularImporter.Import(
            cursor,
            Plan(),
            Schema,
            row => row[Isin]!,
            cancellationToken: TestContext.Current.CancellationToken);

        List<ImportOutcome<string>> outcomes = [.. run];

        Assert.Equal(["US0378331005", "DE0007164600"], outcomes.Where(o => !o.HasErrors).Select(o => o.Value));

        ImportOutcome<string> failed = Assert.Single(outcomes, o => o.HasErrors);
        RowError error = Assert.Single(failed.Errors);

        Assert.Equal("isin.check-digit", error.Code);
        Assert.Equal(3, error.RowNumber);
        Assert.Equal("isin", error.TargetFieldName);
    }

    [Fact]
    public void JudgesTheRuleInThePrecheckAgainstTheDistinctValues()
    {
        using CsvCursor cursor = Cursor("isin\nUS0378331005\nUS0378331006\nDE0007164600\nXX0000000001\n");
        FileProfile profile = new TabularAnalyzer().Analyze(cursor, TestContext.Current.CancellationToken);

        PrecheckResult check = MappingPrecheck.Check(Plan(), Schema, profile);

        PrecheckFinding finding = Assert.Single(check.Findings, f => f.Code == "isin.check-digit");
        Assert.Equal(PrecheckSeverity.Warning, finding.Severity);
        Assert.Contains("2 of 4", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverAsksTheRuleAboutAnEmptyCell()
    {
        // An empty cell is the required check's business; a rule written for values should not have
        // to guard against there being none.
        int asked = 0;
        TextField optional = ImportField.Text("isin").Must("isin.check-digit", v =>
        {
            asked++;
            return CheckDigits.Luhn(v);
        });

        using CsvCursor cursor = Cursor("isin;other\n;x\nUS0378331005;y\n");
        using ImportRun<string?> run = TabularImporter.Import(
            cursor,
            Plan(),
            new TargetSchema { Fields = [optional] },
            row => row[optional],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.All(run, o => Assert.False(o.HasErrors));
        Assert.Equal(1, asked);
    }

    [Theory]
    [InlineData("value.check-digit")]
    [InlineData("mapping.custom")]
    [InlineData("group.custom")]
    [InlineData("structure.custom")]
    [InlineData("")]
    public void RefusesACodeThatCouldBeMistakenForOneOfTheLibrarys(string code)
    {
        // The library's catalog is a translation contract; a caller's code wearing its prefix would be
        // translated as something it is not.
        Assert.ThrowsAny<ArgumentException>(() => ImportField.Text("isin").Must(code, _ => true));
    }

    [Fact]
    public void TypesTheRuleByTheField()
    {
        IntegerField even = ImportField.Integer("n").Must("n.even", n => n % 2 == 0);
        DecimalField positive = ImportField.Decimal("amount").Must("amount.positive", d => d > 0);
        DateField notFuture = ImportField.Date("on").Must("on.not-future", d => d <= new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));

        using CsvCursor cursor = Cursor("n;amount;on\n3;-1,5;2031-01-01\n4;2,5;2024-01-01\n");
        using ImportRun<string> run = TabularImporter.Import(
            cursor,
            new MappingPlan
            {
                Culture = "de-DE",
                Bindings =
                [
                    new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "n", TargetFieldName = "n" },
                    new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "amount", TargetFieldName = "amount" },
                    new ColumnBinding { SourceColumnIndex = 2, SourceHeader = "on", TargetFieldName = "on" },
                ],
            },
            new TargetSchema { Fields = [even, positive, notFuture] },
            _ => "ok",
            cancellationToken: TestContext.Current.CancellationToken);

        List<ImportOutcome<string>> outcomes = [.. run];

        Assert.Equal(
            ["amount.positive", "n.even", "on.not-future"],
            outcomes[0].Errors.Select(e => e.Code).Order(StringComparer.Ordinal));
        Assert.False(outcomes[1].HasErrors);
    }

    [Theory]
    [InlineData("US0378331005", true)]      // an ISIN: letters count as two digits
    [InlineData("DE0007164600", true)]
    [InlineData("4539578763621486", true)]  // a card number
    [InlineData("US0378331006", false)]
    [InlineData("", false)]
    [InlineData("US03783310-5", false)]
    public void ChecksALuhnDigit(string value, bool valid) => Assert.Equal(valid, CheckDigits.Luhn(value));

    [Theory]
    [InlineData("529900T8BM49AURSDO55", true)]  // LEIs
    [InlineData("5493001KJTIIGC8Y1R12", true)]
    [InlineData("WEST12345698765432GB82", true)] // an IBAN, its first four characters moved to the end
    [InlineData("529900T8BM49AURSDO56", false)]
    [InlineData("", false)]
    public void ChecksAnIso7064Mod97Pair(string value, bool valid) => Assert.Equal(valid, CheckDigits.Mod97(value));
}
