using System.Text;

using TriasDev.Tabular.Csv;

using Xunit;

namespace TriasDev.Tabular.Tests.Extraction;

/// <summary>Each layer is called by the verb its class is named for, and a streaming read is a run.</summary>
public sealed class ExtractNamingTests
{
    [Fact]
    public void ExtractsThroughARunAsTheImporterImportsThroughOne()
    {
        TextImportField name = ImportField.Text("name");
        ImportSchema schema = new() { Fields = [name] };
        MappingPlan plan = new() { Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "name", Header = "name" }] };
        using CsvCursor cursor = new(new MemoryStream(Encoding.UTF8.GetBytes("name\nx\n")), "t.csv");

        ExtractionRun run = TabularExtractor.Extract(cursor, plan, schema, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ReadRow(TestContext.Current.CancellationToken));
        Assert.Equal("x", run.CurrentValues[0].Text);
        Assert.Equal(1, run.Summary.RowsProduced);
    }
}
