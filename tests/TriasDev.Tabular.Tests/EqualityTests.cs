using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// Data compares by value, collections included; the schema, which carries delegates and compiled
/// patterns, compares by reference and says so by being made of classes.
/// </summary>
public sealed class EqualityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MappingPlan Plan(params string[] treatAsEmpty) => new()
    {
        Culture = "de-DE",
        Bindings =
        [
            new ColumnBinding { ColumnIndex = 0, FieldName = "a", Header = "a", TreatAsEmpty = [.. treatAsEmpty] },
            new ColumnBinding { ColumnIndex = 1, FieldName = "b", Header = "b" },
        ],
    };

    [Fact]
    public void ComparesTwoPlansBuiltAlikeAsEqual()
    {
        Assert.Equal(Plan("k.A.", "-"), Plan("k.A.", "-"));
        Assert.Equal(Plan("k.A.", "-").GetHashCode(), Plan("k.A.", "-").GetHashCode());
    }

    [Fact]
    public void TellsPlansApartByAnyBindingAndByTheirOrder()
    {
        Assert.NotEqual(Plan("k.A."), Plan("n/a"));
        Assert.NotEqual(Plan("k.A.", "-"), Plan("-", "k.A."));

        MappingPlan plan = Plan();
        Assert.NotEqual(plan, plan with { Bindings = [plan.Bindings[1], plan.Bindings[0]] });
    }

    [Fact]
    public void TellsAPlanApartFromOneWithABindingMore()
    {
        // The shorter list is a prefix of the longer: element by element they agree as far as both go.
        MappingPlan plan = Plan();
        MappingPlan shorter = plan with { Bindings = [plan.Bindings[0]] };

        Assert.NotEqual(shorter, plan);
        Assert.NotEqual(plan, shorter);
    }

    [Fact]
    public void TellsDictionariesApartByAnExtraKeyOrADifferentValue()
    {
        ImportPreviewRow row = Preview(("a", "1"));

        Assert.Equal(row, Preview(("a", "1")));
        Assert.NotEqual(row, Preview(("a", "1"), ("b", "2")));
        Assert.NotEqual(Preview(("a", "1"), ("b", "2")), row);
        Assert.NotEqual(row, Preview(("a", "2")));
        Assert.NotEqual(row, Preview(("b", "1")));
    }

    private static ImportPreviewRow Preview(params (string Field, string? Value)[] values) => new()
    {
        RowNumber = 2,
        Values = values.ToDictionary(v => v.Field, v => v.Value),
    };

    [Fact]
    public void ComparesOptionsByTheCulturesTheyList()
    {
        Assert.Equal(new AnalysisOptions { Cultures = ["de-DE", "en-US"] }, new AnalysisOptions { Cultures = ["de-DE", "en-US"] });
        Assert.NotEqual(new AnalysisOptions { Cultures = ["de-DE"] }, new AnalysisOptions { Cultures = ["en-US"] });
    }

    [Fact]
    public void ComparesTheProfilesOfOneFileReadTwiceAsEqual()
    {
        const string Csv = "id;amount;when\n1;1,50;2024-01-15\n2;2,50;n/a\n3;;2024-03-01\n";

        Assert.Equal(Analyze(Csv), Analyze(Csv));
        Assert.NotEqual(Analyze(Csv), Analyze(Csv.Replace("2,50", "9,50", StringComparison.Ordinal)));
    }

    [Fact]
    public void ComparesTheResultsOfOneCheckMadeTwiceAsEqual()
    {
        ImportSchema schema = new() { Fields = [ImportField.Text("a").Require(), ImportField.Text("b")] };
        FileProfile profile = Analyze("a;b\n;x\nq;y\n");

        Assert.Equal(MappingPrecheck.Check(Plan(), schema, profile), MappingPrecheck.Check(Plan(), schema, profile));
    }

    [Fact]
    public void MakesTheSchemaOfClassesComparedByReference()
    {
        foreach (Type type in (Type[])[typeof(ImportField), typeof(TextImportField), typeof(ImportSchema), typeof(FieldConstraint), typeof(FieldConstraint.MaxLength)])
        {
            Assert.Null(type.GetMethod("<Clone>$"));
        }

        Assert.NotEqual(ImportField.Text("a"), ImportField.Text("a"));
    }

    [Fact]
    public void KeepsTheFluentRulesCopyingRatherThanChanging()
    {
        TextImportField plain = ImportField.Text("a");
        TextImportField required = plain.Require().MaxLength(10);

        Assert.NotSame(plain, required);
        Assert.False(plain.Required);
        Assert.Empty(plain.Constraints);
        Assert.True(required.Required);
        Assert.Equal(10, Assert.IsType<FieldConstraint.MaxLength>(Assert.Single(required.Constraints)).Length);
        Assert.Equal("a", required.Name);
        Assert.Equal(ColumnType.Text, required.Type);
    }

    private static FileProfile Analyze(string csv)
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "t.csv");
        return TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
    }
}
