using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Ods;
using TriasDev.Tabular.Tests.Fixtures;
using TriasDev.Tabular.Xlsx;

using Xunit;

namespace TriasDev.Tabular.Tests;

/// <summary>
/// An option that cannot work is refused where it is handed over, naming the option — not discovered
/// deep inside a read as a limit that is always exceeded.
/// </summary>
public sealed class OptionsValidationTests
{
    private static readonly TextField Name = ImportField.Text("name");

    private static readonly TargetSchema Schema = new() { Fields = [Name] };

    private static readonly MappingPlan Plan = new()
    {
        Bindings = [new ColumnBinding { SourceColumnIndex = 0, SourceHeader = "name", TargetFieldName = "name" }],
    };

    private static MemoryStream Csv() => new(Encoding.UTF8.GetBytes("name;x\na;b\n"), writable: false);

    public static TheoryData<CsvCursorOptions, string> BadCsvOptions => new()
    {
        { new CsvCursorOptions { MaxQuotedFieldLines = -1 }, nameof(CsvCursorOptions.MaxQuotedFieldLines) },
        { new CsvCursorOptions { MaxFieldChars = 0 }, nameof(CsvCursorOptions.MaxFieldChars) },
        { new CsvCursorOptions { DialectProbeBytes = -1 }, nameof(CsvCursorOptions.DialectProbeBytes) },
        { new CsvCursorOptions { MaxColumns = 0 }, nameof(CsvCursorOptions.MaxColumns) },
    };

    public static TheoryData<XlsxCursorOptions, string> BadXlsxOptions => new()
    {
        { new XlsxCursorOptions { MaxUncompressedBytes = 0 }, nameof(XlsxCursorOptions.MaxUncompressedBytes) },
        { new XlsxCursorOptions { MaxSharedStrings = -1 }, nameof(XlsxCursorOptions.MaxSharedStrings) },
        { new XlsxCursorOptions { MaxSharedStringChars = -1 }, nameof(XlsxCursorOptions.MaxSharedStringChars) },
        { new XlsxCursorOptions { MaxPackageEntries = 0 }, nameof(XlsxCursorOptions.MaxPackageEntries) },
        { new XlsxCursorOptions { MaxSheets = 0 }, nameof(XlsxCursorOptions.MaxSheets) },
        { new XlsxCursorOptions { MaxMetadataChars = 0 }, nameof(XlsxCursorOptions.MaxMetadataChars) },
        { new XlsxCursorOptions { MaxRelationships = 0 }, nameof(XlsxCursorOptions.MaxRelationships) },
        { new XlsxCursorOptions { MaxCellFormats = -1 }, nameof(XlsxCursorOptions.MaxCellFormats) },
        { new XlsxCursorOptions { MaxValueChars = 0 }, nameof(XlsxCursorOptions.MaxValueChars) },
    };

    public static TheoryData<OdsCursorOptions, string> BadOdsOptions => new()
    {
        { new OdsCursorOptions { MaxUncompressedBytes = 0 }, nameof(OdsCursorOptions.MaxUncompressedBytes) },
        { new OdsCursorOptions { MaxPackageEntries = 0 }, nameof(OdsCursorOptions.MaxPackageEntries) },
        { new OdsCursorOptions { MaxSheets = 0 }, nameof(OdsCursorOptions.MaxSheets) },
        { new OdsCursorOptions { MaxColumns = 0 }, nameof(OdsCursorOptions.MaxColumns) },
        { new OdsCursorOptions { MaxRows = 0 }, nameof(OdsCursorOptions.MaxRows) },
        { new OdsCursorOptions { MaxValueChars = 0 }, nameof(OdsCursorOptions.MaxValueChars) },
    };

    [Theory]
    [MemberData(nameof(BadOdsOptions))]
    public void RefusesAnOdsOptionThatCannotWork(OdsCursorOptions options, string option)
    {
        byte[] spreadsheet = new OdsPackage().WithTable("S", "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>a</text:p></table:table-cell></table:table-row>").Build();

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new OdsCursor(new MemoryStream(spreadsheet), options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadCsvOptions))]
    public void RefusesACsvOptionThatCannotWork(CsvCursorOptions options, string option)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => new CsvCursor(Csv(), "t.csv", options));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadXlsxOptions))]
    public void RefusesAnXlsxOptionThatCannotWork(XlsxCursorOptions options, string option)
    {
        byte[] workbook = new XlsxPackage().WithSheet("S", """<row r="1"><c r="A1" t="inlineStr"><is><t>a</t></is></c></row>""").Build();

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new XlsxCursor(new MemoryStream(workbook), options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnErrorCeilingOfNothing()
    {
        // Zero used to stop a run at its first failure, which is what AllOrNothing is for.
        using CsvCursor cursor = new(Csv(), "t.csv");

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => TabularExtractor.Start(
            cursor, Plan, Schema, new ExtractionOptions { MaxErrorRows = 0 }, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(ExtractionOptions.MaxErrorRows), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesANegativePreview()
    {
        using CsvCursor cursor = new(Csv(), "t.csv");

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => TabularImporter.Import(
            cursor, Plan, Schema, row => row[Name], new ImportOptions { PreviewRows = -1 }, TestContext.Current.CancellationToken));

        Assert.Contains(nameof(ImportOptions.PreviewRows), error.Message, StringComparison.Ordinal);
    }
}
