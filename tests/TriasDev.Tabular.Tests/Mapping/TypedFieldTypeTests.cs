using System.Text;

using TriasDev.Tabular.Tests.Writing;

using Xunit;

namespace TriasDev.Tabular.Tests.Mapping;

/// <summary>A typed field reads values as its class says, so a declared Type that disagrees is refused.</summary>
public sealed class TypedFieldTypeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RefusesATypedFieldDeclaredWithAnotherType()
    {
        DateImportField misdeclared = new() { Name = "d", Type = ColumnType.Text };
        ImportSchema schema = new() { Fields = [misdeclared] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "d", Header = "d" }] };

        ArgumentException error = Assert.Throws<ArgumentException>(() => TabularImporter.Import(
            new MemoryStream(Encoding.UTF8.GetBytes("d\nhello\n")), "t.csv", plan, schema, row => row[misdeclared], cancellationToken: Token));

        Assert.Contains("d", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Each typed field's declarations, by the field's kind, with a value of that kind spelt in csv.</summary>
    private static ImportField Declared(string kind, string declaration) => (kind, declaration) switch
    {
        ("text", "require") => ImportField.Text("v").Require(),
        ("text", "unique") => ImportField.Text("v").Unique(),
        ("integer", "require") => ImportField.Integer("v").Require(),
        ("integer", "unique") => ImportField.Integer("v").Unique(),
        ("decimal", "require") => ImportField.Decimal("v").Require(),
        ("decimal", "unique") => ImportField.Decimal("v").Unique(),
        ("date", "require") => ImportField.Date("v").Require(),
        ("date", "unique") => ImportField.Date("v").Unique(),
        ("boolean", "require") => ImportField.Boolean("v").Require(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Spelt(string kind) => kind switch
    {
        "text" => "a",
        "integer" => "5",
        "decimal" => "1.5",
        "date" => "2026-01-02",
        _ => "true",
    };

    private static string Other(string kind) => kind switch
    {
        "text" => "b",
        "integer" => "6",
        "decimal" => "2.5",
        _ => "2026-01-03",
    };

    /// <summary>
    /// <c>Require()</c> on every typed field: a row without the value fails with <c>value.required</c>
    /// and nothing else — a declaration that set another flag would import the empty cell as valid.
    /// </summary>
    [Theory]
    [InlineData("text")]
    [InlineData("integer")]
    [InlineData("decimal")]
    [InlineData("date")]
    [InlineData("boolean")]
    public void ARequiredTypedFieldRefusesARowWithoutIt(string kind)
    {
        ImportField field = Declared(kind, "require");
        byte[] file = Encoding.UTF8.GetBytes($"v,k\n{Spelt(kind)},1\n{Spelt(kind)},2\n,3\n");

        (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, "", [field, ImportField.Text("k")], Token);

        Assert.True(field.Required);
        Assert.False(field.MustBeUnique);
        Assert.Equal(3, rows.Count);
        Assert.Equal([ErrorCodes.Value.Required], errors);
    }

    /// <summary>
    /// <c>Unique()</c> on every typed field that has it: whether a value repeats is a property of the
    /// column, so the precheck reports it (<c>value.not-unique</c>, blocking) and the column without a
    /// repeat passes.
    /// </summary>
    [Theory]
    [InlineData("text")]
    [InlineData("integer")]
    [InlineData("decimal")]
    [InlineData("date")]
    public void AUniqueTypedFieldIsRefusedByThePrecheckForARepeatedValue(string kind)
    {
        ImportField field = Declared(kind, "unique");
        ImportSchema schema = new() { Fields = [field, ImportField.Text("k")] };
        MappingPlan plan = new()
        {
            Culture = "",
            Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "v", FieldName = "v" }, new ColumnBinding { ColumnIndex = 1, Header = "k", FieldName = "k" }],
        };

        Assert.True(field.MustBeUnique);
        Assert.False(field.Required);

        PrecheckFinding finding = Assert.Single(MappingPrecheck.Check(plan, schema, Profile($"v,k\n{Spelt(kind)},1\n{Spelt(kind)},2\n")).Findings);
        Assert.Equal((ErrorCodes.Value.NotUnique, "v", PrecheckSeverity.Blocking), (finding.Code, finding.FieldName, finding.Severity));

        Assert.Empty(MappingPrecheck.Check(plan, schema, Profile($"v,k\n{Spelt(kind)},1\n{Other(kind)},2\n")).Findings);
    }

    private static FileProfile Profile(string csv)
    {
        using ITabularCursor cursor = TabularFile.Open(new MemoryStream(Encoding.UTF8.GetBytes(csv), writable: false), "t.csv", cancellationToken: Token);
        return TabularAnalyzer.Analyze(cursor, cancellationToken: Token);
    }

    [Fact]
    public void ATextShorterThanItsMinimumFailsWithItsOwnCode()
    {
        TextImportField code = ImportField.Text("code").MinLength(3);
        byte[] file = Encoding.UTF8.GetBytes("code\nabc\nab\n");

        (List<object?[]> rows, List<string> errors, _) = CsvRoundTrip.Import(file, "", [code], Token);

        Assert.Equal(["abc"], rows[0]);
        Assert.Equal([ErrorCodes.Value.MinLength], errors);
    }
}
