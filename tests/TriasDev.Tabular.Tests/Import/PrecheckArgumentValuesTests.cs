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

        PrecheckFinding unprofiled = Assert.Single(MappingPrecheck.Check(plan with { Culture = "fr-FR" }, schema, profile).Findings);
        Assert.Equal(PrecheckReasons.NotProfiled, unprofiled.Arguments[PrecheckArguments.Reason]);
        Assert.Equal("fr-FR", unprofiled.Arguments[PrecheckArguments.Culture]);
    }
}
