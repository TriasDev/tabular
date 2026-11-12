using System.Globalization;

using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests.Import;

/// <summary>
/// A workbook's number cell holds a double; read as a decimal it keeps the 15 significant digits a
/// double holds, not the binary noise past them — on every runtime (#133).
/// </summary>
/// <remarks>
/// Up to .NET 10 the runtime's <c>(decimal)double</c> cast rounded to 15 digits. From .NET 11 it
/// converts the double's exact binary value, so the cast turned a cell holding <c>0.1</c> into
/// <c>0.1000000000000000055511151231</c>. The expectations here are literals, never a cast, so they
/// hold whatever the runtime does.
/// </remarks>
public sealed class WorkbookNumberTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Cell values as a spreadsheet writes them — up to 17 digits — and the decimal each reads as.</summary>
    private static readonly (string Cell, string Read)[] Numbers =
    [
        ("0.1", "0.1"),
        ("123456789.012345", "123456789.012345"),
        ("9.9999999999999995E-8", "0.0000001"),
        ("0.33333333333333331", "0.333333333333333"),
        ("-2.5", "-2.5"),
        ("1E+20", "100000000000000000000"),
        ("1234.5", "1234.5"),
    ];

    public static TheoryData<string> Formats => ["xlsx", "ods"];

    private static ITabularCursor Open(string format, IEnumerable<string> cells)
    {
        if (format == "xlsx")
        {
            string rows = """<row r="1"><c r="A1" t="inlineStr"><is><t>amount</t></is></c></row>"""
                + string.Concat(cells.Select((cell, i) => $"""<row r="{i + 2}"><c r="A{i + 2}"><v>{cell}</v></c></row>"""));

            return new XlsxCursor(new MemoryStream(new XlsxPackage().WithSheet("S", rows).Build()), cancellationToken: Token);
        }

        string table = """<table:table-row><table:table-cell office:value-type="string"><text:p>amount</text:p></table:table-cell></table:table-row>"""
            + string.Concat(cells.Select(cell =>
                $"""<table:table-row><table:table-cell office:value-type="float" office:value="{cell}"><text:p>{cell}</text:p></table:table-cell></table:table-row>"""));

        return new OdsCursor(new MemoryStream(new OdsPackage().WithTable("S", table).Build()), cancellationToken: Token);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ImportsANumberCellIntoADecimalFieldWithTheDigitsADoubleHolds(string format)
    {
        DecimalImportField amount = ImportField.Decimal("amount");
        using ITabularCursor cursor = Open(format, Numbers.Select(n => n.Cell));

        ImportResult<decimal?> result = TabularImporter.Import(
            cursor,
            new MappingPlan { Culture = "en-US", Bindings = [new ColumnBinding { ColumnIndex = 0, FieldName = "amount", Header = "amount" }] },
            new ImportSchema { Fields = [amount] },
            row => row[amount],
            cancellationToken: Token).ReadAll(cancellationToken: Token);

        Assert.Empty(result.Errors);
        // As text, so the scale is checked too: 0.1, not 0.10 or 0.1000000000000000055511151231.
        Assert.Equal(Numbers.Select(n => n.Read), result.Items.Select(v => v!.Value.ToString(CultureInfo.InvariantCulture)));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ProfilesTheExtremesOfANumberColumnWithTheDigitsADoubleHolds(string format)
    {
        using ITabularCursor cursor = Open(format, ["0.30000000000000004", "0.1", "0.2"]);

        ColumnFacts facts = TabularAnalyzer.Analyze(cursor, new AnalysisOptions { Cultures = ["en-US"] }, cancellationToken: Token)
            .Sheets[0].Columns[0].Facts;

        Assert.Equal("0.1", facts.MinNumeric?.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("0.3", facts.MaxNumeric?.ToString(CultureInfo.InvariantCulture));
    }
}
