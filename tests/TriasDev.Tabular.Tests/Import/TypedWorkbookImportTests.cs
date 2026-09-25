using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// A workbook's typed cells go through the import as the types the file declared, not as text read
/// back under a culture.
/// </summary>
/// <remarks>
/// Every other importer test feeds a csv, whose cells are all text, so the branches that take a
/// workbook's own number, date and boolean were never run end to end.
/// </remarks>
public sealed class TypedWorkbookImportTests
{
    private static readonly IntegerField Count = ImportField.Integer("count");
    private static readonly DecimalField Amount = ImportField.Decimal("amount");
    private static readonly DateField Signed = ImportField.Date("signed");
    private static readonly BooleanField Active = ImportField.Boolean("active");
    private static readonly TextField Note = ImportField.Text("note");

    private static readonly TargetSchema Schema = new() { Fields = [Count, Amount, Signed, Active, Note] };

    private static readonly MappingPlan Plan = new()
    {
        // Under de-DE on purpose: a typed cell must not be read through the culture at all, so a
        // decimal point in the file's own number cannot be mistaken for a group separator.
        Culture = "de-DE",
        Bindings =
        [
            new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "count", TargetFieldName = "count" },
            new ColumnBinding { SourceColumnIndex = 1, SourceHeader = "amount", TargetFieldName = "amount" },
            new ColumnBinding { SourceColumnIndex = 2, SourceHeader = "signed", TargetFieldName = "signed" },
            new ColumnBinding { SourceColumnIndex = 3, SourceHeader = "active", TargetFieldName = "active" },
            new ColumnBinding { SourceColumnIndex = 4, SourceHeader = "note", TargetFieldName = "note" },
        ],
    };

    private const string Header =
        """<row r="1"><c r="A1" t="inlineStr"><is><t>count</t></is></c><c r="B1" t="inlineStr"><is><t>amount</t></is></c><c r="C1" t="inlineStr"><is><t>signed</t></is></c><c r="D1" t="inlineStr"><is><t>active</t></is></c><c r="E1" t="inlineStr"><is><t>note</t></is></c></row>""";

    /// <summary>Style 1 is the built-in date format 14, so a number styled with it is a date.</summary>
    private const string Styles = """<cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs>""";

    private static List<ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)>> Import(string rows)
    {
        byte[] workbook = new XlsxPackage().WithSheet("S", Header + rows).WithStyles(Styles).Build();

        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);
        using ImportRun<(long?, decimal?, DateTime?, bool?, string?)> run = TabularImporter.Import(
            cursor,
            Plan,
            Schema,
            row => (row[Count], row[Amount], row[Signed], row[Active], row[Note]),
            cancellationToken: TestContext.Current.CancellationToken);

        return [.. run.Rows(TestContext.Current.CancellationToken)];
    }

    [Fact]
    public void ReadsEachTypedCellAsTheTypeTheFileDeclared()
    {
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            """<row r="2"><c r="A2"><v>42</v></c><c r="B2"><v>1234.5</v></c><c r="C2" s="1"><v>45000</v></c><c r="D2" t="b"><v>1</v></c><c r="E2"><v>7.25</v></c></row>"""));

        Assert.False(row.HasErrors);
        Assert.Equal((42L, 1234.5m, new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Unspecified), true, "7.25"), row.Value);
    }

    [Theory]
    [InlineData("42.5")]
    [InlineData("1E+30")]
    public void RefusesANumberThatIsNotAWholeOneForAnIntegerField(string number)
    {
        // Saturating 1e30 to long.MaxValue would write a value the file never contained.
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            $"""<row r="2"><c r="A2"><v>{number}</v></c></row>"""));

        RowError error = Assert.Single(row.Errors);
        Assert.Equal((ErrorCodes.Value.TypeMismatch, "count"), (error.Code, error.TargetFieldName));
    }

    [Fact]
    public void RefusesANumberBeyondDecimalsRangeForADecimalField()
    {
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            """<row r="2"><c r="B2"><v>1E+300</v></c></row>"""));

        Assert.Equal("amount", Assert.Single(row.Errors).TargetFieldName);
    }

    [Fact]
    public void RefusesAnErrorCellForATypedField()
    {
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            """<row r="2"><c r="A2" t="e"><v>#N/A</v></c></row>"""));

        RowError error = Assert.Single(row.Errors);
        Assert.Equal((ErrorCodes.Value.TypeMismatch, "count"), (error.Code, error.TargetFieldName));
    }
}
