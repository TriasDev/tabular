using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>A row error, a mapping fault and a precheck finding share one shape a UI can show alike.</summary>
public sealed class ProblemShapeTests
{
    private static string Describe(ITabularProblem problem) => $"{problem.Code}|{problem.FieldName}|{problem.ColumnIndex}";

    private static readonly TextImportField Name = ImportField.Text("name").Require();

    private static readonly TextImportField Other = ImportField.Text("other");

    private static readonly ImportSchema Schema = new() { Fields = [Name, Other] };

    private static MemoryStream Csv() => new(Encoding.UTF8.GetBytes("name;other\n;y\nx;z\n"), writable: false);

    [Fact]
    public void DescribesEveryKindOfProblemThroughOneInterface()
    {
        MappingPlan plan = new()
        {
            Bindings =
            [
                new ColumnBinding { ColumnIndex = 0, FieldName = "name", Header = "name" },
                new ColumnBinding { ColumnIndex = 1, FieldName = "other", Header = "other" },
            ],
        };

        using ImportRun<string?> run = TabularImporter.Import(Csv(), "t.csv", plan, Schema, row => row[Name], cancellationToken: TestContext.Current.CancellationToken);
        RowError error = Assert.Single(run.ReadAll(cancellationToken: TestContext.Current.CancellationToken).Errors);
        Assert.Equal("value.required|name|0", Describe(error));

        MappingFault fault = Assert.Single(MappingPlanValidator.Validate(
            plan with { Bindings = [plan.Bindings[0], new ColumnBinding { ColumnIndex = 1, FieldName = "unknown", Header = "other" }] }, Schema));
        Assert.Equal($"{fault.Code}|unknown|1", Describe(fault));

        FileProfile profile;

        using (ITabularCursor cursor = TabularFile.Open(Csv(), "t.csv", cancellationToken: TestContext.Current.CancellationToken))
        {
            profile = TabularAnalyzer.Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);
        }

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, Schema, profile).Findings, f => f.Code == "value.required");
        Assert.Equal("value.required|name|0", Describe(finding));
    }

    [Fact]
    public void BuildsTypedFieldsFromTheFieldTypeItself()
    {
        ImportField name = ImportField.Text("name");

        Assert.IsType<TextImportField>(name);
        Assert.IsType<DateImportField>(ImportField.Date("d"));
        Assert.Equal(ColumnType.Integer, new ImportField { Name = "n", Type = ColumnType.Integer }.Type);
    }
}
