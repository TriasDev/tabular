using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>The values a precheck finding carries, named for what they hold.</summary>
public sealed class PrecheckArgumentValuesTests
{
    private static FileProfile Profile(string csv)
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "t.csv");
        return TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void NamesTheCountsAndIndicesItsArgumentsHold()
    {
        Assert.Equal("judgedCount", PrecheckArguments.JudgedCount);
        Assert.Equal("profileHeaderRowIndex", PrecheckArguments.ProfileHeaderRowIndex);
        Assert.Equal("planHeaderRowIndex", PrecheckArguments.PlanHeaderRowIndex);
        Assert.Equal("boundColumnCount", PrecheckArguments.BoundColumnCount);
    }

    [Fact]
    public void JudgesATypeMismatchAgainstEveryValueItJudged()
    {
        ImportSchema schema = new() { Fields = [ImportField.Integer("n")] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, schema, Profile("n\n1\n2\nx\n")).Findings);

        Assert.Equal("1", finding.Arguments[PrecheckArguments.FailingCount]);
        Assert.Equal("3", finding.Arguments[PrecheckArguments.JudgedCount]);
        Assert.Equal("integer", finding.Arguments[PrecheckArguments.Type]);
        Assert.Equal(string.Empty, finding.Arguments[PrecheckArguments.Culture]);
    }

    [Theory]
    [InlineData("n\nx\ny\n", PrecheckReasons.NoRowCanSatisfy)]
    [InlineData("n\n1\nx\n", PrecheckReasons.ValuesFail)]
    public void SaysWhetherNoValueOrOnlySomeFailTheType(string csv, string reason)
    {
        ImportSchema schema = new() { Fields = [ImportField.Integer("n")] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, schema, Profile(csv)).Findings);

        Assert.Equal(reason, finding.Arguments[PrecheckArguments.Reason]);
    }

    [Fact]
    public void TextNeedsNoCultureTheProfileMeasured()
    {
        // Anything reads as text, so a plan's culture outside the profiled set leaves nothing undetermined.
        ImportSchema schema = new() { Fields = [ImportField.Text("n")] };
        MappingPlan plan = new() { Culture = "fr-FR", Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };

        Assert.Empty(MappingPrecheck.Check(plan, schema, Profile("n\na\n1\n")).Findings);
    }

    [Fact]
    public void GivesAFindingWithoutExamplesAnEmptyListRatherThanNone()
    {
        ImportSchema schema = new() { Fields = [ImportField.Text("n").Require()] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, schema, Profile("n;k\na;1\n;2\n")).Findings);

        Assert.Equal(ErrorCodes.Value.Required, finding.Code);
        Assert.NotNull(finding.Examples);
        Assert.Empty(finding.Examples);
    }

    [Fact]
    public void CallsTheRepeatsOfAnExactlyCountedColumnExact()
    {
        ImportSchema schema = new() { Fields = [ImportField.Text("n").Unique()] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, schema, Profile("n\nA1\nA1\nA2\n")).Findings);

        Assert.Equal(1, finding.AffectedRows);
        Assert.Equal(RowCountBound.Exact, finding.AffectedRowsBound);
    }

    [Fact]
    public void CountsTheRowsOfEveryFailingValueOfARule()
    {
        // XX once and YY twice: three rows, not the two of the most frequent value.
        ImportSchema schema = new() { Fields = [ImportField.Text("n").AllowedValues(["DE"])] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, schema, Profile("n\nDE\nXX\nYY\nYY\n")).Findings);

        Assert.Equal(3, finding.AffectedRows);
        Assert.Equal(["XX", "YY"], finding.Examples);
    }

    [Fact]
    public void PutsTheFindingThatAffectsMoreRowsFirstWithinASeverity()
    {
        // Declared first, failing one row; declared second, failing three. Both are warnings.
        ImportSchema schema = new() { Fields = [ImportField.Text("a").AllowedValues(["DE"]), ImportField.Text("b").AllowedValues(["DE"])] };
        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "a", Header = "a" }, new ColumnBinding { ColumnIndex = 1, FieldName = "b", Header = "b" }],
        };

        IReadOnlyList<PrecheckFinding> findings = MappingPrecheck.Check(plan, schema, Profile("a;b\nDE;DE\nXX;YY\nDE;YY\nDE;YY\n")).Findings;

        Assert.Equal(["b:3", "a:1"], findings.Select(f => $"{f.FieldName}:{f.AffectedRows}"));
        Assert.All(findings, f => Assert.Equal(PrecheckSeverity.Warning, f.Severity));
    }

    [Fact]
    public void CountsTheColumnsBoundToAGroupNobodyFilled()
    {
        TranslatedImportField title = ImportField.Translated("title", ["en", "de"]).Require();
        ImportSchema schema = new() { Fields = [.. title] };
        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { ColumnIndex = 0, FieldName = "title.en", Header = "en" },
                new ColumnBinding { ColumnIndex = 1, FieldName = "title.de", Header = "de" },
                new ColumnBinding { ColumnIndex = 2, FieldName = "other", Header = "x" },
            ],
        };

        PrecheckFinding finding = Assert.Single(
            MappingPrecheck.Check(plan with { Bindings = plan.Bindings.Take(2).ToList() }, schema, Profile("en;de;x\n;;1\n;;2\n")).Findings,
            f => f.Code == ErrorCodes.Group.Required);

        Assert.Equal("2", finding.Arguments[PrecheckArguments.BoundColumnCount]);
    }

    [Fact]
    public void NamesACultureTheRuntimeLacksAndOneTheProfileWasNotMeasuredUnder()
    {
        ImportSchema schema = new() { Fields = [ImportField.Decimal("n")] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "n", Header = "n" }] };
        FileProfile profile = Profile("n\n1\n");

        PrecheckFinding unknown = Assert.Single(MappingPrecheck.Check(plan with { Culture = "xx-NOPE" }, schema, profile).Findings);
        Assert.Equal(ErrorCodes.Mapping.UnknownCulture, unknown.Code);
        Assert.Equal("xx-NOPE", unknown.Arguments[PrecheckArguments.Culture]);
        Assert.False(MappingPrecheck.Check(plan with { Culture = "xx-NOPE" }, schema, profile).CanImport);

        PrecheckFinding unprofiled = Assert.Single(MappingPrecheck.Check(plan with { Culture = "fr-FR" }, schema, profile).Findings);
        Assert.Equal(PrecheckReasons.NotProfiled, unprofiled.Arguments[PrecheckArguments.Reason]);
        Assert.Equal("fr-FR", unprofiled.Arguments[PrecheckArguments.Culture]);
    }
}
