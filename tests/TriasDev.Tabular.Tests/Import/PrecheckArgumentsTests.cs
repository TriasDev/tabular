using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// A precheck finding carries the data its message needs, named and invariant, so a UI in any
/// language can phrase it; and the names are the ones the documentation lists.
/// </summary>
public sealed partial class PrecheckArgumentsTests
{
    private static FileProfile Profile(string csv)
    {
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "t.csv");
        return TabularAnalyzer.Analyze(cursor, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static readonly ImportSchema Schema = new() { Fields = [ImportField.Text("name")] };

    [Fact]
    public void NamesTheHeaderItExpectedAndTheOneItFound()
    {
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "name", Header = "Name" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, Schema, Profile("Customer\nx\n")).Findings);

        Assert.Equal(ErrorCodes.Mapping.HeaderChanged, finding.Code);
        Assert.Equal("Name", finding.Arguments[PrecheckArguments.ExpectedHeader]);
        Assert.Equal("Customer", finding.Arguments[PrecheckArguments.ActualHeader]);
    }

    [Fact]
    public void NamesBothHeaderRowsOfAStaleProfileAndConcernsNoFieldOrColumn()
    {
        MappingPlan plan = new() { HeaderRowIndex = 1, Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "name", Header = "name" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, Schema, Profile("a\nname\nx\n")).Findings);

        Assert.Equal(ErrorCodes.Mapping.StaleProfile, finding.Code);
        Assert.Equal("0", finding.Arguments[PrecheckArguments.ProfileHeaderRowIndex]);
        Assert.Equal("1", finding.Arguments[PrecheckArguments.PlanHeaderRowIndex]);
        Assert.Null(finding.FieldName);
        Assert.Null(finding.ColumnIndex);
    }

    [Fact]
    public void NamesTheSheetIndexItDidNotFind()
    {
        MappingPlan plan = new() { SheetIndex = 3, Bindings = [] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, Schema, Profile("name\nx\n")).Findings);

        Assert.Equal(ErrorCodes.Mapping.InvalidSheet, finding.Code);
        Assert.Equal("3", finding.Arguments[PrecheckArguments.SheetIndex]);
    }

    [Fact]
    public void GivesAtMostFiveFailingValuesInOrder()
    {
        ImportSchema schema = new() { Fields = [ImportField.Text("code").ExactLength(2)] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "code", Header = "code" }] };

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, schema, Profile("code\nggg\nfff\neee\nddd\nccc\nbbb\nAA\n")).Findings);

        Assert.Equal(["bbb", "ccc", "ddd", "eee", "fff"], finding.Examples);
        Assert.Equal("6", finding.Arguments[PrecheckArguments.FailingCount]);
    }

    [Fact]
    public void DocumentsEveryArgumentAndReasonAndNothingElse()
    {
        string page = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "error-codes.md"));
        string section = page[page.IndexOf("## Precheck arguments", StringComparison.Ordinal)..];

        HashSet<string> declared = [.. Constants(typeof(PrecheckArguments)), .. Constants(typeof(PrecheckReasons))];
        HashSet<string> documented = [.. InBackticks().Matches(section).Select(m => m.Groups[1].Value)];

        Assert.Empty(declared.Except(documented));
        Assert.DoesNotContain(documented.Except(declared), IsArgumentName);
    }

    // Codes (value.required) and type names are in backticks too; argument names and reasons are not dotted.
    private static bool IsArgumentName(string value) => !value.Contains('.', StringComparison.Ordinal);

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

    [GeneratedRegex("`([a-z][a-zA-Z.-]*)`")]
    private static partial Regex InBackticks();

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TriasDev.Tabular.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
