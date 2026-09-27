using System.Text;

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
}
