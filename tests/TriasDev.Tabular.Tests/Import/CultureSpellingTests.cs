using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// The invariant culture has one spelling — the empty string — in the profile and in the plan.
/// </summary>
public sealed class CultureSpellingTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void AcceptsThePlanBuiltFromTheTopHypothesis()
    {
        // The obvious thing a UI does: take the best hypothesis's culture and put it in the plan. The
        // profile used to say "invariant" and the plan to expect "", so that plan was refused.
        using MemoryStream stream = new(Utf8NoBom.GetBytes("amount;x\n1.5;a\n2.25;b\n3;c\n"), writable: false);
        using CsvCursor cursor = new(stream, "test.csv");

        FileProfile profile = new TabularAnalyzer(new AnalysisOptions { Cultures = [""] }).Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);
        TypeHypothesis top = profile.Sheets[0].Columns[0].Hypotheses[0];

        Assert.Equal(ColumnType.Decimal, top.Type);
        Assert.Equal(string.Empty, top.Culture);

        MappingPlan plan = new()
        {
            Culture = top.Culture,
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "amount", TargetFieldName = "amount" }],
        };
        TargetSchema schema = new() { Fields = [ImportField.Decimal("amount")] };

        Assert.Empty(MappingPlanValidator.Validate(plan, schema));

        PrecheckResult result = MappingPrecheck.Check(plan, schema, profile);

        Assert.Empty(result.Findings);
        Assert.True(result.CanImport);
    }
}
