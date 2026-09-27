using System.Text;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>A row dropped because nothing mapped held a value is counted, unless it was padding.</summary>
public sealed class RowsWithNothingMappedTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // -- A row the import drops is worth a number -----------------------------------------------

    [Fact]
    public void CountsRowsThatHeldSomethingJustNotInAMappedColumn()
    {
        // Padding below the data is expected and uninteresting. A row carrying a note in a column
        // nobody mapped is a record the run drops, and dropping records without a word is the worse
        // mistake — harder to notice than a number that does not add up.
        ImportSchema schema = new() { Fields = [ImportField.Text("iso")] };

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "iso", FieldName = "iso" }],
        };

        // Row 1 imports. Row 2 is padding. Row 3 carries a note in the unmapped column.
        using MemoryStream stream = new(Utf8NoBom.GetBytes("iso;note\nDE;\n;\n;see appendix\n"), writable: false);

        using ImportRun<string?> run = TabularImporter.Import(
            stream,
            "t.csv",
            plan,
            schema,
            row => row[schema.Fields[0] as TextImportField ?? throw new InvalidOperationException()], cancellationToken: TestContext.Current.CancellationToken);

        run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, run.Summary.RowsProduced);
        Assert.Equal(2, run.Summary.RowsSkipped);
        Assert.Equal(1, run.Summary.RowsWithNothingMapped);
    }

    [Fact]
    public void CountsNoSuchRowWhenEveryDroppedRowIsPadding()
    {
        ImportSchema schema = new() { Fields = [ImportField.Text("iso")] };

        MappingPlan plan = new()
        {
            Bindings = [new ColumnBinding { ColumnIndex = 0, Header = "iso", FieldName = "iso" }],
        };

        using MemoryStream stream = new(Utf8NoBom.GetBytes("iso\nDE\n\n\n"), writable: false);

        using ImportRun<string?> run = TabularImporter.Import(
            stream,
            "t.csv",
            plan,
            schema,
            row => row[schema.Fields[0] as TextImportField ?? throw new InvalidOperationException()], cancellationToken: TestContext.Current.CancellationToken);

        run.ReadAll(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, run.Summary.RowsWithNothingMapped);
    }
}
