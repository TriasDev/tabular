using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// Severity compares as it reads: blocked above failing rows above "cannot say".
/// </summary>
public sealed class PrecheckSeverityOrderTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void RanksBlockingAboveWarningAboveUndetermined()
    {
        // It used to be declared Warning, Blocking, Undetermined, so sorting findings by severity put
        // "don't know" above "blocked".
        Assert.True(PrecheckSeverity.Blocking > PrecheckSeverity.Warning);
        Assert.True(PrecheckSeverity.Warning > PrecheckSeverity.Undetermined);
    }

    [Fact]
    public void ListsFindingsMostSevereFirst()
    {
        string csv = "varied;short;amount\n"
            + string.Concat(Enumerable.Range(0, 60).Select(i => $"v{i};{(i < 2 ? "toolong" : "ok")};x{i}\n"));

        using MemoryStream stream = new(Utf8NoBom.GetBytes(csv), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");
        FileProfile profile = new TabularAnalyzer(new AnalysisOptions { RetainedDistinctValues = 10 })
            .Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);

        TargetSchema schema = new()
        {
            Fields =
            [
                ImportField.Text("country").AllowedValues(["DE", "AT"]),
                ImportField.Text("code").MaxLength(2),
                ImportField.Decimal("amount"),
            ],
        };

        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "varied", TargetFieldName = "country" },
                new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "short", TargetFieldName = "code" },
                new ColumnBinding { SourceColumnIndex = 2, SourceHeader = "amount", TargetFieldName = "amount" },
            ],
        };

        IReadOnlyList<PrecheckFinding> findings = MappingPrecheck.Check(plan, schema, profile).Findings;

        Assert.Equal(
            [PrecheckSeverity.Blocking, PrecheckSeverity.Warning, PrecheckSeverity.Undetermined],
            findings.Select(f => f.Severity));
    }
}
