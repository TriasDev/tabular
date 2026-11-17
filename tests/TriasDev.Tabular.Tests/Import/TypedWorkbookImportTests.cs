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
    private static readonly IntegerImportField Count = ImportField.Integer("count");
    private static readonly DecimalImportField Amount = ImportField.Decimal("amount");
    private static readonly DateImportField Signed = ImportField.Date("signed");
    private static readonly BooleanImportField Active = ImportField.Boolean("active");
    private static readonly TextImportField Note = ImportField.Text("note");

    private static readonly ImportSchema Schema = new() { Fields = [Count, Amount, Signed, Active, Note] };

    private static readonly MappingPlan Plan = new()
    {
        // Under de-DE on purpose: a typed cell must not be read through the culture at all, so a
        // decimal point in the file's own number cannot be mistaken for a group separator.
        Culture = "de-DE",
        Bindings =
        [
            new ColumnBinding { ColumnIndex = 0, Header = "count", FieldName = "count" },
            new ColumnBinding { ColumnIndex = 1, Header = "amount", FieldName = "amount" },
            new ColumnBinding { ColumnIndex = 2, Header = "signed", FieldName = "signed" },
            new ColumnBinding { ColumnIndex = 3, Header = "active", FieldName = "active" },
            new ColumnBinding { ColumnIndex = 4, Header = "note", FieldName = "note" },
        ],
    };

    private const string Header =
        """<row r="1"><c r="A1" t="inlineStr"><is><t>count</t></is></c><c r="B1" t="inlineStr"><is><t>amount</t></is></c><c r="C1" t="inlineStr"><is><t>signed</t></is></c><c r="D1" t="inlineStr"><is><t>active</t></is></c><c r="E1" t="inlineStr"><is><t>note</t></is></c></row>""";

    /// <summary>Style 1 is the built-in date format 14, so a number styled with it is a date.</summary>
    private const string Styles = """<cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14" applyNumberFormat="1"/></cellXfs>""";

    private static List<ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)>> Import(string rows)
    {
        byte[] workbook = new XlsxPackage()
            .WithSheet("S", Header + rows)
            .WithStyles(Styles)
            .WithSharedStrings("<si><t>from the table</t></si>")
            .Build();

        using XlsxCursor cursor = new(new MemoryStream(workbook), cancellationToken: TestContext.Current.CancellationToken);
        using ImportRun<(long?, decimal?, DateTime?, bool?, string?)> run = TabularImporter.Import(
            cursor,
            Plan,
            Schema,
            row => (row[Count], row[Amount], row[Signed], row[Active], row[Note]),
            cancellationToken: TestContext.Current.CancellationToken);

        return [.. run.ReadRows(TestContext.Current.CancellationToken)];
    }

    [Fact]
    public void ReadsEachTypedCellAsTheTypeTheFileDeclared()
    {
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            """<row r="2"><c r="A2"><v>42</v></c><c r="B2"><v>1234.5</v></c><c r="C2" s="1"><v>45000</v></c><c r="D2" t="b"><v>1</v></c><c r="E2"><v>7.25</v></c></row>"""));

        Assert.False(row.HasErrors);
        Assert.Equal((42L, 1234.5m, new DateTime(2023, 3, 15, 0, 0, 0, DateTimeKind.Unspecified), true, "7.25"), row.Value);
    }

    [Fact]
    public void ReadsAnIsoDateCellAndASharedString()
    {
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            """<row r="2"><c r="C2" t="d"><v>2026-09-25T14:30:00</v></c><c r="D2" t="b"><v>0</v></c><c r="E2" t="s"><v>0</v></c></row>"""));

        Assert.False(row.HasErrors);
        Assert.Equal(
            ((long?)null, (decimal?)null, new DateTime(2026, 9, 25, 14, 30, 0, DateTimeKind.Unspecified), false, "from the table"),
            row.Value);
    }

    [Theory]
    [InlineData("42.5")]
    [InlineData("1E+30")]
    [InlineData("9223372036854775808")]
    [InlineData("9.2233720368547758E+18")]
    public void RefusesANumberThatIsNotAWholeOneForAnIntegerField(string number)
    {
        // Saturating 1e30 to long.MaxValue would write a value the file never contained. 2^63 is the
        // edge of it: long.MaxValue itself is no double, so the comparison meets it rounded up to 2^63.
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            $"""<row r="2"><c r="A2"><v>{number}</v></c></row>"""));

        RowError error = Assert.Single(row.Errors);
        Assert.Equal((ErrorCodes.Value.TypeMismatch, "count"), (error.Code, error.FieldName));
    }

    [Theory]
    [InlineData("9223372036854774784", 9223372036854774784L)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("100000000000000000", 100000000000000000L)]
    public void ReadsTheWholeNumbersAtTheEdgesOfAnIntegerField(string number, long expected)
    {
        // The largest double below 2^63, and -2^63, which a long holds exactly. And 10^17, whose
        // invariant text is in exponent form ("1E+17"): only the number itself can fill an integer
        // field with it, since that text does not parse as an integer.
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            $"""<row r="2"><c r="A2"><v>{number}</v></c></row>"""));

        Assert.False(row.HasErrors);
        Assert.Equal(expected, row.Value.Item1);
    }

    [Fact]
    public void RefusesANumberBeyondDecimalsRangeForADecimalField()
    {
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            """<row r="2"><c r="B2"><v>1E+300</v></c></row>"""));

        Assert.Equal("amount", Assert.Single(row.Errors).FieldName);
    }

    [Theory]
    [InlineData("7.922816251426434E+28")]
    [InlineData("-7.922816251426434E+28")]
    public void RefusesANumberAtTheEdgeOfDecimalsRangeForADecimalField(string number)
    {
        // 2^96: decimal.MaxValue as a double rounds up to it, so the range check lets it through and
        // only the conversion finds it does not fit. It must be refused, never imported as 0.
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            $"""<row r="2"><c r="B2"><v>{number}</v></c></row>"""));

        RowError error = Assert.Single(row.Errors);
        Assert.Equal((ErrorCodes.Value.TypeMismatch, "amount"), (error.Code, error.FieldName));
    }

    [Theory]
    [InlineData("2026-09-25T14:30:00", "2026-09-25T14:30:00")]
    [InlineData("2026-09-25T00:00:00", "2026-09-25")]
    public void ReadsADateCellIntoATextFieldAsItsIsoText(string written, string expected)
    {
        // The time of day is kept, and written the same on every server, whatever its culture.
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            $"""<row r="2"><c r="E2" t="d"><v>{written}</v></c></row>"""));

        Assert.False(row.HasErrors);
        Assert.Equal(expected, row.Value.Item5);
    }

    [Fact]
    public void RefusesAnErrorCellForATypedField()
    {
        ImportOutcome<(long?, decimal?, DateTime?, bool?, string?)> row = Assert.Single(Import(
            """<row r="2"><c r="A2" t="e"><v>#N/A</v></c></row>"""));

        RowError error = Assert.Single(row.Errors);
        Assert.Equal((ErrorCodes.Value.TypeMismatch, "count"), (error.Code, error.FieldName));
    }
}
