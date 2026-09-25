using System.Text;

using TriasDev.Tabular.Analysis;
using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Import;
using TriasDev.Tabular.Mapping;

using Xunit;

namespace TriasDev.Tabular.Tests.Mapping;

/// <summary>
/// A name identifies a field, so two fields cannot answer to one. Four places built that index and
/// three let a duplicate win silently — so a schema declaring one name twice validated against one of
/// the two and extracted into the other.
/// </summary>
public sealed class SchemaFieldsTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static TargetSchema Doubled => new()
    {
        Fields = [ImportField.Text("code"), ImportField.Integer("code")],
    };

    [Fact]
    public void ValidationRefusesASchemaThatNamesAFieldTwice()
    {
        ArgumentException failure = Assert.Throws<ArgumentException>(
            () => MappingPlanValidator.Validate(Plan(), Doubled));

        Assert.Contains("'code'", failure.Message);
    }

    [Fact]
    public void ThePrecheckRefusesIt()
    {
        Assert.Throws<ArgumentException>(() => MappingPrecheck.Check(Plan(), Doubled, Profile()));
    }

    [Fact]
    public void AnImportRefusesIt()
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes("code\nA1\n"), writable: false);

        Assert.Throws<ArgumentException>(
            () => TabularImporter.Import(stream, "t.csv", Plan(), Doubled, row => row.RowNumber, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ASchemaThatNamesEachFieldOnceIsFine()
    {
        TargetSchema schema = new()
        {
            Fields = [ImportField.Text("code"), ImportField.Integer("amount")],
        };

        Assert.Empty(MappingPlanValidator.Validate(Plan(), schema));
    }

    private static MappingPlan Plan() =>
        new()
        {
            Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "code", TargetFieldName = "code" }],
        };

    private static FileProfile Profile()
    {
        using MemoryStream stream = new(Utf8NoBom.GetBytes("code\nA1\n"), writable: false);
        using CsvCursor cursor = new(stream, "t.csv");

        return new TabularAnalyzer().Analyze(cursor);
    }
}
